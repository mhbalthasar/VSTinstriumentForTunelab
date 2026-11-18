using System.Collections.Generic;
using System.IO;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// Placeholder for formats that need a native shim this build does not ship:
// VST3 (event-capable process), LV2 (lilv), AU (AudioToolbox).
//
// The bridge never advertises these formats in HelloAck, so a well-behaved
// client never reaches this host. Keeping the class makes the message loop
// total: a stale project or a hand-written message gets a clear reason instead
// of a protocol error.
internal sealed class UnsupportedInstrumentHost : IBridgeInstrumentHost
{
    readonly string mFormat;
    readonly string mReason;

    public UnsupportedInstrumentHost(string format, string reason)
    {
        mFormat = format;
        mReason = reason;
    }

    public InstrumentLoadOutcome Load(LoadPluginMessage request)
        => InstrumentLoadOutcome.Fail(mReason);

    public bool Unload(int slot) => false;

    public bool Process(int slot, ProcessRequest request, out string? reason)
    {
        reason = mReason;
        return false;
    }

    public byte[]? GetState(int slot) => null;

    public bool SetState(int slot, string base64) => false;

    public List<ParameterInfo> GetParameters(int slot) => [];

    public bool SetParameter(int slot, long id, double value) => false;

    public bool Panic(int slot) => false;

    public bool Reset(int slot) => false;

    public (bool Ok, string? Reason) OpenEditor(int slot, string title) => (false, mReason);

    public bool CloseEditor(int slot) => false;

    public List<PluginInfo> Scan(ScanPluginsMessage request)
    {
        var result = new List<PluginInfo>(request.Paths.Length);

        foreach (string file in request.Paths)
        {
            result.Add(new PluginInfo
            {
                Format = mFormat,
                PluginId = string.Empty,
                Name = Path.GetFileNameWithoutExtension(file),
                Locator = file,
                IsInstrument = false,
                IsAvailable = false,
                UnavailableReason = mReason,
                Bits = BridgeIdentity.Bits,
                Arch = BridgeIdentity.Arch,
                Rid = BridgeIdentity.Rid,
            });
        }

        return result;
    }
}
