using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// VST2.4 instrument host inside the helper.
//
// A VST2 instrument is an AEffect with effFlagsIsSynth, zero (or more) audio
// inputs, at least one output, and an event input via effProcessEvents. The
// renderer sends MIDI events and reads processReplacing's output.
//
// The host is deliberately small: open, sample rate, block size, mains changed,
// process events, process replacing, chunks/parameters. No editor and no
// vendor-specific extensions in this build.
internal sealed unsafe class Vst2InstrumentHost : IBridgeInstrumentHost
{
    readonly Dictionary<int, Slot> mSlots = [];
    SharedAudioRing? mOut;

    // The audioMaster callback is static, so it needs the slot whose call is in
    // flight. The bridge is single-threaded, which makes this safe.
    static Slot? sCurrent;

    // VstTimeInfo is a pure value struct; allocate it once and refresh fields.
    static IntPtr sTimeInfo;

    static readonly Vst2Native.VstHostCallbackProc sHostCallback = HostCallback;
    static readonly IntPtr sHostCallbackPointer = Marshal.GetFunctionPointerForDelegate(sHostCallback);

    public void Attach(SharedAudioRing output) => mOut = output;

    // ── Load / unload ──

    public InstrumentLoadOutcome Load(LoadPluginMessage request)
    {
        if (mSlots.ContainsKey(request.Slot))
            Unload(request.Slot);

        string? binary = ResolveBinary(request.Locator);

        if (binary == null)
            return InstrumentLoadOutcome.Fail("plugin file not found");

        IntPtr library;

        try
        {
            library = NativeLibrary.Load(binary);
        }
        catch (Exception ex)
        {
            return InstrumentLoadOutcome.Fail("cannot load the plugin library: " + ex.Message);
        }

        if (!NativeLibrary.TryGetExport(library, "VSTPluginMain", out IntPtr entry))
        {
            NativeLibrary.Free(library);
            return InstrumentLoadOutcome.Fail("the file is not a VST2 plugin (no VSTPluginMain export)");
        }

        var slot = new Slot
        {
            Library = library,
            SampleRate = request.SampleRate > 0 ? request.SampleRate : 48000,
            Frames = Math.Clamp(request.MaxBlock, 64, SharedAudioLayout.MaxBlockFrames),
        };

        sCurrent = slot;

        try
        {
            // The plugin asks the host for its version and sample rate from
            // inside VSTPluginMain, so those values must already be available.
            IntPtr effect;

            var main = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)entry;
            effect = main(sHostCallbackPointer);

            if (effect == IntPtr.Zero)
                return Fail(slot, "the plugin refused to create its AEffect");

            if (Marshal.ReadInt32(effect, 0) != Vst2Native.Magic)
                return Fail(slot, "the returned pointer is not a VST2 AEffect");

            var layout = Vst2Native.AEffectLayout.ForPointerSize(IntPtr.Size);

            slot.Effect = effect;
            slot.NumPrograms = Math.Clamp(Marshal.ReadInt32(effect, layout.NumPrograms), 0, 4096);
            slot.NumParams = Math.Clamp(Marshal.ReadInt32(effect, layout.NumParams), 0, 4096);
            slot.NumInputs = Math.Clamp(Marshal.ReadInt32(effect, layout.NumInputs), 0, 64);
            int rawOutputs = Marshal.ReadInt32(effect, layout.NumOutputs);
            slot.NumOutputs = Math.Clamp(rawOutputs, 1, 64);
            slot.Flags = Marshal.ReadInt32(effect, layout.Flags);
            slot.IsSynth = (slot.Flags & Vst2Native.EffFlagsIsSynth) != 0;
            // Some generators do not set effFlagsIsSynth. The explicit marker
            // wins; with no marker, "no audio input + audio output" is an
            // instrument by definition.
            slot.IsInstrument = slot.IsSynth || (slot.NumInputs == 0 && rawOutputs > 0);
            slot.HasEditor = (slot.Flags & Vst2Native.EffFlagsHasEditor) != 0;
            slot.ProgramChunks = (slot.Flags & Vst2Native.EffFlagsProgramChunks) != 0;
            slot.LatencySamples = Math.Max(0, Marshal.ReadInt32(effect, layout.InitialDelay));

            // The dispatcher must exist before any early return so Fail can
            // call effClose before the library is freed.
            slot.Dispatcher = (Vst2Native.VstDispatcherProc)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(effect, layout.Dispatcher), typeof(Vst2Native.VstDispatcherProc));
            slot.SetParameter = (Vst2Native.VstSetParameterProc)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(effect, layout.SetParameter), typeof(Vst2Native.VstSetParameterProc));
            slot.GetParameter = (Vst2Native.VstGetParameterProc)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(effect, layout.GetParameter), typeof(Vst2Native.VstGetParameterProc));

            if (request.InstrumentOnly && !slot.IsInstrument)
                return Fail(slot, "not an instrument");

            if (rawOutputs <= 0)
                return Fail(slot, "the plugin has no audio output");

            IntPtr processEntry = Marshal.ReadIntPtr(effect, layout.ProcessReplacing);

            if (processEntry == IntPtr.Zero)
                processEntry = Marshal.ReadIntPtr(effect, layout.Process);

            if (processEntry == IntPtr.Zero)
                return Fail(slot, "the plugin exposes no audio processing entry point");

            slot.ProcessReplacing = (Vst2Native.VstProcessProc)Marshal.GetDelegateForFunctionPointer(
                processEntry, typeof(Vst2Native.VstProcessProc));

            // Standard VST2 initialization order. Skipping mainsChanged leaves
            // some plugins with unallocated internal buffers.
            Vst2Native.Call(slot.Dispatcher, effect, Vst2Native.EffOpen, 0, IntPtr.Zero, IntPtr.Zero, 0);
            Vst2Native.Call(slot.Dispatcher, effect, Vst2Native.EffSetSampleRate, 0,
                IntPtr.Zero, IntPtr.Zero, (float)slot.SampleRate);
            Vst2Native.Call(slot.Dispatcher, effect, Vst2Native.EffSetBlockSize, 0,
                (IntPtr)slot.Frames, IntPtr.Zero, 0);
            Vst2Native.Call(slot.Dispatcher, effect, Vst2Native.EffMainsChanged, 0,
                (IntPtr)1, IntPtr.Zero, 0);

            slot.TailSamples = (int)Math.Clamp(
                Vst2Native.Call(slot.Dispatcher, effect, Vst2Native.EffGetTailSize, 0,
                    IntPtr.Zero, IntPtr.Zero, 0).ToInt64(), 0, int.MaxValue);

            slot.InPlanes = new float[Math.Max(slot.NumInputs, 1)][];
            slot.OutPlanes = new float[Math.Max(slot.NumOutputs, 1)][];

            for (int channel = 0; channel < slot.InPlanes.Length; channel++)
                slot.InPlanes[channel] = new float[slot.Frames];

            for (int channel = 0; channel < slot.OutPlanes.Length; channel++)
                slot.OutPlanes[channel] = new float[slot.Frames];

            slot.PublishPlanes =
            [
                new float[SharedAudioLayout.MaxBlockFrames],
                new float[SharedAudioLayout.MaxBlockFrames],
            ];

            slot.EventMemory = Marshal.AllocHGlobal(
                sizeof(Vst2Native.VstEventsHeader) + Slot.MaxEvents * IntPtr.Size);
            slot.MidiMemory = Marshal.AllocHGlobal(Slot.MaxEvents * sizeof(Vst2Native.VstMidiEvent));

            slot.Started = true;
            Vst2Native.Call(slot.Dispatcher, effect, Vst2Native.EffStartProcess, 0,
                IntPtr.Zero, IntPtr.Zero, 0);

            slot.Processing = true;

            string name = Vst2Native.ReadString(slot.Dispatcher, effect, Vst2Native.EffGetProductString);
            if (string.IsNullOrEmpty(name))
                name = Vst2Native.ReadString(slot.Dispatcher, effect, Vst2Native.EffGetEffectName);

            string vendor = Vst2Native.ReadString(slot.Dispatcher, effect, Vst2Native.EffGetVendorString);

            mSlots[request.Slot] = slot;

            return new InstrumentLoadOutcome
            {
                Ok = true,
                Name = name,
                Vendor = vendor,
                IsInstrument = slot.IsInstrument,
                InputChannels = slot.NumInputs,
                OutputChannels = slot.NumOutputs,
                LatencySamples = slot.LatencySamples,
                TailSamples = slot.TailSamples,
                HasEditor = slot.HasEditor,
                ParameterCount = slot.NumParams,
            };
        }
        catch (Exception ex)
        {
            return Fail(slot, "unexpected error while loading the plugin: " + ex.Message);
        }
        finally
        {
            sCurrent = null;
        }
    }

    InstrumentLoadOutcome Fail(Slot slot, string reason)
    {
        CleanupSlot(slot, closeEffect: true);

        if (slot.Library != IntPtr.Zero)
            NativeLibrary.Free(slot.Library);

        slot.Library = IntPtr.Zero;
        return InstrumentLoadOutcome.Fail(reason);
    }

    public bool Unload(int slotNumber)
    {
        if (!mSlots.Remove(slotNumber, out var slot))
            return false;

        CleanupSlot(slot, closeEffect: true);

        if (slot.Library != IntPtr.Zero)
            NativeLibrary.Free(slot.Library);

        slot.Library = IntPtr.Zero;
        return true;
    }

    static void CleanupSlot(Slot slot, bool closeEffect)
    {
        var editor = slot.Editor;
        slot.Editor = null;

        if (editor != null)
        {
            try
            {
                editor.Dispose();
            }
            catch (Exception)
            {
            }
        }

        if (slot.Effect != IntPtr.Zero && slot.Dispatcher != null)
        {
            try
            {
                if (slot.Started)
                {
                    Vst2Native.Call(slot.Dispatcher, slot.Effect, Vst2Native.EffStopProcess, 0,
                        IntPtr.Zero, IntPtr.Zero, 0);
                }
            }
            catch (Exception)
            {
            }

            try
            {
                Vst2Native.Call(slot.Dispatcher, slot.Effect, Vst2Native.EffMainsChanged, 0,
                    IntPtr.Zero, IntPtr.Zero, 0);
            }
            catch (Exception)
            {
            }

            if (closeEffect)
            {
                try
                {
                    Vst2Native.Call(slot.Dispatcher, slot.Effect, Vst2Native.EffClose, 0,
                        IntPtr.Zero, IntPtr.Zero, 0);
                }
                catch (Exception)
                {
                }
            }
        }

        if (slot.EventMemory != IntPtr.Zero)
            Marshal.FreeHGlobal(slot.EventMemory);

        if (slot.MidiMemory != IntPtr.Zero)
            Marshal.FreeHGlobal(slot.MidiMemory);

        slot.EventMemory = IntPtr.Zero;
        slot.MidiMemory = IntPtr.Zero;
        slot.Effect = IntPtr.Zero;
    }

    // ── Process ──

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

        if (slot.Broken)
        {
            reason = "the plugin is marked broken";
            PublishSilence(slotNumber, slot, frames, request.Tag);
            return false;
        }

        if (request.Bypass)
        {
            PublishSilence(slotNumber, slot, frames, request.Tag);
            return true;
        }

        try
        {
            for (int channel = 0; channel < slot.OutPlanes.Length; channel++)
                Array.Clear(slot.OutPlanes[channel], 0, frames);

            if (request.Transport is { } transport)
            {
                // audioMasterGetTime reads these fields while the plugin runs.
                slot.TimeSamplePosition = transport.SamplePosition;
                slot.TimePpq = transport.PpqPosition;
            }

            foreach (var parameter in request.Parameters)
            {
                if (parameter.Id >= 0 && parameter.Id < slot.NumParams)
                    slot.SetParameter(slot.Effect, (int)parameter.Id, (float)parameter.Value);
            }

            BuildEvents(slot, request.Events, frames);

            sCurrent = slot;

            bool ok = CallProcess(slot, frames);

            slot.TimeSamplePosition += frames;
            slot.TimePpq += frames / slot.SampleRate * 2.0; // 120 BPM = 2 beats/s

            if (!ok)
            {
                reason = "the plugin threw during processing";
                PublishSilence(slotNumber, slot, frames, request.Tag);
                return false;
            }

            PublishMainOutput(slotNumber, slot, frames, request.Tag);
            return true;
        }
        catch (Exception ex)
        {
            // One throw is usually a poisoned plugin: mark the slot so later
            // blocks fail fast instead of crashing the helper again.
            slot.Broken = true;
            reason = "the plugin threw during processing: " + ex.Message;

            try
            {
                PublishSilence(slotNumber, slot, frames, request.Tag);
            }
            catch (Exception)
            {
            }

            return false;
        }
        finally
        {
            sCurrent = null;
        }
    }

    static void BuildEvents(Slot slot, IReadOnlyList<MidiEvent> events, int frames)
    {
        int count = 0;
        var midi = (Vst2Native.VstMidiEvent*)slot.MidiMemory;
        var pointers = (IntPtr*)(slot.EventMemory + sizeof(Vst2Native.VstEventsHeader));

        foreach (var source in events)
        {
            if (count >= Slot.MaxEvents)
                break;

            int offset = Math.Clamp(source.Offset, 0, frames - 1);
            var target = midi + count;

            target->Type = Vst2Native.VstMidiType;
            target->ByteSize = 24;
            target->DeltaFrames = offset;
            target->Flags = 0;
            target->NoteLength = 0;
            target->NoteOffset = 0;
            target->Data3 = 0;
            target->Detune = 0;
            target->NoteOffVelocity = 0;
            target->Reserved1 = 0;
            target->Reserved2 = 0;

            int channel = source.Channel & 0x0F;

            switch (source.Type)
            {
                case MidiEventKind.NoteOn:
                    target->Data0 = (byte)(0x90 | channel);
                    target->Data1 = (byte)Math.Clamp(source.Note, 0, 127);
                    target->Data2 = (byte)Math.Clamp(source.Velocity, 0, 127);
                    break;

                case MidiEventKind.NoteOff:
                    target->Data0 = (byte)(0x80 | channel);
                    target->Data1 = (byte)Math.Clamp(source.Note, 0, 127);
                    target->Data2 = (byte)Math.Clamp(source.Velocity, 0, 127);
                    break;

                case MidiEventKind.ControlChange:
                    target->Data0 = (byte)(0xB0 | channel);
                    target->Data1 = (byte)Math.Clamp(source.Controller, 0, 127);
                    target->Data2 = (byte)Math.Clamp(source.Value, 0, 127);
                    break;

                case MidiEventKind.PitchBend:
                {
                    int value = Math.Clamp(source.Value, 0, 16383);
                    target->Data0 = (byte)(0xE0 | channel);
                    target->Data1 = (byte)(value & 0x7F);
                    target->Data2 = (byte)((value >> 7) & 0x7F);
                    break;
                }

                case MidiEventKind.ProgramChange:
                    target->Data0 = (byte)(0xC0 | channel);
                    target->Data1 = (byte)Math.Clamp(source.Program, 0, 127);
                    target->Data2 = 0;
                    break;

                case MidiEventKind.ChannelPressure:
                    target->Data0 = (byte)(0xD0 | channel);
                    target->Data1 = (byte)Math.Clamp(source.Value, 0, 127);
                    target->Data2 = 0;
                    break;

                case MidiEventKind.PolyPressure:
                    target->Data0 = (byte)(0xA0 | channel);
                    target->Data1 = (byte)Math.Clamp(source.Note, 0, 127);
                    target->Data2 = (byte)Math.Clamp(source.Value, 0, 127);
                    break;

                case MidiEventKind.AllNotesOff:
                    target->Data0 = (byte)(0xB0 | channel);
                    target->Data1 = 123;
                    target->Data2 = 0;
                    break;

                case MidiEventKind.AllSoundOff:
                    target->Data0 = (byte)(0xB0 | channel);
                    target->Data1 = 120;
                    target->Data2 = 0;
                    break;

                case MidiEventKind.ResetControllers:
                    target->Data0 = (byte)(0xB0 | channel);
                    target->Data1 = 121;
                    target->Data2 = 0;
                    break;

                default:
                    continue;
            }

            pointers[count] = (IntPtr)(midi + count);
            count++;
        }

        var header = (Vst2Native.VstEventsHeader*)slot.EventMemory;
        header->NumEvents = count;
        header->Reserved = IntPtr.Zero;

        if (count > 0)
        {
            Vst2Native.Call(slot.Dispatcher, slot.Effect, Vst2Native.EffProcessEvents, 0,
                IntPtr.Zero, slot.EventMemory, 0);
        }
    }

    // processReplacing wants float**; every pointer array and every plane must
    // stay pinned for the duration of the call.
    static bool CallProcess(Slot slot, int frames)
    {
        int inputCount = slot.InPlanes.Length;
        int outputCount = slot.OutPlanes.Length;

        var input = new IntPtr[inputCount];
        var output = new IntPtr[outputCount];
        var pins = new GCHandle[inputCount + outputCount];

        GCHandle inputArray = default;
        GCHandle outputArray = default;

        try
        {
            for (int channel = 0; channel < inputCount; channel++)
            {
                pins[channel] = GCHandle.Alloc(slot.InPlanes[channel], GCHandleType.Pinned);
                input[channel] = pins[channel].AddrOfPinnedObject();
            }

            for (int channel = 0; channel < outputCount; channel++)
            {
                pins[inputCount + channel] = GCHandle.Alloc(slot.OutPlanes[channel], GCHandleType.Pinned);
                output[channel] = pins[inputCount + channel].AddrOfPinnedObject();
            }

            inputArray = GCHandle.Alloc(input, GCHandleType.Pinned);
            outputArray = GCHandle.Alloc(output, GCHandleType.Pinned);

            // A synth with no audio inputs may be handed null rather than a
            // pointer to an empty array.
            IntPtr inputs = slot.NumInputs > 0 ? inputArray.AddrOfPinnedObject() : IntPtr.Zero;

            slot.ProcessReplacing(slot.Effect, inputs, outputArray.AddrOfPinnedObject(), frames);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (inputArray.IsAllocated)
                inputArray.Free();

            if (outputArray.IsAllocated)
                outputArray.Free();

            foreach (var pin in pins)
            {
                if (pin.IsAllocated)
                    pin.Free();
            }
        }
    }

    void PublishMainOutput(int slotNumber, Slot slot, int frames, int tag)
    {
        var left = slot.OutPlanes[0];
        var right = slot.OutPlanes.Length > 1 ? slot.OutPlanes[1] : left;

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

    // ── State ──

    public byte[]? GetState(int slotNumber)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return null;

        return slot.ProgramChunks ? GetChunk(slot) : GetParameterState(slot);
    }

    public bool SetState(int slotNumber, string base64)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        byte[] data;

        try
        {
            data = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return false;
        }

        return slot.ProgramChunks ? SetChunk(slot, data) : SetParameterState(slot, data);
    }

    static byte[]? GetChunk(Slot slot)
    {
        var handle = GCHandle.Alloc(IntPtr.Zero, GCHandleType.Pinned);

        try
        {
            IntPtr size = Vst2Native.Call(slot.Dispatcher, slot.Effect, Vst2Native.EffGetChunk, 0,
                IntPtr.Zero, handle.AddrOfPinnedObject(), 0);

            IntPtr data = Marshal.ReadIntPtr(handle.AddrOfPinnedObject());
            long length = size.ToInt64();

            if (length <= 0 || length > int.MaxValue || data == IntPtr.Zero)
                return null;

            var chunk = new byte[(int)length];
            Marshal.Copy(data, chunk, 0, chunk.Length);
            return chunk;
        }
        finally
        {
            handle.Free();
        }
    }

    static bool SetChunk(Slot slot, byte[] data)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);

        try
        {
            return Vst2Native.Call(slot.Dispatcher, slot.Effect, Vst2Native.EffSetChunk, 0,
                (IntPtr)data.Length, handle.AddrOfPinnedObject(), 0) != IntPtr.Zero;
        }
        finally
        {
            handle.Free();
        }
    }

    // Plugins without effFlagsProgramChunks still need deterministic state.
    // The opaque blob is "TLV2" + program + all normalized parameter values.
    static byte[]? GetParameterState(Slot slot)
    {
        if (slot.NumParams <= 0)
            return null;

        var data = new byte[8 + slot.NumParams * 4];
        Encoding.ASCII.GetBytes("TLV2", 0, 4, data, 0);

        int program = (int)Vst2Native.Call(slot.Dispatcher, slot.Effect, Vst2Native.EffGetProgram, 0,
            IntPtr.Zero, IntPtr.Zero, 0).ToInt64();

        BitConverter.GetBytes(program).CopyTo(data, 4);

        for (int index = 0; index < slot.NumParams; index++)
            BitConverter.GetBytes(slot.GetParameter(slot.Effect, index)).CopyTo(data, 8 + index * 4);

        return data;
    }

    static bool SetParameterState(Slot slot, byte[] data)
    {
        if (data.Length < 8 || data[0] != 'T' || data[1] != 'L' || data[2] != 'V' || data[3] != '2')
            return false;

        int program = BitConverter.ToInt32(data, 4);
        int available = (data.Length - 8) / 4;
        int count = Math.Min(slot.NumParams, available);

        Vst2Native.Call(slot.Dispatcher, slot.Effect, Vst2Native.EffSetProgram, 0,
            (IntPtr)program, IntPtr.Zero, 0);

        for (int index = 0; index < count; index++)
            slot.SetParameter(slot.Effect, index, BitConverter.ToSingle(data, 8 + index * 4));

        return true;
    }

    // ── Parameters ──

    public List<ParameterInfo> GetParameters(int slotNumber)
    {
        var list = new List<ParameterInfo>();

        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return list;

        for (int index = 0; index < slot.NumParams; index++)
        {
            list.Add(new ParameterInfo
            {
                Id = index,
                Name = Vst2Native.ReadString(slot.Dispatcher, slot.Effect, Vst2Native.EffGetParamName, index),
                Min = 0,
                Max = 1,
                Value = slot.GetParameter(slot.Effect, index),
                DefaultValue = 0,
                Steps = 0,
                IsAutomatable = true,
            });
        }

        return list;
    }

    public bool SetParameter(int slotNumber, long id, double value)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        if (id < 0 || id >= slot.NumParams)
            return false;

        slot.SetParameter(slot.Effect, (int)id, (float)value);
        return true;
    }

    // ── Panic / reset ──

    public bool Panic(int slotNumber) => SendControlEvents(slotNumber);

    public bool Reset(int slotNumber)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot) || slot.Dispatcher == null)
            return false;

        try
        {
            // Toggling mains is the only reset VST2 offers. It stops and
            // restarts processing, which clears most internal voices.
            Vst2Native.Call(slot.Dispatcher, slot.Effect, Vst2Native.EffMainsChanged, 0,
                IntPtr.Zero, IntPtr.Zero, 0);
            Vst2Native.Call(slot.Dispatcher, slot.Effect, Vst2Native.EffMainsChanged, 0,
                (IntPtr)1, IntPtr.Zero, 0);
        }
        catch (Exception)
        {
            return false;
        }

        return SendControlEvents(slotNumber);
    }

    bool SendControlEvents(int slotNumber)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        const int frames = 64;
        var events = new List<MidiEvent>(48);

        for (int channel = 0; channel < 16; channel++)
        {
            events.Add(new MidiEvent { Type = MidiEventKind.AllNotesOff, Channel = channel });
            events.Add(new MidiEvent { Type = MidiEventKind.AllSoundOff, Channel = channel });
            events.Add(new MidiEvent { Type = MidiEventKind.ResetControllers, Channel = channel });
        }

        try
        {
            for (int channel = 0; channel < slot.OutPlanes.Length; channel++)
                Array.Clear(slot.OutPlanes[channel], 0, frames);

            BuildEvents(slot, events, frames);

            sCurrent = slot;

            return CallProcess(slot, frames);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            sCurrent = null;
        }
    }

    // ── Editor ──

    public (bool Ok, string? Reason) OpenEditor(int slotNumber, string title)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return (false, "the slot is not loaded");

        if (!slot.HasEditor)
            return (false, "the plugin has no editor");

        if (slot.Editor is { } existing)
        {
            if (existing.IsAlive)
            {
                existing.BringToFront();
                return (true, null);
            }

            existing.Dispose();
            slot.Editor = null;
        }

        if (!Vst2EditorWindow.TryOpen(slot.Effect, slot.Dispatcher, title, out var editor) || editor == null)
            return (false, "the plugin refused to open its editor");

        slot.Editor = editor;
        return (true, null);
    }

    public bool CloseEditor(int slotNumber)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot) || slot.Editor is not { } editor)
            return false;

        slot.Editor = null;
        editor.Dispose();
        return true;
    }

    // ── Scan ──

    public List<PluginInfo> Scan(ScanPluginsMessage request)
    {
        var result = new List<PluginInfo>(request.Paths.Length);

        foreach (string file in request.Paths)
        {
            try
            {
                ScanOne(file, request.InstrumentOnly, result);
            }
            catch (Exception ex)
            {
                result.Add(Unavailable(file, "scanning the plugin failed: " + ex.Message));
            }
        }

        return result;
    }

    static void ScanOne(string file, bool instrumentOnly, List<PluginInfo> result)
    {
        string? binary = ResolveBinary(file);

        if (binary == null)
        {
            result.Add(Unavailable(file, "plugin file not found"));
            return;
        }

        IntPtr library;

        try
        {
            library = NativeLibrary.Load(binary);
        }
        catch (Exception ex)
        {
            result.Add(Unavailable(file, "cannot load the plugin library: " + ex.Message));
            return;
        }

        try
        {
            if (!NativeLibrary.TryGetExport(library, "VSTPluginMain", out IntPtr entry))
            {
                result.Add(Unavailable(file, "not a VST2 plugin (no VSTPluginMain export)"));
                return;
            }

            var slot = new Slot { SampleRate = 48000, Frames = 512 };
            sCurrent = slot;

            var main = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)entry;
            IntPtr effect = main(sHostCallbackPointer);

            if (effect == IntPtr.Zero)
            {
                result.Add(Unavailable(file, "the plugin refused to create its AEffect"));
                return;
            }

            if (Marshal.ReadInt32(effect, 0) != Vst2Native.Magic)
            {
                result.Add(Unavailable(file, "the returned pointer is not a VST2 AEffect"));
                return;
            }

            var layout = Vst2Native.AEffectLayout.ForPointerSize(IntPtr.Size);
            int flags = Marshal.ReadInt32(effect, layout.Flags);
            int numInputs = Math.Clamp(Marshal.ReadInt32(effect, layout.NumInputs), 0, 64);
            int numOutputs = Math.Clamp(Marshal.ReadInt32(effect, layout.NumOutputs), 0, 64);
            bool isInstrument = (flags & Vst2Native.EffFlagsIsSynth) != 0
                || (numInputs == 0 && numOutputs > 0);

            var dispatcher = (Vst2Native.VstDispatcherProc)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(effect, layout.Dispatcher), typeof(Vst2Native.VstDispatcherProc));

            // Open first: some plugins allocate name/category resources in
            // effOpen and return empty strings before it.
            Vst2Native.Call(dispatcher, effect, Vst2Native.EffOpen, 0, IntPtr.Zero, IntPtr.Zero, 0);
            Vst2Native.Call(dispatcher, effect, Vst2Native.EffSetSampleRate, 0,
                IntPtr.Zero, IntPtr.Zero, 48000f);
            Vst2Native.Call(dispatcher, effect, Vst2Native.EffSetBlockSize, 0,
                (IntPtr)512, IntPtr.Zero, 0);

            string name = Vst2Native.ReadString(dispatcher, effect, Vst2Native.EffGetProductString);
            if (string.IsNullOrEmpty(name))
                name = Vst2Native.ReadString(dispatcher, effect, Vst2Native.EffGetEffectName);

            string vendor = Vst2Native.ReadString(dispatcher, effect, Vst2Native.EffGetVendorString);
            int category = (int)Vst2Native.Call(dispatcher, effect, Vst2Native.EffGetPlugCategory, 0,
                IntPtr.Zero, IntPtr.Zero, 0).ToInt64();
            int uniqueId = Marshal.ReadInt32(effect, layout.UniqueId);

            Vst2Native.Call(dispatcher, effect, Vst2Native.EffClose, 0, IntPtr.Zero, IntPtr.Zero, 0);

            if (instrumentOnly && !isInstrument)
                return;

            result.Add(new PluginInfo
            {
                Format = PluginFormats.Vst2,
                PluginId = Path.GetFileName(file) + "/" + uniqueId.ToString("X8"),
                Name = name,
                Vendor = vendor,
                Category = CategoryName(category),
                Locator = file,
                IsInstrument = isInstrument,
                IsAvailable = true,
                Bits = Environment.Is64BitProcess ? 64 : 32,
                Arch = BridgeIdentity.Arch,
                Rid = BridgeIdentity.Rid,
            });
        }
        finally
        {
            sCurrent = null;
            NativeLibrary.Free(library);
        }
    }

    static PluginInfo Unavailable(string file, string reason) => new()
    {
        Format = PluginFormats.Vst2,
        PluginId = string.Empty,
        Name = Path.GetFileNameWithoutExtension(file),
        Locator = file,
        IsInstrument = false,
        IsAvailable = false,
        UnavailableReason = reason,
        Bits = Environment.Is64BitProcess ? 64 : 32,
        Arch = BridgeIdentity.Arch,
        Rid = BridgeIdentity.Rid,
    };

    static string CategoryName(int category) => category switch
    {
        1 => "Effect",
        2 => "Synth",
        3 => "Analysis",
        4 => "Mastering",
        5 => "Spacializer",
        6 => "RoomFx",
        7 => "SurroundFx",
        8 => "Restoration",
        9 => "OfflineProcess",
        10 => "Shell",
        11 => "Generator",
        _ => "Unknown",
    };

    // ── audioMaster callback ──

    static IntPtr HostCallback(IntPtr effect, int opcode, int index, IntPtr value, IntPtr ptr, float opt)
    {
        switch (opcode)
        {
            case Vst2Native.AudioMasterVersion:
                return (IntPtr)2400;

            case Vst2Native.AudioMasterWantMidi:
                return (IntPtr)1;

            case Vst2Native.AudioMasterGetTime:
                return TimeInfoPointer();

            case Vst2Native.AudioMasterGetSampleRate:
                return (IntPtr)(int)(sCurrent?.SampleRate ?? 48000);

            case Vst2Native.AudioMasterGetBlockSize:
                return (IntPtr)(sCurrent?.Frames ?? 512);

            case Vst2Native.AudioMasterGetCurrentProcessLevel:
                return (IntPtr)Vst2Native.ProcessLevelOffline;

            case Vst2Native.AudioMasterGetVendorString:
                return WriteString(ptr, "TuneLab");

            case Vst2Native.AudioMasterGetProductString:
                return WriteString(ptr, "TuneLab VST Instrument Host");

            case Vst2Native.AudioMasterGetVendorVersion:
                return (IntPtr)1000;

            case Vst2Native.AudioMasterCanDo:
                return CanDo(ptr);

            case Vst2Native.AudioMasterGetLanguage:
                return (IntPtr)1;

            default:
                return IntPtr.Zero;
        }
    }

    static IntPtr WriteString(IntPtr pointer, string text)
    {
        if (pointer == IntPtr.Zero)
            return IntPtr.Zero;

        byte[] bytes = Encoding.UTF8.GetBytes(text + "\0");
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return (IntPtr)1;
    }

    static IntPtr CanDo(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
            return IntPtr.Zero;

        string? name = Marshal.PtrToStringUTF8(pointer);

        return name switch
        {
            "receiveVstEvents" => (IntPtr)1,
            "receiveVstMidiEvent" => (IntPtr)1,
            "receiveVstTimeInfo" => (IntPtr)1,
            "noRealTime" => (IntPtr)1,
            "offline" => (IntPtr)1,
            _ => IntPtr.Zero,
        };
    }

    static IntPtr TimeInfoPointer()
    {
        if (sTimeInfo == IntPtr.Zero)
            sTimeInfo = Marshal.AllocHGlobal(sizeof(Vst2Native.VstTimeInfo));

        var info = (Vst2Native.VstTimeInfo*)sTimeInfo;

        info->SamplePos = sCurrent?.TimeSamplePosition ?? 0;
        info->SampleRate = sCurrent?.SampleRate ?? 48000;
        info->NanoSeconds = 0;
        info->PpqPos = sCurrent?.TimePpq ?? 0;
        info->Tempo = 120;
        info->BarStartPos = 0;
        info->CycleStartPos = 0;
        info->CycleEndPos = 0;
        info->TimeSigNumerator = 4;
        info->TimeSigDenominator = 4;
        info->SmpteOffset = 0;
        info->SmpteFrameRate = 0;
        info->SamplesToNextClock = 0;
        info->Flags = Vst2Native.VstTransportPlaying
            | Vst2Native.VstPpqPosValid
            | Vst2Native.VstTempoValid
            | Vst2Native.VstTimeSigValid;

        return sTimeInfo;
    }

    static string? ResolveBinary(string path)
    {
        if (File.Exists(path))
            return path;

        if (!Directory.Exists(path))
            return null;

        // macOS VST2 bundles: Foo.vst/Contents/MacOS/Foo
        string macos = Path.Combine(path, "Contents", "MacOS");

        if (!Directory.Exists(macos))
            return null;

        foreach (string candidate in Directory.EnumerateFiles(macos))
            return candidate;

        return null;
    }

    sealed class Slot
    {
        public const int MaxEvents = 512;

        public IntPtr Library;
        public IntPtr Effect;
        public Vst2Native.VstDispatcherProc Dispatcher = null!;
        public Vst2Native.VstSetParameterProc SetParameter = null!;
        public Vst2Native.VstGetParameterProc GetParameter = null!;
        public Vst2Native.VstProcessProc ProcessReplacing = null!;

        public int NumPrograms;
        public int NumParams;
        public int NumInputs;
        public int NumOutputs;
        public int Flags;
        public int Frames;
        public double SampleRate;

        public bool HasEditor;
        public bool IsSynth;
        public bool IsInstrument;
        public bool ProgramChunks;
        public bool Started;
        public bool Processing;
        public bool Broken;

        public Vst2EditorWindow? Editor;

        public int LatencySamples;
        public int TailSamples;

        public float[][] InPlanes = [];
        public float[][] OutPlanes = [];
        public float[][] PublishPlanes = [];

        public IntPtr EventMemory;
        public IntPtr MidiMemory;

        public long TimeSamplePosition;
        public double TimePpq;
    }
}
