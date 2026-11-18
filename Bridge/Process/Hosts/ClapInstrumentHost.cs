using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// CLAP instrument host inside the helper process.
//
// CLAP is a C ABI, so no native host library is vendored: this class lays out
// clap_audio_buffer_t, writes note/CC/parameter events into clap_input_events_t,
// calls the plugin, and publishes the main stereo output to the shared ring.
//
// Threading: the helper is single-threaded (one message, then the next). The
// plugin sees the same thread for process and on_main_thread, which CLAP allows.
// ClapLibrary's lock-free assumption depends on this.
//
// Editor hosting is intentionally absent in this build. HasEditor is false, so
// the extension falls back to its part panel instead of offering a dead button.
internal sealed unsafe class ClapInstrumentHost : IBridgeInstrumentHost
{
    readonly Dictionary<int, Slot> mSlots = [];
    SharedAudioRing? mOut;

    // Host identity pointers are handed to plugins and must stay valid for the
    // process lifetime. Unmanaged, because a managed array could move.
    static readonly IntPtr sHostName = Utf8HGlobal("TuneLab");
    static readonly IntPtr sHostVendor = Utf8HGlobal("TuneLab");
    static readonly IntPtr sHostUrl = Utf8HGlobal("https://tunelab.app");
    static readonly IntPtr sHostVersion = Utf8HGlobal("2.0");

    // clap.host-thread-check vtable: contents never change, one per process.
    static readonly IntPtr sThreadCheck = BuildThreadCheck();
    static readonly IntPtr sHostGui = BuildHostGui();

    static IntPtr Utf8HGlobal(string text)
    {
        byte[] bytes = ClapNative.Utf8(text);
        IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return pointer;
    }

    static IntPtr BuildThreadCheck()
    {
        IntPtr memory = Marshal.AllocHGlobal(sizeof(ClapNative.ClapHostThreadCheck));
        var check = (ClapNative.ClapHostThreadCheck*)memory;
        check->IsMainThread = &HostIsMainThread;
        check->IsAudioThread = &HostIsAudioThread;
        return memory;
    }

    static IntPtr BuildHostGui()
    {
        IntPtr memory = Marshal.AllocHGlobal(sizeof(ClapNative.ClapHostGui));
        var gui = (ClapNative.ClapHostGui*)memory;
        gui->ResizeHintsChanged = &HostGuiResizeHintsChanged;
        gui->RequestResize = &HostGuiRequestResize;
        gui->RequestShow = &HostGuiRequestShow;
        gui->RequestHide = &HostGuiRequestHide;
        gui->Closed = &HostGuiClosed;
        return memory;
    }

    public void Attach(SharedAudioRing output) => mOut = output;

    // ── Load / unload ──

    public InstrumentLoadOutcome Load(LoadPluginMessage request)
    {
        if (mSlots.ContainsKey(request.Slot))
            Unload(request.Slot);

        if (!Environment.Is64BitProcess)
            return InstrumentLoadOutcome.Fail("32-bit CLAP plugins are not supported");

        if (string.IsNullOrWhiteSpace(request.PluginId))
            return InstrumentLoadOutcome.Fail("the load request carries no CLAP plugin id");

        var lease = ClapLibrary.Acquire(request.Locator, out string? reason);

        if (lease == null)
            return InstrumentLoadOutcome.Fail(reason ?? "the plugin could not be loaded");

        var slot = new Slot
        {
            Lease = lease,
            SampleRate = request.SampleRate > 0 ? request.SampleRate : 48000,
            MaxFrames = (uint)Math.Clamp(request.MaxBlock, 32, SharedAudioLayout.MaxBlockFrames),
        };

        try
        {
            BuildHost(slot);

            if (!CreateInstance(slot, request.PluginId))
            {
                slot.Dispose();
                return InstrumentLoadOutcome.Fail($"the plugin factory refused to create '{request.PluginId}'");
            }

            if (slot.Plugin->Init == null || slot.Plugin->Init(slot.Plugin) == 0)
            {
                slot.Dispose();
                return InstrumentLoadOutcome.Fail("the plugin refused to initialize");
            }

            slot.Initialized = true;
            ReadExtensions(slot);

            var descriptor = slot.Plugin->Descriptor;
            string? name = descriptor == null ? null : ClapNative.StringAt(descriptor->Name);
            string? vendor = descriptor == null ? null : ClapNative.StringAt(descriptor->Vendor);
            bool isInstrument = descriptor != null
                && ClapNative.LooksLikeInstrument(ClapNative.FeaturesAt(descriptor->Features));

            if (request.InstrumentOnly && !isInstrument)
            {
                slot.Dispose();
                return InstrumentLoadOutcome.Fail("not an instrument");
            }

            if (!BuildPorts(slot, out string? portReason))
            {
                slot.Dispose();
                return InstrumentLoadOutcome.Fail(portReason ?? "the plugin has no usable audio ports");
            }

            if (!BuildNoteInput(slot, out string? noteReason))
            {
                slot.Dispose();
                return InstrumentLoadOutcome.Fail(noteReason ?? "the plugin has no note input");
            }

            // State is loaded after init and before activation. A failure is not
            // fatal: the plugin starts from its default state and the renderer
            // still produces audio.
            if (!string.IsNullOrEmpty(request.State))
                SetStateCore(slot, request.State);

            if (slot.Plugin->Activate != null
                && slot.Plugin->Activate(slot.Plugin, slot.SampleRate, 1, slot.MaxFrames) == 0)
            {
                slot.Dispose();
                return InstrumentLoadOutcome.Fail("the plugin refused to activate");
            }

            slot.Activated = true;

            if (slot.Plugin->StartProcessing != null && slot.Plugin->StartProcessing(slot.Plugin) == 0)
            {
                slot.Dispose();
                return InstrumentLoadOutcome.Fail("the plugin refused to start processing");
            }

            slot.Processing = true;
            slot.LatencySamples = ReadLatency(slot);
            slot.TailSamples = ReadTail(slot);

            mSlots[request.Slot] = slot;

            return new InstrumentLoadOutcome
            {
                Ok = true,
                Name = name,
                Vendor = vendor,
                IsInstrument = isInstrument,
                InputChannels = slot.InputChannels,
                OutputChannels = slot.OutputChannels,
                LatencySamples = slot.LatencySamples,
                TailSamples = slot.TailSamples,
                HasEditor = CanOpenGui(slot),
                ParameterCount = ReadParameterCount(slot),
            };
        }
        catch (Exception ex)
        {
            slot.Dispose();
            return InstrumentLoadOutcome.Fail("unexpected error while loading the plugin: " + ex.Message);
        }
    }

