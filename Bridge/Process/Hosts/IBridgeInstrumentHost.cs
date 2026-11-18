using System.Collections.Generic;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// What one format host must implement. The bridge session knows nothing about
// CLAP or VST2; it parses messages, finds the host that owns a slot, and
// forwards the call.
internal interface IBridgeInstrumentHost
{
    InstrumentLoadOutcome Load(LoadPluginMessage request);

    bool Unload(int slot);

    // Render one block. Audio is published to the shared output ring; the
    // returned reason is null on success.
    bool Process(int slot, ProcessRequest request, out string? reason);

    byte[]? GetState(int slot);

    bool SetState(int slot, string base64);

    List<ParameterInfo> GetParameters(int slot);

    bool SetParameter(int slot, long id, double value);

    bool Panic(int slot);

    bool Reset(int slot);

    (bool Ok, string? Reason) OpenEditor(int slot, string title);

    bool CloseEditor(int slot);

    List<PluginInfo> Scan(ScanPluginsMessage request);
}

internal sealed class InstrumentLoadOutcome
{
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
    public int ParameterCount { get; init; }

    public static InstrumentLoadOutcome Fail(string reason) => new() { Ok = false, Reason = reason };
}
