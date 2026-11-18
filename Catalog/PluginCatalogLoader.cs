using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using TuneLab.SDK;
using TuneLab.VstInstruments.Bridge;

namespace TuneLab.VstInstruments.Catalog;

// Catalog loading: enumerate plugin files on the host, describe them in the
// helper, cache the result next to the extension's data.
//
// The cache is keyed by locator + mtime + size. A plugin that changed is
// re-described; a plugin that disappeared is dropped. If the helper is
// unavailable, the cached catalog is still usable for the menu; loading an
// instrument will report the real bridge error later.
internal static class PluginCatalogLoader
{
    static readonly JsonSerializerOptions sJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    static readonly StringComparison sPathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static PluginCatalog LoadOrScan(VstInstrumentSettings settings, ILogger logger, bool forceScan = false)
    {
        var cached = LoadCache(logger);

        var client = VstInstrumentBridgeClient.ForRid(BridgeIdentity.Rid);
        bool started = client.EnsureStartedBlocking();

        if (!started)
        {
            logger.Warning("VST instrument bridge is unavailable; using the cached catalog: "
                + (client.LastBridgeError ?? "no bridge executable"));

            return BuildFromCache(cached);
        }

        var supportedFormats = new HashSet<string>(
            client.BridgeFormats ?? [], StringComparer.OrdinalIgnoreCase);

        if (!supportedFormats.Contains(PluginFormats.Clap))
            logger.Info("This bridge does not host CLAP instruments (64-bit helper required).");

        if (!supportedFormats.Contains(PluginFormats.Vst2))
            logger.Info("This bridge does not host VST2 instruments.");

        var entries = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);

        foreach (var entry in cached)
        {
            if (string.IsNullOrEmpty(entry.Id) || string.IsNullOrEmpty(entry.Locator))
                continue;

            if (!File.Exists(entry.Locator) && !Directory.Exists(entry.Locator))
                continue;

            // Keep the cached entry. It is re-scanned below if its stamp
            // changed; supported formats with no changes cost no plugin load.
            entries[entry.Id] = entry;
        }