    static void BuildHost(Slot slot)
    {
        slot.HostHandle = GCHandle.Alloc(slot);
        slot.HostMemory = Marshal.AllocHGlobal(sizeof(ClapNative.ClapHost));

        var host = (ClapNative.ClapHost*)slot.HostMemory;
        host->Version = new ClapNative.ClapVersion
        {
            Major = ClapNative.HostMajor,
            Minor = ClapNative.HostMinor,
            Revision = ClapNative.HostRevision,
        };
        host->HostData = GCHandle.ToIntPtr(slot.HostHandle);
        host->Name = (byte*)sHostName;
        host->Vendor = (byte*)sHostVendor;
        host->Url = (byte*)sHostUrl;
        host->VersionString = (byte*)sHostVersion;
        host->GetExtension = &HostGetExtension;
        host->RequestRestart = &HostRequestRestart;
        host->RequestProcess = &HostRequestProcess;
        host->RequestCallback = &HostRequestCallback;
    }

    static bool CreateInstance(Slot slot, string pluginId)
    {
        // The catalog stores the bare descriptor id; tolerate a "clap:" prefix
        // from older projects.
        const string prefix = "clap:";
        string id = pluginId.StartsWith(prefix, StringComparison.Ordinal)
            ? pluginId[prefix.Length..]
            : pluginId;

        byte[] idBytes = ClapNative.Utf8(id);

        fixed (byte* idPointer = idBytes)
        {
            var plugin = slot.Lease.Factory->CreatePlugin(
                slot.Lease.Factory, (ClapNative.ClapHost*)slot.HostMemory, idPointer);

            if (plugin == null)
                return false;

            slot.Plugin = plugin;
            return true;
        }
    }

    static void ReadExtensions(Slot slot)
    {
        slot.AudioPorts = (ClapNative.ClapPluginAudioPorts*)GetPluginExtension(slot, ClapNative.AudioPortsExtension);
        slot.NotePorts = (ClapNative.ClapPluginNotePorts*)GetPluginExtension(slot, ClapNative.NotePortsExtension);
        slot.Params = (ClapNative.ClapPluginParams*)GetPluginExtension(slot, ClapNative.ParamsExtension);
        slot.State = (ClapNative.ClapPluginState*)GetPluginExtension(slot, ClapNative.StateExtension);
        slot.Latency = (ClapNative.ClapPluginLatency*)GetPluginExtension(slot, ClapNative.LatencyExtension);
        slot.Tail = (ClapNative.ClapPluginTail*)GetPluginExtension(slot, ClapNative.TailExtension);
        slot.Gui = (ClapNative.ClapPluginGui*)GetPluginExtension(slot, ClapNative.GuiExtension);
    }

    static IntPtr GetPluginExtension(Slot slot, string id)
    {
        if (slot.Plugin == null || slot.Plugin->GetExtension == null)
            return IntPtr.Zero;

        byte[] bytes = ClapNative.Utf8(id);
        IntPtr result;

        fixed (byte* pointer = bytes)
            result = slot.Plugin->GetExtension(slot.Plugin, pointer);

        return result;
    }

    static int ReadLatency(Slot slot)
        => slot.Latency != null && slot.Latency->Get != null
            ? (int)Math.Min(slot.Latency->Get(slot.Plugin), int.MaxValue)
            : 0;

    static int ReadTail(Slot slot)
        => slot.Tail != null && slot.Tail->Get != null
            ? (int)Math.Min(slot.Tail->Get(slot.Plugin), int.MaxValue)
            : 0;

    static int ReadParameterCount(Slot slot)
        => slot.Params != null && slot.Params->Count != null
            ? (int)Math.Min(slot.Params->Count(slot.Plugin), 4096)
            : 0;

    // Ports are queried once at load time and buffers are allocated once:
    // the process path does not allocate.
    static bool BuildPorts(Slot slot, out string? reason)
    {
        reason = null;

        if (slot.AudioPorts == null || slot.AudioPorts->Count == null || slot.AudioPorts->Get == null)
        {
            // No audio-ports extension: the conservative assumption for an
            // instrument is one stereo output and no audio input.
            slot.Outputs.Add(CreatePort(2, (int)slot.MaxFrames, isMain: true));
        }
        else
        {
            CollectPorts(slot, isInput: true);
            CollectPorts(slot, isInput: false);
        }

        slot.MainInput = FindMain(slot.Inputs);
        slot.MainOutput = FindMain(slot.Outputs);

        if (slot.Outputs.Count == 0 || slot.MainOutput < 0)
        {
            reason = "the plugin has no audio output";
            return false;
        }

        slot.OutputChannels = slot.Outputs[slot.MainOutput].Channels;
        slot.InputChannels = slot.MainInput >= 0 ? slot.Inputs[slot.MainInput].Channels : 0;

        if (slot.OutputChannels <= 0)
        {
            reason = "the plugin's main output has no channels";
            return false;
        }

        slot.AllocateBuffers();
        return true;
    }

