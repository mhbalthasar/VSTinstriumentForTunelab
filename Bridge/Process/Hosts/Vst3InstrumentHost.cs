using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OwnVST3Host;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// VST3 (and, on macOS, AudioUnit) instrument host inside the helper process.
//
// The audio path is the vendored OwnAudioVST3 C ABI, not a hand-written VST3
// COM implementation. It gives us the processor, MIDI input, parameters,
// processor state and editor window behind one small surface.
//
// The bridge remains single-threaded for process/control; the editor runs on
// its own STA thread because the native host creates the plugin view on that
// thread and pumps it there.
internal sealed class Vst3InstrumentHost : IBridgeInstrumentHost
{
    static readonly Lazy<string?> sNativePath = new(FindNativeLibrary, isThreadSafe: true);

    public static string? NativePath => sNativePath.Value;

    public static bool IsSupported => Environment.Is64BitProcess && NativePath != null;

    static string NativeFileName => OwnVst3Wrapper.GetNativeLibraryName();

    static string? FindNativeLibrary()
    {
        if (!Environment.Is64BitProcess)
            return null;

        string name = NativeFileName;
        string baseDirectory = AppContext.BaseDirectory;

        string[] candidates =
        [
            Path.Combine(baseDirectory, name),
            Path.Combine(baseDirectory, "runtimes", BridgeIdentity.Rid, "native", name),
            Path.Combine(baseDirectory, "native", BridgeIdentity.Rid, name),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        string? configured = Environment.GetEnvironmentVariable("TL_VST3_NATIVE");

        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            return configured;

        return null;
    }

    readonly Dictionary<int, Slot> mSlots = [];
    SharedAudioRing? mOut;

    public void Attach(SharedAudioRing output) => mOut = output;

    public InstrumentLoadOutcome Load(LoadPluginMessage request)
    {
        if (mSlots.ContainsKey(request.Slot))
            Unload(request.Slot);

        if (!IsSupported)
            return InstrumentLoadOutcome.Fail("the VST3/AU native host library is missing in this bridge");

        bool isAu = string.Equals(request.Format, PluginFormats.Au, StringComparison.OrdinalIgnoreCase);

        if (isAu && !OperatingSystem.IsMacOS())
            return InstrumentLoadOutcome.Fail("AudioUnit hosting is only available on macOS");

        string? native = NativePath;

        if (native == null)
            return InstrumentLoadOutcome.Fail("the VST3/AU native host library is missing in this bridge");

        // VST3 loads by bundle path; AU identities are not paths ("AudioUnit:...").
        // The catalog carries the AU identifier in PluginId and the bundle path in
        // Locator.
        string target = isAu
            ? (!string.IsNullOrWhiteSpace(request.PluginId) ? request.PluginId : request.Locator)
            : request.Locator;

        if (string.IsNullOrWhiteSpace(target))
            return InstrumentLoadOutcome.Fail("the load request carries no plugin location");

        if (!isAu && !File.Exists(target) && !Directory.Exists(target))
            return InstrumentLoadOutcome.Fail("plugin bundle not found");

        int block = Math.Clamp(request.MaxBlock, 32, SharedAudioLayout.MaxBlockFrames);
        double sampleRate = request.SampleRate > 0 ? request.SampleRate : 48000;

        OwnVst3Wrapper wrapper;

        try
        {
            wrapper = new OwnVst3Wrapper(native);
        }
        catch (Exception ex)
        {
            return InstrumentLoadOutcome.Fail("cannot load the VST3/AU native host library: " + ex.Message);
        }

        try
        {
            if (!wrapper.LoadPlugin(target))
            {
                wrapper.Dispose();
                return InstrumentLoadOutcome.Fail("the plugin could not be loaded (bitness, bundle or missing dependency)");
            }

            if (!wrapper.Initialize(sampleRate, block))
            {
                wrapper.Dispose();
                return InstrumentLoadOutcome.Fail("the plugin refused to initialize at " + sampleRate + " Hz");
            }

            int inputs = Math.Max(0, wrapper.ActualInputChannels);
            int outputs = Math.Max(1, wrapper.ActualOutputChannels);

            bool isInstrument = wrapper.IsInstrument
                || Vst3InstrumentProbe.LooksLikeInstrument(isAu ? request.Locator : target)
                || (inputs == 0 && outputs > 0);

            if (request.InstrumentOnly && !isInstrument)
            {
                wrapper.Dispose();
                return InstrumentLoadOutcome.Fail("not an instrument");
            }

            // State is applied after initialization and before the first block.
            if (!string.IsNullOrEmpty(request.State))
            {
                try
                {
                    byte[] state = Convert.FromBase64String(request.State);
                    wrapper.SetState(state);
                }
                catch (FormatException)
                {
                }
                catch (Exception)
                {
                }
            }

            int channels = Math.Max(2, Math.Max(inputs, outputs));

            var slot = new Slot
            {
                Wrapper = wrapper,
                Frames = block,
                SampleRate = sampleRate,
                Channels = channels,
                Inputs = inputs,
                Outputs = outputs,
                InPlanes = CreatePlanes(channels, block),
                OutPlanes = CreatePlanes(channels, block),
                PublishPlanes =
                [
                    new float[SharedAudioLayout.MaxBlockFrames],
                    new float[SharedAudioLayout.MaxBlockFrames],
                ],
            };

            mSlots[request.Slot] = slot;

            int parameterCount = 0;

            try
            {
                parameterCount = wrapper.GetParameterCount();
            }
            catch (Exception)
            {
            }

            return new InstrumentLoadOutcome
            {
                Ok = true,
                Name = Safe(() => wrapper.Name) ?? Path.GetFileNameWithoutExtension(target),
                Vendor = Safe(() => wrapper.Vendor),
                IsInstrument = isInstrument,
                InputChannels = inputs,
                OutputChannels = outputs,
                LatencySamples = Math.Max(0, Safe(() => wrapper.LatencySamples)),
                TailSamples = 0,
                HasEditor = Safe(() => wrapper.HasEditor),
                ParameterCount = parameterCount,
            };
        }
        catch (Exception ex)
        {
            try
            {
                wrapper.Dispose();
            }
            catch (Exception)
            {
            }

            return InstrumentLoadOutcome.Fail("unexpected error while loading the plugin: " + ex.Message);
        }
    }

    static float[][] CreatePlanes(int channels, int frames)
    {
        var planes = new float[channels][];

        for (int channel = 0; channel < channels; channel++)
            planes[channel] = new float[frames];

        return planes;
    }

    static T Safe<T>(Func<T> get, T fallback = default!)
    {
        try
        {
            return get();
        }
        catch (Exception)
        {
            return fallback;
        }
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

        try
        {
            if (request.Transport is { } transport)
            {
                slot.Wrapper.SetTempo(transport.Tempo);
                slot.Wrapper.SetTransportState(transport.Playing);
            }

            foreach (var parameter in request.Parameters)
            {
                if (parameter.Id < int.MinValue || parameter.Id > int.MaxValue)
                    continue;

                slot.Wrapper.SetParameter((int)parameter.Id,
                    Math.Clamp(parameter.Value, 0.0, 1.0));
            }

            var midi = BuildMidi(request.Events, frames);

            if (midi.Count > 0)
                slot.Wrapper.ProcessMidi([.. midi]);

            for (int channel = 0; channel < slot.InPlanes.Length; channel++)
                Array.Clear(slot.InPlanes[channel], 0, frames);

            for (int channel = 0; channel < slot.OutPlanes.Length; channel++)
                Array.Clear(slot.OutPlanes[channel], 0, frames);

            if (!slot.Wrapper.ProcessAudio(slot.InPlanes, slot.OutPlanes, slot.Channels, frames))
            {
                reason = "the plugin failed to process the block";
                PublishSilence(slotNumber, slot, frames, request.Tag);
                return false;
            }

            PublishOutput(slotNumber, slot, frames, request.Tag);
            return true;
        }
        catch (Exception ex)
        {
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
    }

    static List<OwnVST3Host.MidiEvent> BuildMidi(IReadOnlyList<MidiEvent> events, int frames)
    {
        var result = new List<OwnVST3Host.MidiEvent>(events.Count);

        foreach (var source in events)
        {
            int offset = Math.Clamp(source.Offset, 0, frames - 1);
            int channel = source.Channel & 0x0F;

            switch (source.Type)
            {
                case MidiEventKind.NoteOn:
                    Add(0x90 | channel, source.Note, source.Velocity);
                    break;
                case MidiEventKind.NoteOff:
                    Add(0x80 | channel, source.Note, source.Velocity);
                    break;
                case MidiEventKind.ControlChange:
                    Add(0xB0 | channel, source.Controller, source.Value);
                    break;
                case MidiEventKind.PitchBend:
                    Add(0xE0 | channel, source.Value & 0x7F, (source.Value >> 7) & 0x7F);
                    break;
                case MidiEventKind.ProgramChange:
                    Add(0xC0 | channel, source.Program, 0);
                    break;
                case MidiEventKind.ChannelPressure:
                    Add(0xD0 | channel, source.Value, 0);
                    break;
                case MidiEventKind.PolyPressure:
                    Add(0xA0 | channel, source.Note, source.Value);
                    break;
                case MidiEventKind.AllNotesOff:
                    Add(0xB0 | channel, 123, 0);
                    break;
                case MidiEventKind.AllSoundOff:
                    Add(0xB0 | channel, 120, 0);
                    break;
                case MidiEventKind.ResetControllers:
                    Add(0xB0 | channel, 121, 0);
                    break;
            }

            void Add(int status, int data1, int data2)
            {
                result.Add(new OwnVST3Host.MidiEvent
                {
                    Status = (byte)status,
                    Data1 = (byte)Math.Clamp(data1, 0, 127),
                    Data2 = (byte)Math.Clamp(data2, 0, 127),
                    SampleOffset = offset,
                });
            }
        }

        return result;
    }

    void PublishOutput(int slotNumber, Slot slot, int frames, int tag)
    {
        int channels = Math.Max(1, Math.Min(2, slot.OutPlanes.Length));

        for (int i = 0; i < frames; i++)
        {
            float left = slot.OutPlanes[0][i];
            float right = slot.OutPlanes.Length > 1 ? slot.OutPlanes[1][i] : left;

            slot.PublishPlanes[0][i] = float.IsFinite(left) ? left : 0f;
            slot.PublishPlanes[1][i] = float.IsFinite(right) ? right : 0f;
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
        => mSlots.TryGetValue(slotNumber, out var slot) ? Safe(() => slot.Wrapper.GetState()) : null;

    public bool SetState(int slotNumber, string base64)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        try
        {
            byte[] data = Convert.FromBase64String(base64);
            return slot.Wrapper.SetState(data);
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

        try
        {
            foreach (var parameter in slot.Wrapper.GetAllParameters())
            {
                double range = parameter.MaxValue - parameter.MinValue;

                if (!double.IsFinite(range) || range <= 0)
                    continue;

                result.Add(new ParameterInfo
                {
                    Id = parameter.Id,
                    Name = parameter.Name ?? string.Empty,
                    Min = 0,
                    Max = 1,
                    Value = Math.Clamp((parameter.CurrentValue - parameter.MinValue) / range, 0, 1),
                    DefaultValue = Math.Clamp((parameter.DefaultValue - parameter.MinValue) / range, 0, 1),
                    Steps = 0,
                    IsAutomatable = true,
                });
            }
        }
        catch (Exception)
        {
            result.Clear();
        }

        return result;
    }

    public bool SetParameter(int slotNumber, long id, double value)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        if (id < int.MinValue || id > int.MaxValue)
            return false;

        try
        {
            return slot.Wrapper.SetParameter((int)id, Math.Clamp(value, 0.0, 1.0));
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool Panic(int slotNumber)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        var events = new List<OwnVST3Host.MidiEvent>(16 * 3);

        for (int channel = 0; channel < 16; channel++)
        {
            events.Add(new OwnVST3Host.MidiEvent { Status = (byte)(0xB0 | channel), Data1 = 123, Data2 = 0 });
            events.Add(new OwnVST3Host.MidiEvent { Status = (byte)(0xB0 | channel), Data1 = 120, Data2 = 0 });
            events.Add(new OwnVST3Host.MidiEvent { Status = (byte)(0xB0 | channel), Data1 = 121, Data2 = 0 });
        }

        try
        {
            slot.Wrapper.ProcessMidi([.. events]);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool Reset(int slotNumber)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return false;

        try
        {
            Panic(slotNumber);
            slot.Wrapper.SetTransportState(false);
            slot.Wrapper.ResetTransportPosition();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public (bool Ok, string? Reason) OpenEditor(int slotNumber, string title)
    {
        if (!mSlots.TryGetValue(slotNumber, out var slot))
            return (false, "slot not found");

        if (!Safe(() => slot.Wrapper.HasEditor))
            return (false, "the plugin has no editor");

        if (slot.Editor is { IsAlive: true })
        {
            slot.Editor.BringToFront();
            return (true, null);
        }

        slot.Editor?.Dispose();
        slot.Editor = null;

        if (!Vst3EditorWindow.TryOpen(slot, out var editor) || editor == null)
            return (false, "the plugin refused to open its editor");

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
        if (!IsSupported)
            return [];

        bool isAu = string.Equals(request.Format, PluginFormats.Au, StringComparison.OrdinalIgnoreCase);

        if (isAu)
            return ScanAudioUnits(request);

        var result = new List<PluginInfo>(request.Paths.Length);

        foreach (string path in request.Paths)
        {
            try
            {
                var descriptor = PluginScanner.ResolveAsync(new PluginDescriptor
                {
                    Identifier = path,
                    Format = PluginFormat.Vst3,
                }).GetAwaiter().GetResult();

                if (descriptor == null)
                {
                    // Native scanner resolve is not available for every
                    // bundle/native version. Instantiate it once instead; the
                    // helper is the isolation boundary, so a bad plugin only
                    // costs this scan batch.
                    var loaded = DescribeByLoading(path, request.InstrumentOnly, out string? loadReason);

                    if (loaded == null)
                        result.Add(Unavailable(path, loadReason ?? "the VST3 bundle could not be described"));
                    else
                        result.Add(loaded);

                    continue;
                }

                bool isInstrument = descriptor.IsInstrument
                    || IsInstrumentCategory(descriptor.Category)
                    || Vst3InstrumentProbe.LooksLikeInstrument(path)
                    || (descriptor.InputChannels == 0 && descriptor.OutputChannels > 0);

                if (request.InstrumentOnly && !isInstrument)
                    continue;

                result.Add(new PluginInfo
                {
                    Format = PluginFormats.Vst3,
                    PluginId = Path.GetFileNameWithoutExtension(path),
                    Name = descriptor.Name,
                    Vendor = descriptor.Vendor,
                    Category = descriptor.Category,
                    Locator = path,
                    IsInstrument = isInstrument,
                    IsAvailable = true,
                    Bits = 64,
                    Arch = BridgeIdentity.Arch,
                    Rid = BridgeIdentity.Rid,
                });
            }
            catch (Exception ex)
            {
                result.Add(Unavailable(path, "scanning the VST3 bundle failed: " + ex.Message));
            }
        }

        return result;
    }

    List<PluginInfo> ScanAudioUnits(ScanPluginsMessage request)
    {
        if (!OperatingSystem.IsMacOS())
            return [];

        var result = new List<PluginInfo>();
        var requested = new HashSet<string>(request.Paths, StringComparer.OrdinalIgnoreCase);

        try
        {
            var descriptors = PluginScanner.ScanAsync(PluginFormat.AudioUnit, ScanMode.Fast)
                .GetAwaiter().GetResult();

            foreach (var descriptor in descriptors)
            {
                bool isInstrument = descriptor.IsInstrument
                    || IsInstrumentCategory(descriptor.Category)
                    || (descriptor.InputChannels == 0 && descriptor.OutputChannels > 0);

                if (request.InstrumentOnly && !isInstrument)
                    continue;

                string path = descriptor.FilePath;

                if (string.IsNullOrWhiteSpace(path) && request.Paths.Length == 1)
                    path = request.Paths[0];

                if (requested.Count > 0
                    && !requested.Contains(path)
                    && !requested.Any(p => string.Equals(
                        Path.GetFileName(p), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                result.Add(new PluginInfo
                {
                    Format = PluginFormats.Au,
                    PluginId = descriptor.Identifier,
                    Name = descriptor.Name,
                    Vendor = descriptor.Vendor,
                    Category = descriptor.Category,
                    Locator = path,
                    IsInstrument = isInstrument,
                    IsAvailable = !string.IsNullOrEmpty(descriptor.Identifier),
                    UnavailableReason = string.IsNullOrEmpty(descriptor.Identifier)
                        ? "the AU identifier is missing"
                        : null,
                    Bits = 64,
                    Arch = BridgeIdentity.Arch,
                    Rid = BridgeIdentity.Rid,
                });
            }
        }
        catch (Exception ex)
        {
            result.Add(Unavailable("<AudioUnit registry>", "scanning AudioUnits failed: " + ex.Message));
        }

        return result;
    }

    PluginInfo? DescribeByLoading(string path, bool instrumentOnly, out string? reason)
    {
        reason = null;
        OwnVst3Wrapper? wrapper = null;

        try
        {
            wrapper = new OwnVst3Wrapper(NativePath!);

            if (!wrapper.LoadPlugin(path))
            {
                reason = "the plugin could not be loaded while describing the bundle";
                return null;
            }

            // Initialize once so the native bus arrangement reports the real
            // input/output counts. The explicit category marker still wins.
            try
            {
                wrapper.Initialize(48000, 512);
            }
            catch (Exception)
            {
            }

            int inputs = Math.Max(0, Safe(() => wrapper.ActualInputChannels));
            int outputs = Math.Max(0, Safe(() => wrapper.ActualOutputChannels));
            bool isInstrument = wrapper.IsInstrument
                || Vst3InstrumentProbe.LooksLikeInstrument(path)
                || (inputs == 0 && outputs > 0);

            if (instrumentOnly && !isInstrument)
            {
                reason = "not an instrument";
                return null;
            }

            return new PluginInfo
            {
                Format = PluginFormats.Vst3,
                PluginId = Path.GetFileNameWithoutExtension(path),
                Name = Safe(() => wrapper.Name) ?? Path.GetFileNameWithoutExtension(path),
                Vendor = Safe(() => wrapper.Vendor) ?? string.Empty,
                Category = string.Empty,
                Locator = path,
                IsInstrument = isInstrument,
                IsAvailable = true,
                Bits = 64,
                Arch = BridgeIdentity.Arch,
                Rid = BridgeIdentity.Rid,
            };
        }
        catch (Exception ex)
        {
            reason = "describing the bundle failed: " + ex.Message;
            return null;
        }
        finally
        {
            try
            {
                wrapper?.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }

    static bool IsInstrumentCategory(string? category)
        => !string.IsNullOrEmpty(category)
           && (category.Contains("Instrument", StringComparison.OrdinalIgnoreCase)
               || category.Contains("Synth", StringComparison.OrdinalIgnoreCase));

    static PluginInfo Unavailable(string path, string reason) => new()
    {
        Format = PluginFormats.Vst3,
        PluginId = string.Empty,
        Name = Path.GetFileNameWithoutExtension(path),
        Locator = path,
        IsInstrument = false,
        IsAvailable = false,
        UnavailableReason = reason,
        Bits = Environment.Is64BitProcess ? 64 : 32,
        Arch = BridgeIdentity.Arch,
        Rid = BridgeIdentity.Rid,
    };

    internal sealed class Slot : IDisposable
    {
        public OwnVst3Wrapper Wrapper = null!;
        public int Frames;
        public double SampleRate;
        public int Channels;
        public int Inputs;
        public int Outputs;
        public float[][] InPlanes = [];
        public float[][] OutPlanes = [];
        public float[][] PublishPlanes = [];
        public bool Broken;
        public Vst3EditorWindow? Editor;

        public void Dispose()
        {
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

            try
            {
                Wrapper.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }
}