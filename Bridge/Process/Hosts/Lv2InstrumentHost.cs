using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// A self-contained LV2 instrument host.
//
// It supports the common instrument shape: atom input carrying midi:MidiEvent,
// audio output ports, scalar control ports, state/worker/options/time
// extensions and a Windows LV2 UI. The dependency-free implementation is
// enough for synths such as the LV2 builds of ZynAddSubFX, Calf, etc.
internal sealed unsafe class Lv2InstrumentHost : IBridgeInstrumentHost
{
    const int AtomBufferBytes = 64 * 1024;
    const int MaxEvents = 4096;

    readonly Dictionary<int, Slot> mSlots = [];
    SharedAudioRing? mOut;

    public void Attach(SharedAudioRing output) => mOut = output;

    static void Debug(string message)
    {
        if (Environment.GetEnvironmentVariable("TL_VSTI_DEBUG") == "1")
            Console.Error.WriteLine("vsti-lv2: " + message);
    }

    public InstrumentLoadOutcome Load(LoadPluginMessage request)
    {
        if (mSlots.ContainsKey(request.Slot))
            Unload(request.Slot);

        if (!Directory.Exists(request.Locator))
            return InstrumentLoadOutcome.Fail("LV2 bundle not found");

        var descriptions = Lv2Ttl.Describe(request.Locator);

        Debug("described " + descriptions.Count + " LV2 plugin(s) from " + request.Locator);

        if (descriptions.Count == 0)
            return InstrumentLoadOutcome.Fail("the LV2 bundle has no usable instrument description");

        var plugin = descriptions.FirstOrDefault(p =>
                string.Equals(p.Uri, request.PluginId, StringComparison.Ordinal))
            ?? descriptions.First();

        IntPtr library;

        try
        {
            Debug("loading " + plugin.BinaryPath);
            library = NativeLibrary.Load(plugin.BinaryPath);
            Debug("library loaded");
        }
        catch (Exception ex)
        {
            return InstrumentLoadOutcome.Fail("cannot load the LV2 binary: " + ex.Message);
        }

        try
        {
            if (!NativeLibrary.TryGetExport(library, "lv2_descriptor", out IntPtr entry))
            {
                NativeLibrary.Free(library);
                return InstrumentLoadOutcome.Fail("the LV2 binary has no lv2_descriptor export");
            }

            var descriptorFn = (delegate* unmanaged[Cdecl]<uint, IntPtr>)entry;
            IntPtr descriptor = IntPtr.Zero;
            var seenUris = new List<string>();

            // Bundles such as LSP expose hundreds of plugins from one binary.
            // The list is null-terminated; this cap only protects against a
            // broken descriptor function that never returns null.
            for (uint index = 0; index < 4096; index++)
            {
                IntPtr candidate = descriptorFn(index);

                if (candidate == IntPtr.Zero)
                    break;

                string uri = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(candidate)) ?? string.Empty;
                seenUris.Add(uri);

                if (string.Equals(uri, plugin.Uri, StringComparison.Ordinal))
                {
                    descriptor = candidate;
                    break;
                }
            }

            if (descriptor == IntPtr.Zero)
            {
                NativeLibrary.Free(library);
                string seen = seenUris.Count == 0
                    ? "none"
                    : string.Join(", ", seenUris.Take(8)) + (seenUris.Count > 8 ? ", ..." : "");
                return InstrumentLoadOutcome.Fail(
                    "the LV2 descriptor for '" + plugin.Uri + "' was not found in '" +
                    plugin.BinaryPath + "' (descriptors: " + seenUris.Count + "; " + seen + ")");
            }

            Debug("descriptor found");

            int block = Math.Clamp(request.MaxBlock, 32, SharedAudioLayout.MaxBlockFrames);
            var worker = new Lv2Worker();
            var featureSet = new Lv2FeatureSet(block, worker);
            double sampleRate = request.SampleRate > 0 ? request.SampleRate : 48000;

            var slot = new Slot
            {
                Library = library,
                Descriptor = descriptor,
                FeatureSet = featureSet,
                Worker = worker,
                Plugin = plugin,
                UiBinary = !string.IsNullOrEmpty(plugin.UiBinaryPath) && File.Exists(plugin.UiBinaryPath)
                    ? plugin.UiBinaryPath
                    : FindUiBinary(plugin.BundlePath, plugin.BinaryPath,
                        NativeLibrary.TryGetExport(library, "lv2ui_descriptor", out _)),
                Frames = block,
                SampleRate = sampleRate,
                Ports = [],
            };

            try
            {
                BuildPorts(slot);
                Debug("ports built");

                var descriptorPtr = (Lv2Native.Descriptor*)descriptor;
                slot.State = Lv2State.GetInterface(descriptor, descriptorPtr->ExtensionData);

                var instantiate = (delegate* unmanaged[Cdecl]<IntPtr, double, IntPtr, IntPtr, IntPtr>)
                    descriptorPtr->Instantiate;

                if (instantiate == null)
                {
                    slot.Dispose();
                    return InstrumentLoadOutcome.Fail("the LV2 descriptor has no instantiate callback");
                }

                IntPtr bundlePath = Marshal.StringToCoTaskMemUTF8(plugin.BundlePath);
                Debug("calling instantiate");

                try
                {
                    slot.Handle = instantiate(descriptor, sampleRate, bundlePath, featureSet.Features);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(bundlePath);
                }

                if (slot.Handle == IntPtr.Zero)
                {
                    slot.Dispose();
                    return InstrumentLoadOutcome.Fail("the LV2 plugin refused to instantiate");
                }

                Debug("instantiated");
                slot.Worker.Attach(slot.Handle, descriptorPtr->ExtensionData);

                slot.Connect = (delegate* unmanaged[Cdecl]<IntPtr, uint, void*, void>)
                    descriptorPtr->ConnectPort;
                slot.Activate = (delegate* unmanaged[Cdecl]<IntPtr, void>)
                    descriptorPtr->Activate;
                slot.Run = (delegate* unmanaged[Cdecl]<IntPtr, uint, void>)
                    descriptorPtr->Run;
                slot.Deactivate = (delegate* unmanaged[Cdecl]<IntPtr, void>)
                    descriptorPtr->Deactivate;
                slot.Cleanup = (delegate* unmanaged[Cdecl]<IntPtr, void>)
                    descriptorPtr->Cleanup;

                if (slot.Connect == null || slot.Run == null)
                {
                    slot.Dispose();
                    return InstrumentLoadOutcome.Fail("the LV2 descriptor has no connect_port/run callback");
                }

                foreach (var port in slot.Ports.Values)
                {
                    slot.Connect(slot.Handle, (uint)port.Description.Index, (void*)port.Buffer);
                }

                if (!string.IsNullOrEmpty(request.State) && slot.State != null)
                {
                    try
                    {
                        byte[] stateBytes = Convert.FromBase64String(request.State);
                        Lv2State.Restore(slot.Handle, slot.State.Value, stateBytes, slot.FeatureSet.Features);
                    }
                    catch (Exception)
                    {
                    }
                }

                if (slot.Activate != null)
                    slot.Activate(slot.Handle);
                slot.Activated = true;
                Debug("activated");

                mSlots[request.Slot] = slot;

                int audioOut = slot.Ports.Values.Count(p => p.Description.IsOutput && p.Description.IsAudio);
                int audioIn = slot.Ports.Values.Count(p => p.Description.IsInput && p.Description.IsAudio);

                return new InstrumentLoadOutcome
                {
                    Ok = true,
                    Name = plugin.Name,
                    Vendor = plugin.Vendor,
                    IsInstrument = true,
                    InputChannels = audioIn,
                    OutputChannels = audioOut,
                    LatencySamples = 0,
                    TailSamples = 0,
                    HasEditor = slot.UiBinary != null,
                    ParameterCount = slot.ControlParameters.Count,
                };
            }
            catch
            {
                slot.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            try
            {
                NativeLibrary.Free(library);
            }
            catch (Exception)
            {
            }

            return InstrumentLoadOutcome.Fail("unexpected LV2 load error: " + ex.Message);
        }
    }

