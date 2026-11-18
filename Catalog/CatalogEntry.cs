using System;
using System.Collections.Generic;
using TuneLab.SDK;

namespace TuneLab.VstInstruments.Catalog;

// One selectable instrument in the extension catalog.
//
// The id is stable across machines and projects:
//   clap:<descriptor-id>
//   vst2:<file-name>/<unique-id>
//   vst3:<bundle>/<class>   (future)
//   lv2:<plugin-uri>        (future)
//   au:<subtype>/<maker>    (future)
internal sealed class CatalogEntry
{
    public string Id { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public string PluginId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Locator { get; set; } = string.Empty;
    public bool IsInstrument { get; set; }
    public bool IsAvailable { get; set; } = true;
    public string? UnavailableReason { get; set; }
    public int Bits { get; set; }
    public string Arch { get; set; } = string.Empty;
    public string Rid { get; set; } = string.Empty;
    public long MtimeTicks { get; set; }
    public long Size { get; set; }
    public int LatencySamples { get; set; }
    public int TailSamples { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? PluginId : Name;

    public string FormatDisplay => Format switch
    {
        Bridge.PluginFormats.Clap => "CLAP",
        Bridge.PluginFormats.Vst2 => "VST2",
        Bridge.PluginFormats.Vst3 => "VST3",
        Bridge.PluginFormats.Lv2 => "LV2",
        Bridge.PluginFormats.Au => "AU",
        _ => Format,
    };
}

internal sealed class CatalogCache
{
    public int Version { get; set; } = 1;
    public List<CatalogEntry> Entries { get; set; } = [];
}

internal sealed class PluginCatalog
{
    public static readonly PluginCatalog Empty = new(
        new Dictionary<string, CatalogEntry>(StringComparer.Ordinal),
        []);

    public PluginCatalog(IReadOnlyDictionary<string, CatalogEntry> byId,
        IReadOnlyList<InstrumentSourceLayoutItem> layout)
    {
        ById = byId;
        Layout = layout;
    }

    public IReadOnlyDictionary<string, CatalogEntry> ById { get; }

    public IReadOnlyList<InstrumentSourceLayoutItem> Layout { get; }
}