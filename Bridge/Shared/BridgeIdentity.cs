using System;
using System.Runtime.InteropServices;

namespace TuneLab.VstInstruments.Bridge;

// Runtime identity shared by the extension, the helper and the catalog.
//
// The bitness alone is not enough: an x64 host can start an x86 helper, and a
// win-x64 helper is not a win-arm64 helper. Every diagnostic and every
// architecture-mismatch message uses the same string.
public static class BridgeIdentity
{
    public static string Arch => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => "x86",
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
    };

    public static int Bits => Environment.Is64BitProcess ? 64 : 32;

    public static string Rid { get; } = ComputeRid();

    public static string RidFor(int bits)
    {
        if (OperatingSystem.IsWindows())
        {
            if (bits == 32)
                return "win-x86";

            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        }

        if (bits == 32)
            return string.Empty; // .NET 8 has no linux-x86/osx-x86 runtime.

        bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        if (OperatingSystem.IsMacOS())
            return arm ? "osx-arm64" : "osx-x64";

        return arm ? "linux-arm64" : "linux-x64";
    }

    static string ComputeRid()
    {
        string rid = RidFor(Bits);

        if (!string.IsNullOrEmpty(rid))
            return rid;

        return RuntimeInformation.RuntimeIdentifier;
    }
}