    static int FindMain(List<Port> ports)
    {
        for (int i = 0; i < ports.Count; i++)
        {
            if (ports[i].IsMain)
                return i;
        }

        return ports.Count > 0 ? 0 : -1;
    }

    static void CollectPorts(Slot slot, bool isInput)
    {
        var ports = isInput ? slot.Inputs : slot.Outputs;

        uint count = slot.AudioPorts->Count(slot.Plugin, isInput ? (byte)1 : (byte)0);

        // A malformed plugin can report an absurd count; never allocate along
        // an unbounded loop.
        if (count > 32)
            count = 32;

        for (uint index = 0; index < count; index++)
        {
            var info = default(ClapNative.ClapAudioPortInfo);

            if (slot.AudioPorts->Get(slot.Plugin, index, isInput ? (byte)1 : (byte)0, &info) == 0)
                continue;

            int channels = (int)Math.Min(info.ChannelCount, 8);

            if (channels <= 0)
                continue;

            bool isMain = (info.Flags & ClapNative.AudioPortIsMain) != 0;
            ports.Add(CreatePort(channels, (int)slot.MaxFrames, isMain));
        }
    }

    static Port CreatePort(int channels, int frames, bool isMain)
    {
        var port = new Port
        {
            Channels = channels,
            IsMain = isMain,
            Frames = frames,
        };

        port.Planes = Marshal.AllocHGlobal(channels * frames * sizeof(float));
        port.Data = Marshal.AllocHGlobal(channels * sizeof(IntPtr));

        var data = (float**)port.Data;

        for (int channel = 0; channel < channels; channel++)
            data[channel] = (float*)port.Planes + (long)channel * frames;

        port.Buffer = Marshal.AllocHGlobal(sizeof(ClapNative.ClapAudioBuffer));

        var buffer = (ClapNative.ClapAudioBuffer*)port.Buffer;
        buffer->Data32 = data;
        buffer->Data64 = null;
        buffer->ChannelCount = (uint)channels;
        buffer->Latency = 0;
        buffer->ConstantMask = 0;

        return port;
    }

    // The first input note port is the instrument's note input. Prefer the CLAP
    // dialect; fall back to MIDI so older plugins still work.
    static bool BuildNoteInput(Slot slot, out string? reason)
    {
        reason = null;

        if (slot.NotePorts == null || slot.NotePorts->Count == null || slot.NotePorts->Get == null)
        {
            reason = "the plugin has no note input";
            return false;
        }

        uint count = slot.NotePorts->Count(slot.Plugin, 1);

        if (count > 32)
            count = 32;

        if (count == 0)
        {
            reason = "the plugin has no note input";
            return false;
        }

        uint bestDialects = 0;
        int bestIndex = -1;

        for (uint index = 0; index < count; index++)
        {
            var info = default(ClapNative.ClapNotePortInfo);

            if (slot.NotePorts->Get(slot.Plugin, index, 1, &info) == 0)
                continue;

            uint dialects = info.SupportedDialects;

            if ((dialects & ClapNative.NoteDialectClap) != 0)
            {
                bestDialects = dialects;
                bestIndex = (int)index;
                break;
            }

            if (bestIndex < 0)
            {
                bestDialects = dialects;
                bestIndex = (int)index;
            }
        }

        if (bestIndex < 0)
        {
            reason = "the plugin's note input has no usable dialect";
            return false;
        }

        slot.NotePortIndex = (short)bestIndex;
        slot.NoteDialects = bestDialects;
        return true;
    }

    public bool Unload(int slotNumber)
    {
        if (!mSlots.Remove(slotNumber, out var slot))
            return false;

        slot.Dispose();
        return true;
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

        int frames = Math.Min(request.Frames, (int)slot.MaxFrames);

        if (frames <= 0)
        {
            reason = "the render request has no frames";
            return false;
        }

        try
        {
            DrainCallback(slot);

            if (slot.RestartRequested)
                Restart(slot);

            if (slot.Broken)
            {
                reason = "the plugin is marked broken";
                PublishSilence(slotNumber, slot, frames, request.Tag);
                return false;
            }

            if (request.Bypass)
            {
                // Bypass means silence for an instrument, not passthrough.
                PublishSilence(slotNumber, slot, frames, request.Tag);
                return true;
            }

            ClearInputs(slot, frames);
            ClearOutputs(slot, frames);

            // Parameter changes queued by SetParameter occupy the first slots
            // of the event buffer.
            int count = slot.PendingCount;

            // Plugin parameter automation for this block, before notes.
            foreach (var parameter in request.Parameters)
            {
                if (count >= ClapNative.MaxEventsPerBlock)
                    break;

                IntPtr cookie = slot.Cookies.TryGetValue((uint)parameter.Id, out var known)
                    ? known
                    : IntPtr.Zero;

                WriteParam(slot, count, (uint)parameter.Id, cookie, parameter.Value);
                count++;
            }

            foreach (var midi in request.Events)
            {
                if (count >= ClapNative.MaxEventsPerBlock)
                    break;

                int offset = Math.Clamp(midi.Offset, 0, frames - 1);
                WriteEvent(slot, ref count, midi, offset);
            }

            slot.PendingCount = count;

            var process = slot.ProcessPtr;
            process->SteadyTime = slot.SteadyTime;
            process->FramesCount = (uint)frames;
            process->Transport = IntPtr.Zero;
            process->AudioInputs = (ClapNative.ClapAudioBuffer*)slot.InputBuffers;
            process->AudioOutputs = (ClapNative.ClapAudioBuffer*)slot.OutputBuffers;
            process->AudioInputsCount = (uint)slot.Inputs.Count;
            process->AudioOutputsCount = (uint)slot.Outputs.Count;
            process->InEvents = slot.InEventsList;
            process->OutEvents = slot.OutEventsList;

            int status = slot.Plugin->Process == null
                ? ClapNative.ProcessContinue
                : slot.Plugin->Process(slot.Plugin, process);

            slot.SteadyTime += frames;

            if (status == ClapNative.ProcessError)
            {
                reason = "the plugin reported a process error";
                PublishSilence(slotNumber, slot, frames, request.Tag);
                return false;
            }

            PublishMainOutput(slotNumber, slot, frames, request.Tag);
            return true;
        }
        catch (Exception ex)
        {
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
            // Parameter events are delivered once. Note events are rebuilt for
            // every block, so the whole buffer is reusable next time.
            slot.PendingCount = 0;
        }
    }

