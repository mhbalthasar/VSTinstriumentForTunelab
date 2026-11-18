using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TuneLab.VstInstruments.Catalog;

// Scan folders are edited from a button dialog, not from the host's settings
// data object. The SDK does not give a button a way to write the host's
// settings store, so the extension keeps its own small file next to the
// catalog cache. It is loaded before the first scan and saved when the dialog
// is confirmed.
internal sealed class ScanPathFile
{
    public int Version { get; set; } = 1;
    public List<string> Clap { get; set; } = [];
    public List<string> Vst2 { get; set; } = [];
    public List<string> Vst3 { get; set; } = [];
    public List<string> Lv2 { get; set; } = [];
    public List<string> Au { get; set; } = [];
}

internal static class ScanPathStore
{
    static readonly JsonSerializerOptions sJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string FilePath => Path.Combine(CatalogPaths.DataRoot, "scan-paths.json");

    public static bool TryLoad(out ScanPathFile file)
    {
        file = new ScanPathFile();

        try
        {
            if (!File.Exists(FilePath))
                return false;

            string json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<ScanPathFile>(json, sJson);

            if (loaded == null)
                return false;

            file = loaded;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Save(VstInstrumentSettings settings)
    {
        try
        {
            Directory.CreateDirectory(CatalogPaths.DataRoot);

            var file = new ScanPathFile
            {
                Clap = [.. settings.ClapPaths],
                Vst2 = [.. settings.Vst2Paths],
                Vst3 = [.. settings.Vst3Paths],
                Lv2 = [.. settings.Lv2Paths],
                Au = [.. settings.AuPaths],
            };

            File.WriteAllText(FilePath, JsonSerializer.Serialize(file, sJson));
        }
        catch (Exception)
        {
        }
    }
}