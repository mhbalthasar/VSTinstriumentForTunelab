using System;
using System.IO;

namespace TuneLab.VstInstruments.Catalog;

// Where the extension keeps its cache. TuneLab's SDK does not expose an
// extension-data directory, so each platform's conventional per-user data
// location is used. The folder is created on demand.
internal static class CatalogPaths
{
    public static string DataRoot { get; } = ComputeDataRoot();

    public static string CacheFile => Path.Combine(DataRoot, "catalog.json");

    static string ComputeDataRoot()
    {
        // Tests (and portable setups) can isolate the catalog cache without
        // touching the real per-user TuneLab data folder.
        string? overrideRoot = Environment.GetEnvironmentVariable("TUNELAB_VSTI_DATA_ROOT");

        if (!string.IsNullOrWhiteSpace(overrideRoot))
            return Path.GetFullPath(overrideRoot);

        if (OperatingSystem.IsWindows())
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (!string.IsNullOrEmpty(local))
                return Path.Combine(local, "TuneLab", "VstInstruments");
        }
        else if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Home, "Library", "Application Support", "TuneLab", "VstInstruments");
        }
        else
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

            if (!string.IsNullOrEmpty(xdg))
                return Path.Combine(xdg, "TuneLab", "VstInstruments");

            return Path.Combine(Home, ".local", "share", "TuneLab", "VstInstruments");
        }

        return Path.Combine(Path.GetTempPath(), "TuneLab-VstInstruments");
    }

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}