        var rootsByFormat = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [PluginFormats.Clap] = Collect(settings.ClapPaths, DefaultClapRoots()),
            [PluginFormats.Vst2] = Collect(settings.Vst2Paths, DefaultVst2Roots()),
            [PluginFormats.Vst3] = Collect(settings.Vst3Paths, DefaultVst3Roots()),
            [PluginFormats.Lv2] = Collect(settings.Lv2Paths, DefaultLv2Roots()),
            [PluginFormats.Au] = Collect(settings.AuPaths, DefaultAuRoots()),
        };

        foreach (var (format, roots) in rootsByFormat)
        {
            if (!supportedFormats.Contains(format))
            {
                // A cached entry for an unsupported format must not appear as
                // selectable: this build cannot render it.
                foreach (var entry in entries.Values.Where(e => e.Format == format).ToList())
                {
                    entry.IsAvailable = false;
                    entry.UnavailableReason = "this build of the bridge cannot host " + format;
                }

                continue;
            }

            var files = Enumerate(roots, format, settings.ScanDepth);
            var stale = new List<string>(files.Count);

            foreach (string file in files)
            {
                var stamp = GetStamp(file);
                var known = entries.Values.FirstOrDefault(e =>
                    e.Format == format && string.Equals(e.Locator, file, sPathComparison));

                if (!forceScan && known != null && known.IsAvailable
                    && known.MtimeTicks == stamp.Mtime && known.Size == stamp.Size)
                {
                    continue;
                }

                if (known != null)
                    entries.Remove(known.Id);

                stale.Add(file);
            }

            if (stale.Count > 0)
                ScanByArchitecture(format, stale, entries, logger);
        }

        // Files that vanished between cache write and now.
        foreach (var entry in entries.Values.ToList())
        {
            if (!File.Exists(entry.Locator) && !Directory.Exists(entry.Locator))
                entries.Remove(entry.Id);
        }

        SaveCache(entries.Values.ToList(), logger);
        return Build(entries.Values);
    }

    // ── Scanning ──

    // The extension may be x64 while a VST2 plugin is x86. Group files by the
    // architecture read from their binary headers and use the matching helper.
    static void ScanByArchitecture(string format, List<string> files,
        Dictionary<string, CatalogEntry> entries, ILogger logger)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var unavailable = new List<(string File, string Reason)>();

        foreach (string file in files)
        {
            string rid = PluginBinaryProbe.DetectRid(file, format) ?? BridgeIdentity.Rid;

            if (string.Equals(format, PluginFormats.Clap, StringComparison.OrdinalIgnoreCase)
                && rid.EndsWith("x86", StringComparison.OrdinalIgnoreCase))
            {
                unavailable.Add((file, "32-bit CLAP plugins are not supported"));
                continue;
            }

            if (!OperatingSystem.IsWindows() && rid.EndsWith("x86", StringComparison.OrdinalIgnoreCase))
            {
                unavailable.Add((file, "no 32-bit .NET runtime exists on this platform"));
                continue;
            }

            if (string.IsNullOrEmpty(rid))
                rid = BridgeIdentity.Rid;

            if (!groups.TryGetValue(rid, out var list))
                groups[rid] = list = [];

            list.Add(file);
        }

        foreach (var (rid, list) in groups)
        {
            var client = VstInstrumentBridgeClient.ForRid(rid);

            if (!client.EnsureStartedBlocking())
            {
                string reason = client.LastBridgeError ?? $"the helper for {rid} could not start";

                foreach (string file in list)
                    unavailable.Add((file, reason));

                continue;
            }

            if (client.BridgeFormats == null
                || !client.BridgeFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
            {
                foreach (string file in list)
                    unavailable.Add((file, $"this bridge ({rid}) cannot host {format}"));

                continue;
            }

            ScanBatches(client, format, list, entries, logger);
        }

        foreach (var (file, reason) in unavailable)
            AddUnavailable(entries, file, format, reason);
    }

    static void AddUnavailable(Dictionary<string, CatalogEntry> entries, string file, string format, string reason)
    {
        string id = format + ":unavailable:" + file;
        var stamp = GetStamp(file);

        entries[id] = new CatalogEntry
        {
            Id = id,
            Format = format,
            PluginId = string.Empty,
            Name = Path.GetFileNameWithoutExtension(file),
            Locator = file,
            IsInstrument = false,
            IsAvailable = false,
            UnavailableReason = reason,
            MtimeTicks = stamp.Mtime,
            Size = stamp.Size,
        };
    }

    static void ScanBatches(VstInstrumentBridgeClient client, string format, List<string> files,
        Dictionary<string, CatalogEntry> entries, ILogger logger)
    {
        // Scanning executes plugin code. A small batch keeps a crash from
        // destroying a long scan; a failed batch is retried one file at a time.
        const int batchSize = 4;

        for (int start = 0; start < files.Count; start += batchSize)
        {
            var batch = files.GetRange(start, Math.Min(batchSize, files.Count - start));
            var infos = client.ScanBlocking(format, batch, instrumentOnly: true,
                timeout: TimeSpan.FromSeconds(90));

            if (infos != null)
            {
                AddEntries(entries, infos, logger);
                continue;
            }

            logger.Warning($"Scanning a {format} batch failed: {client.LastBridgeError ?? "no reply"}");
            client.KillBridge();

            if (!client.EnsureStartedBlocking())
            {
                logger.Warning("The bridge could not be restarted; the remaining files are not scanned.");
                return;
            }

            // Isolate the offending file so the rest of the batch still lands.
            foreach (string file in batch)
            {
                var one = client.ScanBlocking(format, [file], instrumentOnly: true,
                    timeout: TimeSpan.FromSeconds(90));

                if (one == null)
                {
                    logger.Warning($"Scanning '{file}' failed: {client.LastBridgeError ?? "no reply"}");
                    client.KillBridge();

                    if (!client.EnsureStartedBlocking())
                        return;

                    continue;
                }

                AddEntries(entries, one, logger);
            }
        }
    }

    static void AddEntries(Dictionary<string, CatalogEntry> entries,
        IReadOnlyList<PluginInfo> infos, ILogger logger)
    {
        foreach (var info in infos)
        {
            if (!info.IsAvailable || !info.IsInstrument || string.IsNullOrEmpty(info.PluginId))
                continue;

            if (string.IsNullOrEmpty(info.Locator))
                continue;

            string baseId = info.Format + ":" + info.PluginId;
            string id = baseId;
            int suffix = 2;

            while (entries.ContainsKey(id))
            {
                id = baseId + "#" + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }

            var stamp = GetStamp(info.Locator);

            entries[id] = new CatalogEntry
            {
                Id = id,
                Format = info.Format,
                PluginId = info.PluginId,
                Name = info.Name,
                Vendor = info.Vendor,
                Category = info.Category,
                Locator = info.Locator,
                IsInstrument = true,
                IsAvailable = true,
                Bits = info.Bits,
                Arch = info.Arch,
                Rid = info.Rid,
                MtimeTicks = stamp.Mtime,
                Size = stamp.Size,
            };

            logger.Info($"Found {info.Format} instrument: {info.Name} ({id})");
        }
    }

    // ── Catalog construction ──

    static PluginCatalog Build(IEnumerable<CatalogEntry> source)
    {
        var byId = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);

        foreach (var entry in source)
            byId[entry.Id] = entry;

        var selectable = byId.Values
            .Where(e => e.IsInstrument && e.IsAvailable)
            .OrderBy(e => e.FormatDisplay, StringComparer.Ordinal)
            .ThenBy(e => e.Vendor, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var layout = new List<InstrumentSourceLayoutItem>();

        foreach (var formatGroup in selectable.GroupBy(e => e.FormatDisplay))
        {
            var vendorItems = new List<InstrumentSourceLayoutItem>();

            foreach (var vendorGroup in formatGroup
                .GroupBy(e => string.IsNullOrWhiteSpace(e.Vendor) ? "Unknown vendor" : e.Vendor)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var instruments = vendorGroup
                    .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(e => InstrumentSourceLayoutItem.Instrument(e.Id))
                    .ToList();

                vendorItems.Add(InstrumentSourceLayoutItem.Group(vendorGroup.Key, instruments));
            }

            layout.Add(InstrumentSourceLayoutItem.Group(formatGroup.Key, vendorItems));
        }

        return new PluginCatalog(byId, layout);
    }

    static PluginCatalog BuildFromCache(IEnumerable<CatalogEntry> cached)
        => Build(cached.Where(e => File.Exists(e.Locator) || Directory.Exists(e.Locator)));

    // ── Cache ──

    static List<CatalogEntry> LoadCache(ILogger logger)
    {
        try
        {
            if (!File.Exists(CatalogPaths.CacheFile))
                return [];

            string json = File.ReadAllText(CatalogPaths.CacheFile);
            var cache = JsonSerializer.Deserialize<CatalogCache>(json, sJson);
            return cache?.Entries ?? [];
        }
        catch (Exception ex)
        {
            logger.Warning("Reading the instrument catalog cache failed: " + ex.Message);
            return [];
        }
    }

    static void SaveCache(List<CatalogEntry> entries, ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(CatalogPaths.DataRoot);
            string json = JsonSerializer.Serialize(new CatalogCache { Entries = entries }, sJson);
            File.WriteAllText(CatalogPaths.CacheFile, json);
        }
        catch (Exception ex)
        {
            logger.Warning("Writing the instrument catalog cache failed: " + ex.Message);
        }
    }

    // ── File enumeration ──

    static List<string> Enumerate(IEnumerable<string> roots, string format, int depth)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsLinux()
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase);

        foreach (string root in roots)
        {
            string expanded;

            try
            {
                expanded = ExpandHome(root);
            }
            catch (Exception)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(expanded))
                continue;

            if (File.Exists(expanded))
            {
                if (Matches(expanded, format) && seen.Add(expanded))
                    result.Add(expanded);

                continue;
            }

            if (!Directory.Exists(expanded))
                continue;

            Walk(expanded, format, Math.Max(0, depth), result, seen);
        }

        return result;
    }

    static void Walk(string directory, string format, int depth, List<string> result, HashSet<string> seen)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if (Matches(file, format) && seen.Add(file))
                    result.Add(file);
            }

            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                // Plugin bundles are leaves: never walk inside a .vst3/.clap/
                // .vst/.lv2/.component directory.
                if (IsBundle(child, format))
                {
                    if (seen.Add(child))
                        result.Add(child);

                    continue;
                }

                if (depth > 0)
                    Walk(child, format, depth - 1, result, seen);
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    static bool Matches(string path, string format)
    {
        string extension = Path.GetExtension(path);

        return format switch
        {
            PluginFormats.Clap => extension.Equals(".clap", StringComparison.OrdinalIgnoreCase),

            PluginFormats.Vst2 => OperatingSystem.IsWindows()
                ? extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                : OperatingSystem.IsMacOS()
                    ? extension.Equals(".vst", StringComparison.OrdinalIgnoreCase)
                    : extension.Equals(".so", StringComparison.OrdinalIgnoreCase),

            PluginFormats.Vst3 => extension.Equals(".vst3", StringComparison.OrdinalIgnoreCase),
            PluginFormats.Lv2 => extension.Equals(".lv2", StringComparison.OrdinalIgnoreCase),
            PluginFormats.Au => extension.Equals(".component", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    static bool IsBundle(string path, string format) => format switch
    {
        PluginFormats.Clap => Path.GetExtension(path).Equals(".clap", StringComparison.OrdinalIgnoreCase),
        PluginFormats.Vst2 => OperatingSystem.IsMacOS()
            && Path.GetExtension(path).Equals(".vst", StringComparison.OrdinalIgnoreCase),
        PluginFormats.Vst3 => Path.GetExtension(path).Equals(".vst3", StringComparison.OrdinalIgnoreCase),
        PluginFormats.Lv2 => Path.GetExtension(path).Equals(".lv2", StringComparison.OrdinalIgnoreCase),
        PluginFormats.Au => Path.GetExtension(path).Equals(".component", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    static (long Mtime, long Size) GetStamp(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                return (info.LastWriteTimeUtc.Ticks, info.Length);
            }

            if (Directory.Exists(path))
                return (Directory.GetLastWriteTimeUtc(path).Ticks, 0);
        }
        catch (Exception)
        {
        }

        return (0, 0);
    }

    static string ExpandHome(string path)
    {
        if (path == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (path.StartsWith("~/", StringComparison.Ordinal)
            || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }

        return path;
    }

    static List<string> Collect(List<string> configured, IEnumerable<string> defaults)
    {
        var result = new List<string>();

        foreach (string path in configured)
        {
            if (!string.IsNullOrWhiteSpace(path))
                result.Add(path);
        }

        foreach (string path in defaults)
        {
            if (!string.IsNullOrWhiteSpace(path))
                result.Add(path);
        }

        return result
            .Distinct(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Default scan roots ──

    static IEnumerable<string> DefaultClapRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(CommonProgramFiles, "CLAP");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Library/Audio/Plug-Ins/CLAP";
            yield return Path.Combine(Home, "Library", "Audio", "Plug-Ins", "CLAP");
        }
        else
        {
            yield return "/usr/lib/clap";
            yield return "/usr/local/lib/clap";
            yield return Path.Combine(Home, ".clap");
        }
    }

    static IEnumerable<string> DefaultVst2Roots()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(CommonProgramFiles, "VST2");
            yield return Path.Combine(ProgramFiles, "VSTPlugins");
            yield return Path.Combine(ProgramFiles, "Steinberg", "VSTPlugins");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Library/Audio/Plug-Ins/VST";
            yield return Path.Combine(Home, "Library", "Audio", "Plug-Ins", "VST");
        }
        else
        {
            yield return "/usr/lib/vst";
            yield return "/usr/local/lib/vst";
            yield return Path.Combine(Home, ".vst");
        }
    }

    static IEnumerable<string> DefaultVst3Roots()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(CommonProgramFiles, "VST3");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Library/Audio/Plug-Ins/VST3";
            yield return Path.Combine(Home, "Library", "Audio", "Plug-Ins", "VST3");
        }
        else
        {
            yield return "/usr/lib/vst3";
            yield return "/usr/local/lib/vst3";
            yield return Path.Combine(Home, ".vst3");
        }
    }

    static IEnumerable<string> DefaultLv2Roots()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(CommonProgramFiles, "LV2");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Library/Audio/Plug-Ins/LV2";
            yield return Path.Combine(Home, "Library", "Audio", "Plug-Ins", "LV2");
        }
        else
        {
            yield return "/usr/lib/lv2";
            yield return "/usr/local/lib/lv2";
            yield return Path.Combine(Home, ".lv2");
        }
    }

    static IEnumerable<string> DefaultAuRoots()
    {
        if (!OperatingSystem.IsMacOS())
            yield break;

        yield return "/Library/Audio/Plug-Ins/Components";
        yield return Path.Combine(Home, "Library", "Audio", "Plug-Ins", "Components");
    }

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static string CommonProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);

    static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
}