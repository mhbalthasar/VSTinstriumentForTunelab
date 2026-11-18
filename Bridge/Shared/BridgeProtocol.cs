using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TuneLab.VstInstruments.Bridge;

// Wire protocol between the TuneLab extension (host) and the helper process.
//
// This is deliberately derived from the experimental epmixer VstBridge protocol,
// but it is a different protocol with a different pipe name. The differences are
// exactly the ones an instrument needs:
//   - note/CC/pitch-bend events travel with every ProcessRequest;
//   - an instrument has no audio input, only audio output;
//   - bypass means silence, not passthrough;
//   - load carries the plugin format, stable id and optional state;
//   - transport is optional and may be null.
//
// Both sides compile this same file (the extension references the bridge's
// shared sources, the bridge project includes them). Changing a field is a
// two-sided change by construction.
public static class BridgeProtocol
{
    // Process-global names MUST differ from epmixer's:
    //   epmixer: TuneLab.VstBridge / tunelab-vstbridge / .tlhost
    //   ours:    TuneLab.VstInstrumentBridge / tunelab-vstinstrument-bridge / .tlvsti
    public const string PipeName = "TuneLab.VstInstrumentBridge";

    // macOS puts named-pipe sockets under /var/folders/.../T and the kernel
    // limits the whole path to 104 bytes. The full product name plus RID and
    // PID exceeds that limit on GitHub's runners, so Unix uses a short alias.
    // Windows keeps the descriptive name.
    public static string PipeNameFor(string rid, int processId)
    {
        string suffix = rid + "-" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return OperatingSystem.IsWindows() ? PipeName + "-" + suffix : "tlvsti-" + suffix;
    }

    // Bump only for a layout/meaning change. Additive fields do not bump.
    public const int ProtocolVersion = 1;

    // One frame = 4-byte little-endian length + UTF-8 JSON (+ optional binary).
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

// Protocol values are lowercase and stable. Display names ("CLAP", "VST3") live
// in the UI layer, never in the wire protocol.
public static class PluginFormats
{
    public const string Clap = "clap";
    public const string Vst2 = "vst2";
    public const string Vst3 = "vst3";
    public const string Lv2 = "lv2";
    public const string Au = "au";
}

public enum BridgeMessageKind
{
    Hello,
    HelloAck,
    Ping,
    PingAck,

    LoadPlugin,
    LoadPluginResult,
    UnloadPlugin,
    UnloadPluginResult,

    ProcessRequest,
    ProcessResult,

    GetState,
    StateChunk,
    SetState,
    SetStateResult,

    // Unsolicited bridge -> host event. It travels on a separate event pipe so
    // it can never be mistaken for the reply to a command. A slot is enough:
    // the host asks for the state only when it receives this.
    StateChanged,

    GetParameters,
    ParameterList,
    SetParameter,
    SetParameterResult,

    Panic,
    PanicResult,
    Reset,
    ResetResult,

    OpenEditor,
    OpenEditorResult,
    CloseEditor,
    CloseEditorResult,

    ScanPlugins,
    PluginList,

    Shutdown,
    ShutdownAck,
    Error,
}

public enum MidiEventKind
{
    NoteOn,
    NoteOff,
    ControlChange,
    PitchBend,
    ProgramChange,
    ChannelPressure,
    PolyPressure,
    AllNotesOff,
    AllSoundOff,
    ResetControllers,
}

// One MIDI event inside a render block. Offset is in samples from the start of
// the block. NoteId is allocated by the host and lets the helper match note-offs
// for overlapping same-pitch notes. Formats without note ids use it internally.
public sealed class MidiEvent
{
    public MidiEventKind Type { get; init; }
    public int Offset { get; init; }
    public int Channel { get; init; }
    public int Note { get; init; }
    public int Velocity { get; init; }
    public int Controller { get; init; }
    public int Value { get; init; }
    public int Program { get; init; }
    public int NoteId { get; init; }
}

// Optional transport. Null means "no transport"; the plugin must cope (CLAP
// allows a null transport, VST2 gets a best-effort VstTimeInfo).
public sealed class TransportInfo
{
    public bool Playing { get; init; }
    public double Tempo { get; init; } = 120.0;
    public int TimeSigNum { get; init; } = 4;
    public int TimeSigDen { get; init; } = 4;
    public long SamplePosition { get; init; }
    public double PpqPosition { get; init; }
    public double BarPosition { get; init; }
}

public sealed class HelloMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.Hello;
    public int Version { get; init; } = BridgeProtocol.ProtocolVersion;

