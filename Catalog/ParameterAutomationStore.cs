using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using TuneLab.VstInstruments.Bridge;

namespace TuneLab.VstInstruments.Catalog;

// Plugin parameters that the user has promoted to TuneLab automation tracks.
//
// This is keyed by the instrument id (the same id as the catalog entry), not
// by a part id: part ids are runtime tokens and do not survive a project
// reload. A part using the same instrument therefore gets the same added
// parameter tracks. The cache also stores the parameter metadata, so
// declarations can be rebuilt without loading the plugin again.
internal sealed class InstrumentParameterAutomation
{
    public string InstrumentId { get; set; } = string.Empty;
    public List<ParameterInfo> Parameters { get; set; } = [];
    public List<long> Selected { get; set; } = [];
}

internal sealed class ParameterAutomationFile
{
    public int Version { get; set; } = 1;
    public List<InstrumentParameterAutomation> Instruments { get; set; } = [];
}

internal static class ParameterAutomationStore
{
    static readonly JsonSerializerOptions sJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    static readonly object sLock = new();
    static readonly Dictionary<string, InstrumentParameterAutomation> sById = new(StringComparer.Ordinal);
    static bool sLoaded;

    public static string FilePath => Path.Combine(CatalogPaths.DataRoot, "automation.json");

    public static bool IsLinear(ParameterInfo parameter)
        => parameter.IsAutomatable
            && parameter.Steps == 0
            && double.IsFinite(parameter.Min)
            && double.IsFinite(parameter.Max)
            && parameter.Max > parameter.Min;

    public static IReadOnlyList<ParameterInfo> GetParameters(string instrumentId)
    {
        lock (sLock)
        {
            EnsureLoaded();
            return sById.TryGetValue(instrumentId, out var entry)
                ? [.. entry.Parameters]
                : [];
        }
    }

    public static IReadOnlyList<ParameterInfo> GetSelectedParameters(string instrumentId)
    {
        lock (sLock)
        {
            EnsureLoaded();

            if (!sById.TryGetValue(instrumentId, out var entry) || entry.Selected.Count == 0)
                return [];

            var byId = entry.Parameters.ToDictionary(p => p.Id);
            var result = new List<ParameterInfo>(entry.Selected.Count);

            foreach (long id in entry.Selected)
            {
                if (byId.TryGetValue(id, out var parameter))
                    result.Add(parameter);
            }

            return result;
        }
    }

    public static void SetParameters(string instrumentId, IReadOnlyList<ParameterInfo> parameters,
        IReadOnlyList<long> selected)
    {
        lock (sLock)
        {
            EnsureLoaded();

            sById[instrumentId] = new InstrumentParameterAutomation
            {
                InstrumentId = instrumentId,
                Parameters = [.. parameters],
                Selected = [.. selected],
            };

            Save();
        }
    }

    public static void Clear(string instrumentId)
    {
        lock (sLock)
        {
            EnsureLoaded();

            if (sById.Remove(instrumentId))
                Save();
        }
    }

    static void EnsureLoaded()
    {
        if (sLoaded)
            return;

        sLoaded = true;

        try
        {
            if (!File.Exists(FilePath))
                return;

            var file = JsonSerializer.Deserialize<ParameterAutomationFile>(
                File.ReadAllText(FilePath), sJson);

            if (file == null)
                return;

            foreach (var entry in file.Instruments)
            {
                if (!string.IsNullOrEmpty(entry.InstrumentId))
                    sById[entry.InstrumentId] = entry;
            }
        }
        catch (Exception)
        {
        }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(CatalogPaths.DataRoot);

            var file = new ParameterAutomationFile
            {
                Instruments = [.. sById.Values],
            };

            File.WriteAllText(FilePath, JsonSerializer.Serialize(file, sJson));
        }
        catch (Exception)
        {
        }
    }
}