    static void ClearInputs(Slot slot, int frames)
    {
        // Instruments normally have no audio inputs. A plugin that declares
        // them (a vocoder, a drum machine with an input bus) must never read
        // uninitialized memory.
        foreach (var port in slot.Inputs)
        {
            for (int channel = 0; channel < port.Channels; channel++)
                new Span<float>((float*)port.Planes + (long)channel * port.Frames, frames).Clear();
        }
    }

    static void ClearOutputs(Slot slot, int frames)
    {
        foreach (var port in slot.Outputs)
        {
            for (int channel = 0; channel < port.Channels; channel++)
                new Span<float>((float*)port.Planes + (long)channel * port.Frames, frames).Clear();
        }
    }

    void PublishMainOutput(int slotNumber, Slot slot, int frames, int tag)
    {
        var main = slot.Outputs[slot.MainOutput];

        var left = new Span<float>((float*)main.Planes, frames);
        var right = main.Channels > 1
            ? new Span<float>((float*)main.Planes + main.Frames, frames)
            : left;

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

    // ── Events ──

    static void WriteEvent(Slot slot, ref int count, MidiEvent midi, int offset)
    {
        switch (midi.Type)
        {
            case MidiEventKind.NoteOn:
            case MidiEventKind.NoteOff:
                if ((slot.NoteDialects & ClapNative.NoteDialectClap) != 0)
                {
                    WriteNote(slot, count, midi, offset);
                }
                else
                {
                    WriteMidi(slot, count, offset, (byte)((midi.Type == MidiEventKind.NoteOn ? 0x90 : 0x80)
                        | (midi.Channel & 0x0F)), (byte)midi.Note, (byte)midi.Velocity);
                }

                count++;
                break;

            case MidiEventKind.ControlChange:
                WriteMidi(slot, count++, offset, (byte)(0xB0 | (midi.Channel & 0x0F)),
                    (byte)midi.Controller, (byte)midi.Value);
                break;

            case MidiEventKind.PitchBend:
            {
                int value = Math.Clamp(midi.Value, 0, 16383);
                WriteMidi(slot, count++, offset, (byte)(0xE0 | (midi.Channel & 0x0F)),
                    (byte)(value & 0x7F), (byte)((value >> 7) & 0x7F));
                break;
            }

            case MidiEventKind.ProgramChange:
                WriteMidi(slot, count++, offset, (byte)(0xC0 | (midi.Channel & 0x0F)),
                    (byte)midi.Program, 0);
                break;

            case MidiEventKind.ChannelPressure:
                WriteMidi(slot, count++, offset, (byte)(0xD0 | (midi.Channel & 0x0F)),
                    (byte)midi.Value, 0);
                break;

            case MidiEventKind.PolyPressure:
                WriteMidi(slot, count++, offset, (byte)(0xA0 | (midi.Channel & 0x0F)),
                    (byte)midi.Note, (byte)midi.Value);
                break;

            case MidiEventKind.AllNotesOff:
                WriteMidi(slot, count++, offset, (byte)(0xB0 | (midi.Channel & 0x0F)), 123, 0);
                break;

            case MidiEventKind.AllSoundOff:
                WriteMidi(slot, count++, offset, (byte)(0xB0 | (midi.Channel & 0x0F)), 120, 0);
                break;

            case MidiEventKind.ResetControllers:
                WriteMidi(slot, count++, offset, (byte)(0xB0 | (midi.Channel & 0x0F)), 121, 0);
                break;
        }
    }

    static void WriteNote(Slot slot, int index, MidiEvent midi, int offset)
    {
        var note = (ClapNative.ClapEventNote*)ReserveEvent(slot, index, sizeof(ClapNative.ClapEventNote));

        note->Header.Size = (uint)sizeof(ClapNative.ClapEventNote);
        note->Header.Time = (uint)offset;
        note->Header.SpaceId = ClapNative.EventSpacePlugin;
        note->Header.Type = midi.Type == MidiEventKind.NoteOn
            ? ClapNative.EventTypeNoteOn
            : ClapNative.EventTypeNoteOff;
        note->Header.Flags = 0;
        note->NoteId = midi.NoteId;
        note->PortIndex = slot.NotePortIndex;
        note->Channel = (short)Math.Clamp(midi.Channel, 0, 15);
        note->Key = (short)Math.Clamp(midi.Note, 0, 127);
        note->Velocity = Math.Clamp(midi.Velocity, 0, 127) / 127.0;
    }

    static void WriteMidi(Slot slot, int index, int offset, byte data0, byte data1, byte data2)
    {
        var midi = (ClapNative.ClapEventMidi*)ReserveEvent(slot, index, sizeof(ClapNative.ClapEventMidi));

        midi->Header.Size = (uint)sizeof(ClapNative.ClapEventMidi);
        midi->Header.Time = (uint)offset;
        midi->Header.SpaceId = ClapNative.EventSpacePlugin;
        midi->Header.Type = ClapNative.EventTypeMidi;
        midi->Header.Flags = 0;
        midi->PortIndex = (ushort)slot.NotePortIndex;
        midi->Data0 = data0;
        midi->Data1 = data1;
        midi->Data2 = data2;
    }

    static void WriteParam(Slot slot, int index, uint id, IntPtr cookie, double value)
    {
        var param = (ClapNative.ClapEventParamValue*)ReserveEvent(slot, index, sizeof(ClapNative.ClapEventParamValue));

        param->Header.Size = (uint)sizeof(ClapNative.ClapEventParamValue);
        param->Header.Time = 0;
        param->Header.SpaceId = ClapNative.EventSpacePlugin;
        param->Header.Type = ClapNative.EventTypeParamValue;
        param->Header.Flags = 0;
        param->ParamId = id;
        param->Cookie = cookie;
        param->NoteId = -1;
        param->PortIndex = -1;
        param->Channel = -1;
        param->Key = -1;
        param->Value = value;
    }

    static byte* ReserveEvent(Slot slot, int index, int size)
    {
        int offset = index == 0
            ? 0
            : (slot.EventOffsets[index - 1] + slot.EventSizes[index - 1] + 7) & ~7;

        slot.EventOffsets[index] = offset;
        slot.EventSizes[index] = size;

        return (byte*)slot.EventMemory + offset;
    }

    // ── State ──

    public byte[]? GetState(int slotNumber)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return null;

        if (slot.State == null || slot.State->Save == null)
            return null;

        var stream = new MemoryStream();
        var handle = GCHandle.Alloc(stream);

        try
        {
            var output = new ClapNative.ClapOStream
            {
                Ctx = GCHandle.ToIntPtr(handle),
                Write = &StateWrite,
            };

            return slot.State->Save(slot.Plugin, &output) != 0 ? stream.ToArray() : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            handle.Free();
        }
    }