    static string? FindUiBinary(string bundle, string pluginBinary, bool pluginHasUi)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories))
            {
                string extension = Path.GetExtension(file);

                if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".so", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(pluginBinary),
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (pluginHasUi)
                        return file;

                    continue;
                }

                IntPtr library = IntPtr.Zero;

                try
                {
                    library = NativeLibrary.Load(file);

                    if (NativeLibrary.TryGetExport(library, "lv2ui_descriptor", out _))
                        return file;
                }
                catch (Exception)
                {
                }
                finally
                {
                    if (library != IntPtr.Zero)
                    {
                        try
                        {
                            NativeLibrary.Free(library);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    static void BuildPorts(Slot slot)
    {
        int frames = slot.Frames;
        int controlIndex = 0;

        foreach (var description in slot.Plugin.Ports.OrderBy(p => p.Index))
        {
            int bytes;

            if (description.IsAudio)
                bytes = Math.Max(1, frames) * sizeof(float);
            else if (description.IsAtom)
                bytes = AtomBufferBytes;
            else
                bytes = sizeof(float);

            IntPtr buffer = Marshal.AllocHGlobal(bytes);
            new Span<byte>((void*)buffer, bytes).Clear();

            var port = new PortRuntime
            {
                Description = description,
                Buffer = buffer,
                Bytes = bytes,
            };

            if (description.IsControl && description.IsInput)
            {
                float value = (float)description.Default;
                *(float*)buffer = value;

                port.ParameterIndex = controlIndex++;
                slot.ControlParameters[description.Index] = port;
            }

            if (description.IsAtom && description.IsInput)
            {
                port.AtomSequence = buffer;

                if (description.SupportsTime)
                    slot.TimeInputs.Add(port);
                else
                    slot.AtomInputs.Add(port);
            }
            else if (description.IsAtom && description.IsOutput)
            {
                port.AtomSequence = buffer;
                slot.AtomOutputs.Add(port);
            }

            if (description.IsAudio && description.IsOutput)
                slot.AudioOutputs.Add(port);

            slot.Ports[description.Index] = port;
        }

        // Publish buffers are always the first two output audio planes.
        slot.PublishPlanes =
        [
            new float[SharedAudioLayout.MaxBlockFrames],
            new float[SharedAudioLayout.MaxBlockFrames],
        ];
    }

    public bool Unload(int slotNumber)
    {
        if (!mSlots.Remove(slotNumber, out var slot))
            return false;

        slot.Dispose();
        return true;
    }

    public bool Process(int slotNumber, ProcessRequest request, out string? reason)
    {
        reason = null;

        if (!mSlots.TryGetValue(slotNumber, out var slot))
        {
            reason = "the slot is not loaded";
            return false;
        }

        int frames = Math.Min(request.Frames, slot.Frames);

        if (frames <= 0)
        {
            reason = "the render request has no frames";
            return false;
        }

        if (request.Bypass)
        {
            PublishSilence(slotNumber, slot, frames, request.Tag);
            return true;
        }

        try
        {
            foreach (var parameter in request.Parameters)
            {
                if (!slot.ControlParameters.TryGetValue((int)parameter.Id, out var port))
                    continue;

                double value = Math.Clamp(parameter.Value, 0, 1);
                double actual = port.Description.Minimum
                    + value * (port.Description.Maximum - port.Description.Minimum);
                *(float*)port.Buffer = (float)actual;
                slot.Editor?.NotifyPortEvent((uint)port.Description.Index, (float)actual);
            }

            foreach (var output in slot.AudioOutputs)
                new Span<byte>((void*)output.Buffer, frames * sizeof(float)).Clear();

            foreach (var output in slot.AtomOutputs)
            {
                var sequence = (Lv2Native.AtomSequence*)output.Buffer;
                sequence->Atom.Size = 0;
                sequence->Atom.Type = 0;
                sequence->EventCount = 0;
            }

            var midi = new List<MidiEvent>(request.Events.Length + slot.PendingEvents.Count);
            midi.AddRange(slot.PendingEvents);
            midi.AddRange(request.Events);
            slot.PendingEvents.Clear();

            FillAtomInputs(slot, midi, frames);
            FillTimeInputs(slot, request.Transport, frames);

            slot.Worker.DrainResponses();

            try
            {
                slot.Run(slot.Handle, (uint)frames);
            }
            finally
            {
                slot.Worker.EndRun();
            }

            PublishAudio(slotNumber, slot, frames, request.Tag);
            return true;
        }
        catch (Exception ex)
        {
            reason = "the LV2 plugin threw during processing: " + ex.Message;
            PublishSilence(slotNumber, slot, frames, request.Tag);
            return false;
        }
    }

    void FillAtomInputs(Slot slot, IReadOnlyList<MidiEvent> events, int frames)
    {
        if (slot.AtomInputs.Count == 0)
            return;

        uint sequenceType = slot.FeatureSet.Urid(Lv2Native.AtomSequenceUri);
        uint midiType = slot.FeatureSet.Urid(Lv2Native.MidiEventUri);
        int eventSize = sizeof(Lv2Native.AtomEvent);

        foreach (var port in slot.AtomInputs)
        {
            IntPtr buffer = port.Buffer;
            new Span<byte>((void*)buffer, AtomBufferBytes).Clear();

            var sequence = (Lv2Native.AtomSequence*)buffer;
            sequence->Atom.Type = sequenceType;
            sequence->Atom.Size = sizeof(uint); // event_count
            sequence->EventCount = 0;

            IntPtr cursor = buffer + sizeof(Lv2Native.AtomSequence) + sizeof(uint);

            foreach (var source in events)
            {
                if (sequence->EventCount >= MaxEvents ||
                    cursor.ToInt64() - buffer.ToInt64() + eventSize + 3 > AtomBufferBytes)
                {
                    break;
                }

                var target = (Lv2Native.AtomEvent*)cursor;
                target->Frames = Math.Clamp(source.Offset, 0, Math.Max(0, frames - 1));
                target->Subframes = 0;
                target->Type = midiType;
                target->Size = 3;

                byte* data = (byte*)target + eventSize;
                byte status = MidiStatus(source);

                data[0] = status;
                data[1] = (byte)Math.Clamp(MidiData1(source), 0, 127);
                data[2] = (byte)Math.Clamp(MidiData2(source), 0, 127);

                sequence->Atom.Size += (uint)(eventSize + 3);
                sequence->EventCount++;
                cursor += eventSize + 3;
            }
        }
    }

    void FillTimeInputs(Slot slot, TransportInfo? transport, int frames)
    {
        if (slot.TimeInputs.Count == 0)
            return;

        uint objectType = slot.FeatureSet.Urid(Lv2Native.AtomObjectUri);
        uint positionType = slot.FeatureSet.Urid(Lv2Native.TimePositionUri);
        uint longType = slot.FeatureSet.Urid(Lv2Native.AtomLongUri);
        uint floatType = slot.FeatureSet.Urid(Lv2Native.AtomFloatUri);
        uint intType = slot.FeatureSet.Urid(Lv2Native.AtomIntUri);

        double tempo = transport?.Tempo ?? 120.0;
        int timeSigNum = Math.Max(1, transport?.TimeSigNum ?? 4);
        int timeSigDen = Math.Max(1, transport?.TimeSigDen ?? 4);
        long samplePosition = transport?.SamplePosition ?? 0;
        double ppq = transport?.PpqPosition ?? 0;
        double barBeat = ppq % timeSigNum;
        float speed = transport?.Playing == true ? 1f : 0f;

        foreach (var port in slot.TimeInputs)
        {
            IntPtr buffer = port.Buffer;
            new Span<byte>((void*)buffer, AtomBufferBytes).Clear();

            var atom = (Lv2Native.Atom*)buffer;
            atom->Type = objectType;
            atom->Size = 16; // id + otype

            uint* body = (uint*)(buffer + sizeof(Lv2Native.Atom));
            body[0] = 0;
            body[1] = positionType;

            IntPtr cursor = buffer + sizeof(Lv2Native.Atom) + 8;

            WriteProperty(buffer, ref cursor, slot, Lv2Native.TimeFrameUri, longType, 8,
                (void*)&samplePosition);
            float speedValue = speed;
            WriteProperty(buffer, ref cursor, slot, Lv2Native.TimeSpeedUri, floatType, 4,
                (void*)&speedValue);
            float bpmValue = (float)tempo;
            WriteProperty(buffer, ref cursor, slot, Lv2Native.TimeBeatsPerMinuteUri, floatType, 4,
                (void*)&bpmValue);
            uint beatUnit = (uint)timeSigDen;
            WriteProperty(buffer, ref cursor, slot, Lv2Native.TimeBeatUnitUri, intType, 4,
                (void*)&beatUnit);
            float beatsPerBar = timeSigNum;
            WriteProperty(buffer, ref cursor, slot, Lv2Native.TimeBeatsPerBarUri, floatType, 4,
                (void*)&beatsPerBar);
            float barBeatValue = (float)barBeat;
            WriteProperty(buffer, ref cursor, slot, Lv2Native.TimeBarBeatUri, floatType, 4,
                (void*)&barBeatValue);

            atom->Size = (uint)(cursor.ToInt64() - buffer.ToInt64() - sizeof(Lv2Native.Atom));
        }
    }

    void WriteProperty(IntPtr buffer, ref IntPtr cursor, Slot slot, string keyUri, uint type,
        uint size, void* value)
    {
        uint key = slot.FeatureSet.Urid(keyUri);
        *(uint*)cursor = key;
        cursor += 4;
        *(uint*)cursor = 0; // context
        cursor += 4;
        var valueAtom = (Lv2Native.Atom*)cursor;
        valueAtom->Size = size;
        valueAtom->Type = type;
        cursor += sizeof(Lv2Native.Atom);

        if (size > 0)
        {
            Buffer.MemoryCopy(value, (void*)cursor, size, size);
            cursor += (int)size;
        }
    }

    static byte MidiStatus(MidiEvent source)
    {
        int channel = source.Channel & 0x0F;

        return source.Type switch
        {
            MidiEventKind.NoteOn => (byte)(0x90 | channel),
            MidiEventKind.NoteOff => (byte)(0x80 | channel),
            MidiEventKind.ControlChange => (byte)(0xB0 | channel),
            MidiEventKind.PitchBend => (byte)(0xE0 | channel),
            MidiEventKind.ProgramChange => (byte)(0xC0 | channel),
            MidiEventKind.ChannelPressure => (byte)(0xD0 | channel),
            MidiEventKind.PolyPressure => (byte)(0xA0 | channel),
            MidiEventKind.AllNotesOff => (byte)(0xB0 | channel),
            MidiEventKind.AllSoundOff => (byte)(0xB0 | channel),
            MidiEventKind.ResetControllers => (byte)(0xB0 | channel),
            _ => (byte)(0x90 | channel),
        };
    }

    static int MidiData1(MidiEvent source)
    {
        return source.Type switch
        {
            MidiEventKind.NoteOn or MidiEventKind.NoteOff => source.Note,
            MidiEventKind.ControlChange => source.Controller,
            MidiEventKind.PitchBend => source.Value & 0x7F,
            MidiEventKind.ProgramChange => source.Program,
            MidiEventKind.ChannelPressure => source.Value,
            MidiEventKind.PolyPressure => source.Note,
            MidiEventKind.AllNotesOff => 123,
            MidiEventKind.AllSoundOff => 120,
            MidiEventKind.ResetControllers => 121,
            _ => 0,
        };
    }

    static int MidiData2(MidiEvent source)
    {
        return source.Type switch
        {
            MidiEventKind.NoteOn or MidiEventKind.NoteOff => source.Velocity,
            MidiEventKind.ControlChange => source.Value,
            MidiEventKind.PitchBend => (source.Value >> 7) & 0x7F,
            MidiEventKind.PolyPressure => source.Value,
            _ => 0,
        };
    }

    void PublishAudio(int slotNumber, Slot slot, int frames, int tag)
    {
        int count = Math.Min(2, slot.AudioOutputs.Count);

        if (count == 0)
        {
            PublishSilence(slotNumber, slot, frames, tag);
            return;
        }

        float* left = (float*)slot.AudioOutputs[0].Buffer;
        float* right = count > 1 ? (float*)slot.AudioOutputs[1].Buffer : left;

        for (int i = 0; i < frames; i++)
        {
            float l = left[i];
            float r = right[i];

            slot.PublishPlanes[0][i] = float.IsFinite(l) ? l : 0f;
            slot.PublishPlanes[1][i] = float.IsFinite(r) ? r : 0f;
        }

        mOut?.TryPublish(slotNumber, slot.PublishPlanes, 2, frames, 1000, tag);
    }

    void PublishSilence(int slotNumber, Slot slot, int frames, int tag)
    {
        Array.Clear(slot.PublishPlanes[0], 0, frames);
        Array.Clear(slot.PublishPlanes[1], 0, frames);
        mOut?.TryPublish(slotNumber, slot.PublishPlanes, 2, frames, 1000, tag);
    }

    public byte[]? GetState(int slotNumber)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot) || slot.State == null)
            return null;

        return Lv2State.Save(slot.Handle, slot.State.Value, slot.FeatureSet.Features);
    }

    public bool SetState(int slotNumber, string base64)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot) || slot.State == null)
            return false;

        try
        {
            byte[] data = Convert.FromBase64String(base64);
            return Lv2State.Restore(slot.Handle, slot.State.Value, data, slot.FeatureSet.Features);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public List<ParameterInfo> GetParameters(int slotNumber)
    {
        var result = new List<ParameterInfo>();

        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return result;

        foreach (var port in slot.ControlParameters.Values.OrderBy(p => p.Description.Index))
        {
            double range = port.Description.Maximum - port.Description.Minimum;

            if (!double.IsFinite(range) || range <= 0)
                continue;

            double actual = *(float*)port.Buffer;

            result.Add(new ParameterInfo
            {
                Id = port.Description.Index,
                Name = port.Description.Name,
                Min = 0,
                Max = 1,
                Value = Math.Clamp((actual - port.Description.Minimum) / range, 0, 1),
                DefaultValue = Math.Clamp((port.Description.Default - port.Description.Minimum) / range, 0, 1),
                Steps = 0,
                IsAutomatable = true,
            });
        }

        return result;
    }

    public bool SetParameter(int slotNumber, long id, double value)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        if (!slot.ControlParameters.TryGetValue((int)id, out var port))
            return false;

        double clamped = Math.Clamp(value, 0, 1);
        double actual = port.Description.Minimum
            + clamped * (port.Description.Maximum - port.Description.Minimum);
        *(float*)port.Buffer = (float)actual;
        slot.Editor?.NotifyPortEvent((uint)port.Description.Index, (float)actual);
        return true;
    }

    public bool Panic(int slotNumber)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        for (int channel = 0; channel < 16; channel++)
        {
            slot.PendingEvents.Add(new MidiEvent
            {
                Type = MidiEventKind.ControlChange,
                Channel = channel,
                Controller = 123,
                Value = 0,
            });
            slot.PendingEvents.Add(new MidiEvent
            {
                Type = MidiEventKind.ControlChange,
                Channel = channel,
                Controller = 120,
                Value = 0,
            });
            slot.PendingEvents.Add(new MidiEvent
            {
                Type = MidiEventKind.ControlChange,
                Channel = channel,
                Controller = 121,
                Value = 0,
            });
        }

        return true;
    }

    public bool Reset(int slotNumber)
    {
        if (!mSlots.ContainsKey(slotNumber))
            return false;

        return Panic(slotNumber);
    }

    public (bool Ok, string? Reason) OpenEditor(int slotNumber, string title)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return (false, "slot not found");

        if (slot.UiBinary == null)
            return (false, "the LV2 bundle has no UI binary");

        if (slot.Editor is { IsAlive: true })
        {
            slot.Editor.BringToFront();
            return (true, null);
        }

        slot.Editor?.Dispose();
        slot.Editor = null;

        ILv2EditorWindow? editor = null;

        if (OperatingSystem.IsWindows())
        {
            if (Lv2EditorWindow.TryOpen(slot, title, out var windowsEditor))
                editor = windowsEditor;
        }
        else if (OperatingSystem.IsLinux())
        {
            // Gtk3/Qt5 UIs need their own native toolkit host. X11UI is the
            // direct fallback and remains the common case for LSP etc.
            if (Lv2SuilEditorWindow.TryOpen(slot, title, out var suilEditor))
                editor = suilEditor;
            else if (Lv2X11EditorWindow.TryOpen(slot, title, out var x11Editor))
                editor = x11Editor;
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (Lv2CocoaEditorWindow.TryOpen(slot, title, out var cocoaEditor))
                editor = cocoaEditor;
        }

        if (editor == null)
            return (false, "the LV2 UI refused to open or this platform has no UI host");

        slot.Editor = editor;
        return (true, null);
    }

    public bool CloseEditor(int slotNumber)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        slot.Editor?.Dispose();
        slot.Editor = null;
        return true;
    }

    public List<PluginInfo> Scan(ScanPluginsMessage request)
    {
        var result = new List<PluginInfo>();

        foreach (string bundle in request.Paths)
        {
            try
            {
                var descriptions = Lv2Ttl.Describe(bundle);

                if (descriptions.Count == 0)
                {
                    result.Add(Unavailable(bundle, "the LV2 bundle has no usable instrument"));
                    continue;
                }

                foreach (var plugin in descriptions)
                {
                    result.Add(new PluginInfo
                    {
                        Format = PluginFormats.Lv2,
                        PluginId = plugin.Uri,
                        Name = plugin.Name,
                        Vendor = plugin.Vendor,
                        Category = "Instrument",
                        Locator = bundle,
                        IsInstrument = true,
                        IsAvailable = true,
                        Bits = Environment.Is64BitProcess ? 64 : 32,
                        Arch = BridgeIdentity.Arch,
                        Rid = BridgeIdentity.Rid,
                    });
                }
            }
            catch (Exception ex)
            {
                result.Add(Unavailable(bundle, "scanning the LV2 bundle failed: " + ex.Message));
            }
        }

        return result;
    }

    static PluginInfo Unavailable(string bundle, string reason) => new()
    {
        Format = PluginFormats.Lv2,
        PluginId = string.Empty,
        Name = Path.GetFileNameWithoutExtension(bundle),
        Locator = bundle,
        IsInstrument = false,
        IsAvailable = false,
        UnavailableReason = reason,
        Bits = Environment.Is64BitProcess ? 64 : 32,
        Arch = BridgeIdentity.Arch,
        Rid = BridgeIdentity.Rid,
    };

    internal sealed class Slot : IDisposable
    {
        public IntPtr Library;
        public IntPtr Descriptor;
        public IntPtr Handle;
        public Lv2FeatureSet FeatureSet = null!;
        public Lv2Worker Worker = null!;
        public Lv2PluginDescription Plugin = null!;
        public string? UiBinary;
        public ILv2EditorWindow? Editor;
        public int Frames;
        public double SampleRate;
        public bool Activated;
        public Lv2State.Interface? State;
        public Dictionary<int, PortRuntime> Ports = [];
        public Dictionary<int, PortRuntime> ControlParameters = [];
        public List<PortRuntime> AudioOutputs = [];
        public List<PortRuntime> AtomInputs = [];
        public List<PortRuntime> TimeInputs = [];
        public List<PortRuntime> AtomOutputs = [];
        public List<MidiEvent> PendingEvents = [];
        public float[][] PublishPlanes = [];

        public delegate* unmanaged[Cdecl]<IntPtr, uint, void*, void> Connect;
        public delegate* unmanaged[Cdecl]<IntPtr, void> Activate;
        public delegate* unmanaged[Cdecl]<IntPtr, uint, void> Run;
        public delegate* unmanaged[Cdecl]<IntPtr, void> Deactivate;
        public delegate* unmanaged[Cdecl]<IntPtr, void> Cleanup;

        public void Dispose()
        {
            try
            {
                Editor?.Dispose();
            }
            catch (Exception)
            {
            }

            try
            {
                Worker?.Dispose();
            }
            catch (Exception)
            {
            }

            if (Handle != IntPtr.Zero)
            {
                try
                {
                    if (Activated && Deactivate != null)
                        Deactivate(Handle);
                }
                catch (Exception)
                {
                }

                try
                {
                    if (Cleanup != null)
                        Cleanup(Handle);
                }
                catch (Exception)
                {
                }
            }

            foreach (var port in Ports.Values)
            {
                if (port.Buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(port.Buffer);

                port.Buffer = IntPtr.Zero;
            }

            Ports.Clear();
            ControlParameters.Clear();
            AudioOutputs.Clear();
            AtomInputs.Clear();
            TimeInputs.Clear();
            AtomOutputs.Clear();
            PendingEvents.Clear();

            try
            {
                FeatureSet?.Dispose();
            }
            catch (Exception)
            {
            }

            if (Library != IntPtr.Zero)
            {
                try
                {
                    NativeLibrary.Free(Library);
                }
                catch (Exception)
                {
                }
            }

            Library = IntPtr.Zero;
            Handle = IntPtr.Zero;
            Descriptor = IntPtr.Zero;
        }
    }

    internal sealed class PortRuntime
    {
        public Lv2PortDescription Description = null!;
        public IntPtr Buffer;
        public int Bytes;
        public int ParameterIndex;
        public IntPtr AtomSequence;
    }
}