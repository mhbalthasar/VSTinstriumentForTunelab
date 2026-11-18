using System;
using System.Collections.Generic;
using System.IO;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// Describes .clap files by loading each factory and reading its descriptors.
// Runs in the helper: loading a plugin executes third-party code, and a crash
// here must not take TuneLab down.
internal static unsafe class ClapScanner
{
    public static List<PluginInfo> ScanFiles(IReadOnlyList<string> files, bool instrumentOnly)
    {
        var result = new List<PluginInfo>(files.Count);

        if (!Environment.Is64BitProcess)
        {
            foreach (string file in files)
            {
                result.Add(Unavailable(file, "32-bit CLAP plugins are not supported"));
            }

            return result;
        }

        foreach (string file in files)
        {
            try
            {
                ScanOne(file, instrumentOnly, result);
            }
            catch (Exception ex)
            {
                result.Add(Unavailable(file, "scanning the plugin failed: " + ex.GetBaseException().Message));
            }
        }

        return result;
    }

    static void ScanOne(string file, bool instrumentOnly, List<PluginInfo> result)
    {
        var lease = ClapLibrary.Acquire(file, out string? reason);

        if (lease == null)
        {
            result.Add(Unavailable(file, reason ?? "the plugin could not be loaded"));
            return;
        }

        try
        {
            uint count = lease.Factory->GetPluginCount(lease.Factory);

            if (count == 0)
            {
                result.Add(Unavailable(file, "the plugin factory is empty"));
                return;
            }

            // A malformed factory can return a huge count; never loop forever.
            if (count > 1024)
                count = 1024;

            for (uint index = 0; index < count; index++)
            {
                var descriptor = lease.Factory->GetPluginDescriptor(lease.Factory, index);

                if (descriptor == null)
                    continue;

                string id = ClapNative.StringAt(descriptor->Id) ?? string.Empty;
                string name = ClapNative.StringAt(descriptor->Name) ?? Path.GetFileNameWithoutExtension(file);
                string vendor = ClapNative.StringAt(descriptor->Vendor) ?? string.Empty;
                var features = ClapNative.FeaturesAt(descriptor->Features);
                bool isInstrument = ClapNative.LooksLikeInstrument(features);

                if (instrumentOnly && !isInstrument)
                    continue;

                result.Add(new PluginInfo
                {
                    Format = PluginFormats.Clap,
                    PluginId = id,
                    Name = name,
                    Vendor = vendor,
                    Category = string.Join("|", features),
                    Locator = file,
                    IsInstrument = isInstrument,
                    IsAvailable = true,
                    Bits = BridgeIdentity.Bits,
                    Arch = BridgeIdentity.Arch,
                    Rid = BridgeIdentity.Rid,
                });
            }
        }
        finally
        {
            lease.Dispose();
        }
    }

    static PluginInfo Unavailable(string file, string reason) => new()
    {
        Format = PluginFormats.Clap,
        PluginId = string.Empty,
        Name = Path.GetFileNameWithoutExtension(file),
        Locator = file,
        IsInstrument = false,
        IsAvailable = false,
        UnavailableReason = reason,
        Bits = BridgeIdentity.Bits,
        Arch = BridgeIdentity.Arch,
        Rid = BridgeIdentity.Rid,
    };
}