    public bool SetState(int slotNumber, string base64)
        => mSlots.TryGetValue(slotNumber, out var slot) && SetStateCore(slot, base64);

    static bool SetStateCore(Slot slot, string base64)
    {
        if (slot.State == null || slot.State->Load == null)
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

        var stream = new MemoryStream(data, writable: false);
        var handle = GCHandle.Alloc(stream);

        try
        {
            var input = new ClapNative.ClapIStream
            {
                Ctx = GCHandle.ToIntPtr(handle),
                Read = &StateRead,
            };

            return slot.State->Load(slot.Plugin, &input) != 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            handle.Free();
        }
    }

    // ── Parameters ──

    public List<ParameterInfo> GetParameters(int slotNumber)
    {
        var list = new List<ParameterInfo>();

        if (!mSlots.TryGetValue(slotNumber, out var slot) || slot.Params == null)
            return list;

        DrainCallback(slot);

        try
        {
            if (slot.Params->Count == null || slot.Params->GetInfo == null)
                return list;

            uint count = slot.Params->Count(slot.Plugin);

            if (count > 4096)
                count = 4096;

            for (uint index = 0; index < count; index++)
            {
                var info = default(ClapNative.ClapParamInfo);

                if (slot.Params->GetInfo(slot.Plugin, index, &info) == 0)
                    continue;

                slot.Cookies[info.Id] = info.Cookie;

                double value = info.DefaultValue;

                if (slot.Params->GetValue != null)
                    slot.Params->GetValue(slot.Plugin, info.Id, &value);

                bool stepped = (info.Flags & ClapNative.ParamIsStepped) != 0;
                bool readOnly = (info.Flags & ClapNative.ParamIsReadonly) != 0;

                list.Add(new ParameterInfo
                {
                    Id = info.Id,
                    Name = ClapNative.FixedString(info.Name, 256),
                    Min = info.MinValue,
                    Max = info.MaxValue,
                    Value = value,
                    DefaultValue = info.DefaultValue,
                    Steps = stepped ? (int)Math.Max(0, Math.Round(info.MaxValue - info.MinValue + 1)) : 0,
                    IsAutomatable = !readOnly,
                });
            }
        }
        catch (Exception)
        {
            list.Clear();
        }

        return list;
    }

    public bool SetParameter(int slotNumber, long id, double value)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        if (slot.Params == null || slot.PendingCount >= ClapNative.MaxEventsPerBlock)
            return false;