    // Full runtime identity, not just bitness: an x64 host can start an x86
    // helper, and a win-x64 helper is not a win-arm64 helper.
    public string Rid { get; init; } = string.Empty;
    public string Arch { get; init; } = string.Empty;
    public int Bits { get; init; }
    public string[] Formats { get; init; } = [];
}

public sealed class NativeAvailability
{
    public string? Vst3 { get; init; }
    public string? Lv2 { get; init; }
    public string? Au { get; init; }
}

public sealed class HelloAckMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.HelloAck;
    public bool Ok { get; init; }
    public string? Reason { get; init; }
    public int Version { get; init; } = BridgeProtocol.ProtocolVersion;
    public string Rid { get; init; } = string.Empty;
    public string Arch { get; init; } = string.Empty;
    public string[] Formats { get; init; } = [];
    public NativeAvailability? Native { get; init; }
}

public sealed class PingAck
{
    public BridgeMessageKind Kind => BridgeMessageKind.PingAck;
}

public sealed class LoadPluginMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.LoadPlugin;
    public int Slot { get; init; }
    public string Format { get; init; } = PluginFormats.Clap;
    public string PluginId { get; init; } = string.Empty;
    public string Locator { get; init; } = string.Empty;
    public double SampleRate { get; init; } = 48000;
    public int MaxBlock { get; init; } = 1024;

    // base64(gzip(state blob)) or null. The helper decodes it; the host never
    // sends a raw multi-megabyte byte array over JSON.
    public string? State { get; init; }

    // When true (the default for this extension), the helper must reject
    // anything that is not an instrument.
    public bool InstrumentOnly { get; init; } = true;
}

public sealed class LoadPluginResultMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.LoadPluginResult;
    public int Slot { get; init; }
    public bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? Name { get; init; }
    public string? Vendor { get; init; }
    public bool IsInstrument { get; init; }
    public int InputChannels { get; init; }
    public int OutputChannels { get; init; }
    public int LatencySamples { get; init; }
    public int TailSamples { get; init; }
    public bool HasEditor { get; init; }
    public bool NeedsOpenGl { get; init; }
    public int ParameterCount { get; init; }
}

public sealed class UnloadPluginMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.UnloadPlugin;
    public int Slot { get; init; }
}

public sealed class UnloadPluginResultMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.UnloadPluginResult;
    public int Slot { get; init; }
    public bool Ok { get; init; }
}

// One plugin parameter change inside a render block. Parameter IDs are the
// format's own IDs: CLAP clap_id, VST2 parameter index.
public sealed class ParameterEvent
{
    public long Id { get; init; }
    public double Value { get; init; }
}

public sealed class ProcessRequest
{
    public BridgeMessageKind Kind => BridgeMessageKind.ProcessRequest;
    public int Slot { get; init; }
    public int Frames { get; init; }
    public bool Bypass { get; init; }
    public int Tag { get; init; }
    public TransportInfo? Transport { get; init; }
    public MidiEvent[] Events { get; init; } = [];

    // Automation for plugin parameters. Applied before the block's note
    // events. Additive field; old bridges ignore it.
    public ParameterEvent[] Parameters { get; init; } = [];
}