        IntPtr cookie = slot.Cookies.TryGetValue((uint)id, out var known) ? known : IntPtr.Zero;
        WriteParam(slot, slot.PendingCount, (uint)id, cookie, value);
        slot.PendingCount++;
        return true;
    }

    // ── Panic / reset ──

    public bool Panic(int slotNumber) => SendControlEvents(slotNumber, reset: false);

    public bool Reset(int slotNumber)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        try
        {
            if (slot.Plugin != null && slot.Plugin->Reset != null)
                slot.Plugin->Reset(slot.Plugin);
        }
        catch (Exception)
        {
            return false;
        }

        return SendControlEvents(slotNumber, reset: true);
    }

    // Sends all-notes-off / all-sound-off / reset-controllers on every channel
    // through a tiny silent block. Some plugins only look at events during
    // process, so a control-only call is not enough.
    bool SendControlEvents(int slotNumber, bool reset)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot) || slot.Plugin == null)
            return false;

        const int frames = 64;
        int count = 0;

        for (int channel = 0; channel < 16; channel++)
        {
            WriteMidi(slot, count++, 0, (byte)(0xB0 | channel), 123, 0);
            WriteMidi(slot, count++, 0, (byte)(0xB0 | channel), 120, 0);
            WriteMidi(slot, count++, 0, (byte)(0xB0 | channel), 121, 0);
        }

        slot.PendingCount = count;

        try
        {
            ClearInputs(slot, frames);
            ClearOutputs(slot, frames);

            var process = slot.ProcessPtr;
            process->SteadyTime = slot.SteadyTime;
            process->FramesCount = frames;
            process->Transport = IntPtr.Zero;
            process->AudioInputs = (ClapNative.ClapAudioBuffer*)slot.InputBuffers;
            process->AudioOutputs = (ClapNative.ClapAudioBuffer*)slot.OutputBuffers;
            process->AudioInputsCount = (uint)slot.Inputs.Count;
            process->AudioOutputsCount = (uint)slot.Outputs.Count;
            process->InEvents = slot.InEventsList;
            process->OutEvents = slot.OutEventsList;

            if (slot.Plugin->Process != null)
                slot.Plugin->Process(slot.Plugin, process);

            slot.SteadyTime += frames;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            slot.PendingCount = 0;
        }
    }

    // ── Editor ──

    public void PumpEditor() => ClapEditorWindow.PumpAll();

    public (bool Ok, string? Reason) OpenEditor(int slotNumber, string title)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return (false, "the slot is not loaded");

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

        if (!CanOpenGui(slot))
            return (false, "this plugin has no editor on this platform");

        // A plugin may have requested on_main_thread during load/activate.
        // gui->create can wait for that callback; drain it first on this
        // (bridge main) thread.
        DrainCallback(slot);

        // CLAP requires gui->create on the host's main thread. Some plugins
        // (Surge XT) block forever when it is called from the editor thread,
        // so create here and let the editor thread only own the window,
        // set_parent, show and the message loop.
        // Some plugins only support a floating GUI; try that first when the
        // plugin says it supports it. The bridge main loop pumps its messages.
        bool supportsFloating = slot.Gui->IsApiSupported != null
            && slot.Gui->IsApiSupported(slot.Plugin, (byte*)ClapNative.Win32Api, 1) != 0;

        if (supportsFloating && slot.Gui->Create != null
            && slot.Gui->Create(slot.Plugin, (byte*)ClapNative.Win32Api, 1) != 0)
        {
            if (!ClapEditorWindow.TryOpenFloatingOnMainThread(slot.Plugin, slot.Gui, title,
                out var floatingWindow, out string? floatingReason) || floatingWindow == null)
            {
                return (false, floatingReason ?? "the plugin refused to open its editor");
            }

            slot.Editor = floatingWindow;
            return (true, null);
        }

        if (slot.Gui->Create == null
            || slot.Gui->Create(slot.Plugin, (byte*)ClapNative.Win32Api, 0) == 0)
        {
            return (false, "gui->create failed");
        }

        if (!ClapEditorWindow.TryOpenOnMainThread(slot.Plugin, slot.Gui, title,
            out var window, out string? reason) || window == null)
        {
            return (false, reason ?? "the plugin refused to open its editor");
        }

        slot.Editor = window;
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

    static bool CanOpenGui(Slot slot)
    {
        if (!OperatingSystem.IsWindows() || slot.Plugin == null || slot.Gui == null)
            return false;

        if (slot.Gui->IsApiSupported == null)
            return false;

        try
        {
            return slot.Gui->IsApiSupported(slot.Plugin, (byte*)ClapNative.Win32Api, 0) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── Scan ──

    public List<PluginInfo> Scan(ScanPluginsMessage request)
        => ClapScanner.ScanFiles(request.Paths, request.InstrumentOnly);

    // ── Plugin callbacks ──

    static void DrainCallback(Slot slot)
    {
        if (!slot.CallbackRequested)
            return;

        slot.CallbackRequested = false;

        try
        {
            if (slot.Plugin != null && slot.Plugin->OnMainThread != null)
                slot.Plugin->OnMainThread(slot.Plugin);
        }
        catch (Exception)
        {
            // A plugin that throws in its own callback must not stop rendering.
        }
    }

    static void Restart(Slot slot)
    {
        slot.RestartRequested = false;

        if (slot.Plugin == null)
            return;

        try
        {
            if (slot.Processing && slot.Plugin->StopProcessing != null)
                slot.Plugin->StopProcessing(slot.Plugin);

            slot.Processing = false;

            if (slot.Activated && slot.Plugin->Deactivate != null)
                slot.Plugin->Deactivate(slot.Plugin);

            slot.Activated = false;

            if (slot.Plugin->Activate == null
                || slot.Plugin->Activate(slot.Plugin, slot.SampleRate, 1, slot.MaxFrames) == 0)
            {
                slot.Broken = true;
                return;
            }

            slot.Activated = true;

            if (slot.Plugin->StartProcessing != null && slot.Plugin->StartProcessing(slot.Plugin) == 0)
            {
                slot.Broken = true;
                return;
            }

            slot.Processing = true;
            slot.LatencySamples = ReadLatency(slot);
        }
        catch (Exception)
        {
            slot.Broken = true;
        }
    }

    static Slot? SlotOf(ClapNative.ClapHost* host)
    {
        if (host == null || host->HostData == IntPtr.Zero)
            return null;

        return GCHandle.FromIntPtr(host->HostData).Target as Slot;
    }

    // Reverse P/Invoke (plugin -> host): an exception must never cross the
    // boundary, or it terminates the process.
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static IntPtr HostGetExtension(ClapNative.ClapHost* host, byte* id)
    {
        try
        {
            if (id == null)
                return IntPtr.Zero;

            string? name = Marshal.PtrToStringUTF8((IntPtr)id);

            if (string.Equals(name, ClapNative.ThreadCheckExtension, StringComparison.Ordinal))
                return sThreadCheck;

            if (string.Equals(name, ClapNative.HostGuiExtension, StringComparison.Ordinal))
                return sHostGui;

            return IntPtr.Zero;
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static void HostRequestRestart(ClapNative.ClapHost* host)
    {
        try
        {
            var slot = SlotOf(host);
            if (slot != null)
                slot.RestartRequested = true;
        }
        catch (Exception)
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static void HostRequestProcess(ClapNative.ClapHost* host)
    {
        // The host drives processing block by block; the next block is already
        // on its way, so there is nothing to schedule here.
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static void HostRequestCallback(ClapNative.ClapHost* host)
    {
        try
        {
            var slot = SlotOf(host);
            if (slot != null)
                slot.CallbackRequested = true;

            // If gui->create is calling us from the editor thread, the bridge
            // main thread is blocked waiting for the editor. Run the callback
            // here; the host thread-check already answers "main thread" on
            // this thread, so this matches what the plugin was told.
            if (ClapEditorWindow.Current != null && slot != null && slot.Plugin != null
                && slot.Plugin->OnMainThread != null)
            {
                slot.Plugin->OnMainThread(slot.Plugin);
                slot.CallbackRequested = false;
            }
        }
        catch (Exception)
        {
        }
    }

    // ── clap.host-gui ──
    //
    // These callbacks arrive from the plugin's editor thread, not the bridge
    // main thread. They only touch the editor object, which forwards to its
    // own window thread.

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static void HostGuiResizeHintsChanged(ClapNative.ClapHost* host)
    {
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static byte HostGuiRequestResize(ClapNative.ClapHost* host, uint width, uint height)
    {
        try
        {
            var editor = EditorOf(host);
            if (editor == null)
                return 0;

            editor.RequestResize((int)width, (int)height);
            return 1;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static byte HostGuiRequestShow(ClapNative.ClapHost* host)
    {
        try
        {
            var editor = EditorOf(host);
            if (editor == null)
                return 0;

            editor.RequestShow();
            return 1;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static byte HostGuiRequestHide(ClapNative.ClapHost* host)
    {
        try
        {
            var editor = EditorOf(host);
            if (editor == null)
                return 0;

            editor.RequestHide();
            return 1;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static void HostGuiClosed(ClapNative.ClapHost* host, byte wasDestroyed)
    {
    }

    // During gui->create the slot does not have the editor object yet; the
    // editor thread's Current catches the initial resize request.
    static ClapEditorWindow? EditorOf(ClapNative.ClapHost* host)
        => ClapEditorWindow.Current ?? SlotOf(host)?.Editor;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static byte HostIsMainThread(ClapNative.ClapHost* host) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static byte HostIsAudioThread(ClapNative.ClapHost* host) => 1;

    // clap_ostream_t::write
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static long StateWrite(ClapNative.ClapOStream* stream, void* buffer, ulong size)
    {
        try
        {
            if (stream == null || stream->Ctx == IntPtr.Zero)
                return -1;

            if (GCHandle.FromIntPtr(stream->Ctx).Target is not MemoryStream output)
                return -1;

            var span = new ReadOnlySpan<byte>(buffer, (int)Math.Min(size, int.MaxValue));
            output.Write(span);
            return span.Length;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    // clap_istream_t::read. 0 = EOF, which is not a failure.
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static long StateRead(ClapNative.ClapIStream* stream, void* buffer, ulong size)
    {
        try
        {
            if (stream == null || stream->Ctx == IntPtr.Zero)
                return -1;

            if (GCHandle.FromIntPtr(stream->Ctx).Target is not MemoryStream input)
                return -1;

            long remaining = input.Length - input.Position;

            if (remaining <= 0)
                return 0;

            int wanted = (int)Math.Min((ulong)Math.Min(remaining, int.MaxValue), size);
            var span = new Span<byte>(buffer, wanted);
            return input.Read(span);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    // ── Slot ──

    sealed class Port
    {
        public int Channels;
        public int Frames;
        public bool IsMain;

        // float[channels][frames], unmanaged: CLAP wants one float* per channel.
        public IntPtr Planes;
        public IntPtr Data;

        // clap_audio_buffer_t pointing at the two blocks above.
        public IntPtr Buffer;

        public void Release()
        {
            if (Planes != IntPtr.Zero)
                Marshal.FreeHGlobal(Planes);

            if (Data != IntPtr.Zero)
                Marshal.FreeHGlobal(Data);

            if (Buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(Buffer);

            Planes = IntPtr.Zero;
            Data = IntPtr.Zero;
            Buffer = IntPtr.Zero;
        }
    }

    sealed class Slot : IDisposable
    {
        public ClapLibrary.Lease Lease = null!;
        public ClapNative.ClapPlugin* Plugin;

        public IntPtr HostMemory;
        public GCHandle HostHandle;

        public ClapNative.ClapPluginAudioPorts* AudioPorts;
        public ClapNative.ClapPluginNotePorts* NotePorts;
        public ClapNative.ClapPluginParams* Params;
        public ClapNative.ClapPluginState* State;
        public ClapNative.ClapPluginLatency* Latency;
        public ClapNative.ClapPluginTail* Tail;
        public ClapNative.ClapPluginGui* Gui;

        public volatile ClapEditorWindow? Editor;

        public readonly List<Port> Inputs = [];
        public readonly List<Port> Outputs = [];
        public int MainInput = -1;
        public int MainOutput = -1;
        public int InputChannels;
        public int OutputChannels;

        public uint MaxFrames;
        public double SampleRate;
        public bool Initialized;
        public bool Activated;
        public bool Processing;
        public bool Broken;

        public short NotePortIndex;
        public uint NoteDialects;

        public volatile bool RestartRequested;
        public volatile bool CallbackRequested;

        public int LatencySamples;
        public int TailSamples;
        public long SteadyTime;

        public float[][] PublishPlanes =
        [
            new float[SharedAudioLayout.MaxBlockFrames],
            new float[SharedAudioLayout.MaxBlockFrames],
        ];

        public IntPtr InputBuffers;
        public IntPtr OutputBuffers;

        public IntPtr EventMemory;
        public readonly int[] EventOffsets = new int[ClapNative.MaxEventsPerBlock];
        public readonly int[] EventSizes = new int[ClapNative.MaxEventsPerBlock];
        public int PendingCount;

        public IntPtr InEventsMemory;
        public ClapNative.ClapInputEvents* InEventsList;
        public IntPtr OutEventsMemory;
        public ClapNative.ClapOutputEvents* OutEventsList;

        public IntPtr ProcessMemory;
        public ClapNative.ClapProcess* ProcessPtr;

        public GCHandle EventsHandle;

        public readonly Dictionary<uint, IntPtr> Cookies = [];

        public void AllocateBuffers()
        {
            InputBuffers = AllocateBufferArray(Inputs);
            OutputBuffers = AllocateBufferArray(Outputs);

            EventMemory = Marshal.AllocHGlobal(ClapNative.MaxEventsPerBlock * ClapNative.MaxEventBytes);

            InEventsMemory = Marshal.AllocHGlobal(sizeof(ClapNative.ClapInputEvents));
            InEventsList = (ClapNative.ClapInputEvents*)InEventsMemory;
            InEventsList->Size = &InputEventsSize;
            InEventsList->Get = &InputEventsGet;

            OutEventsMemory = Marshal.AllocHGlobal(sizeof(ClapNative.ClapOutputEvents));
            OutEventsList = (ClapNative.ClapOutputEvents*)OutEventsMemory;
            OutEventsList->TryPush = &OutputEventsTryPush;

            EventsHandle = GCHandle.Alloc(this);
            InEventsList->Ctx = GCHandle.ToIntPtr(EventsHandle);
            OutEventsList->Ctx = GCHandle.ToIntPtr(EventsHandle);

            ProcessMemory = Marshal.AllocHGlobal(sizeof(ClapNative.ClapProcess));
            ProcessPtr = (ClapNative.ClapProcess*)ProcessMemory;
        }

        static IntPtr AllocateBufferArray(List<Port> ports)
        {
            IntPtr memory = Marshal.AllocHGlobal(Math.Max(1, ports.Count) * sizeof(ClapNative.ClapAudioBuffer));
            var buffers = (ClapNative.ClapAudioBuffer*)memory;

            for (int i = 0; i < ports.Count; i++)
                buffers[i] = *(ClapNative.ClapAudioBuffer*)ports[i].Buffer;

            return memory;
        }

        public void Dispose()
        {
            // The editor must be destroyed before the plugin: gui->destroy runs
            // on the editor thread and the plugin must still be alive for it.
            var editor = Editor;
            Editor = null;

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

            var plugin = Plugin;

            if (plugin != null)
            {
                try
                {
                    if (Processing && plugin->StopProcessing != null)
                        plugin->StopProcessing(plugin);
                }
                catch (Exception)
                {
                }

                try
                {
                    if (Activated && plugin->Deactivate != null)
                        plugin->Deactivate(plugin);
                }
                catch (Exception)
                {
                }

                try
                {
                    if (plugin->Destroy != null)
                        plugin->Destroy(plugin);
                }
                catch (Exception)
                {
                }
            }

            Plugin = null;

            foreach (var port in Inputs)
                port.Release();

            foreach (var port in Outputs)
                port.Release();

            Inputs.Clear();
            Outputs.Clear();

            Free(ref InputBuffers);
            Free(ref OutputBuffers);
            Free(ref EventMemory);
            Free(ref InEventsMemory);
            Free(ref OutEventsMemory);
            Free(ref ProcessMemory);
            Free(ref HostMemory);

            if (HostHandle.IsAllocated)
                HostHandle.Free();

            if (EventsHandle.IsAllocated)
                EventsHandle.Free();

            Lease?.Dispose();
            Lease = null!;
        }

        static void Free(ref IntPtr memory)
        {
            if (memory != IntPtr.Zero)
                Marshal.FreeHGlobal(memory);

            memory = IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static uint InputEventsSize(ClapNative.ClapInputEvents* list)
    {
        try
        {
            var slot = list->Ctx == IntPtr.Zero ? null : GCHandle.FromIntPtr(list->Ctx).Target as Slot;
            return (uint)(slot?.PendingCount ?? 0);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static ClapNative.ClapEventHeader* InputEventsGet(ClapNative.ClapInputEvents* list, uint index)
    {
        try
        {
            var slot = list->Ctx == IntPtr.Zero ? null : GCHandle.FromIntPtr(list->Ctx).Target as Slot;

            if (slot == null || index >= (uint)slot.PendingCount)
                return null;

            return (ClapNative.ClapEventHeader*)((byte*)slot.EventMemory + slot.EventOffsets[index]);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The renderer does not consume plugin output events yet. Accept and drop
    // them so a plugin that reports parameter changes does not fail.
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static byte OutputEventsTryPush(ClapNative.ClapOutputEvents* list, ClapNative.ClapEventHeader* header) => 1;
}