// Offline rendering has no real-time deadline, so the helper acknowledges each
// block explicitly. The audio itself still travels through the shared ring.
public sealed class ProcessResult
{
    public BridgeMessageKind Kind => BridgeMessageKind.ProcessResult;
    public int Slot { get; init; }
    public int Tag { get; init; }
    public bool Ok { get; init; }
    public int Frames { get; init; }
    public int Channels { get; init; }
    public string? Reason { get; init; }
}

public sealed class GetStateMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.GetState;
    public int Slot { get; init; }
}

public sealed class StateChunkMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.StateChunk;
    public int Slot { get; init; }
    public string Base64 { get; init; } = string.Empty;
}

public sealed class SetStateMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.SetState;
    public int Slot { get; init; }
    public string Base64 { get; init; } = string.Empty;
}

public sealed class SetStateResultMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.SetStateResult;
    public int Slot { get; init; }
    public bool Ok { get; init; }
}

// Bridge -> host, unsolicited. Sent on the event pipe (the command pipe's name
// plus ".events") after the bridge notices that a plugin's state hash changed
// while the host was not driving the plugin.
public sealed class StateChangedMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.StateChanged;
    public int Slot { get; init; }
}

public sealed class GetParametersMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.GetParameters;
    public int Slot { get; init; }
}

public sealed class ParameterInfo
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public double Min { get; init; }
    public double Max { get; init; }
    public double Value { get; init; }
    public double DefaultValue { get; init; }
    public int Steps { get; init; }
    public bool IsAutomatable { get; init; } = true;
}

public sealed class ParameterListMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.ParameterList;
    public int Slot { get; init; }
    public List<ParameterInfo> Parameters { get; init; } = [];
}

public sealed class SetParameterMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.SetParameter;
    public int Slot { get; init; }
    public long Id { get; init; }
    public double Value { get; init; }
}

public sealed class SetParameterResultMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.SetParameterResult;
    public int Slot { get; init; }
    public bool Ok { get; init; }
}

public sealed class PanicMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.Panic;
    public int Slot { get; init; }
}

public sealed class PanicResultMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.PanicResult;
    public int Slot { get; init; }
    public bool Ok { get; init; }
}

public sealed class ResetMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.Reset;
    public int Slot { get; init; }
}

public sealed class ResetResultMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.ResetResult;
    public int Slot { get; init; }
    public bool Ok { get; init; }
}

public sealed class OpenEditorMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.OpenEditor;
    public int Slot { get; init; }
    public string Title { get; init; } = string.Empty;
}

public sealed class OpenEditorResultMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.OpenEditorResult;
    public int Slot { get; init; }
    public bool Ok { get; init; }
    public string? Reason { get; init; }
    public int ChildWindows { get; init; }
    public long WindowHandle { get; init; }
}

public sealed class CloseEditorMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.CloseEditor;
    public int Slot { get; init; }
}

public sealed class CloseEditorResultMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.CloseEditorResult;
    public int Slot { get; init; }
    public bool Ok { get; init; }
}

public sealed class ScanPluginsMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.ScanPlugins;
    public string Format { get; init; } = PluginFormats.Clap;
    public bool Deep { get; init; }
    public bool InstrumentOnly { get; init; } = true;
    public string[] Paths { get; init; } = [];
}

public sealed class PluginInfo
{
    public string Format { get; init; } = string.Empty;
    public string PluginId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Vendor { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Locator { get; init; } = string.Empty;
    public bool IsInstrument { get; init; }
    public bool IsAvailable { get; init; } = true;
    public string? UnavailableReason { get; init; }
    public int Bits { get; init; }
    public string Arch { get; init; } = string.Empty;
    public string Rid { get; init; } = string.Empty;
}

public sealed class PluginListMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.PluginList;
    public string Format { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public List<PluginInfo> Entries { get; init; } = [];
}

public sealed class ShutdownMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.Shutdown;
}

public sealed class ShutdownAck
{
    public BridgeMessageKind Kind => BridgeMessageKind.ShutdownAck;
}

public sealed class ErrorMessage
{
    public BridgeMessageKind Kind => BridgeMessageKind.Error;
    public string Message { get; init; } = string.Empty;
}
