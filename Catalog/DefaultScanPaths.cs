using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TuneLab.VstInstruments.Catalog;

// Standard plugin locations, seeded into the editable scan-path lists. They
// are normal values once seeded: the user can delete a row or clear a format,
// and an empty list then really means "scan nothing". This is deliberately not
// a hidden scan fallback.
internal static class DefaultScanPaths
{
    static readonly StringComparer sPathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public static void Apply(VstInstrumentSettings settings)
    {
        settings.ClapPaths = Clean(Clap());
        settings.Vst2Paths = Clean(Vst2());
        settings.Vst3Paths = Clean(Vst3());
        settings.Lv2Paths = Clean(Lv2());
        settings.AuPaths = Clean(Au());
    }

    public static IEnumerable<string> Clap()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Combine(CommonFiles, "CLAP");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Library/Audio/Plug-Ins/CLAP";
            yield return Combine(Home, "Library", "Audio", "Plug-Ins", "CLAP");
        }
        else
        {
            yield return "/usr/lib/clap";
            yield return "/usr/local/lib/clap";
            yield return Combine(Home, ".clap");
        }
    }

    public static IEnumerable<string> Vst2()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Combine(CommonFiles, "VST2");
            yield return Combine(CommonFilesX86, "VST2");
            yield return Combine(ProgramFiles, "VSTPlugins");
            yield return Combine(ProgramFilesX86, "VSTPlugins");
            yield return Combine(ProgramFiles, "Steinberg", "VSTPlugins");
            yield return Combine(ProgramFilesX86, "Steinberg", "VSTPlugins");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Library/Audio/Plug-Ins/VST";
            yield return Combine(Home, "Library", "Audio", "Plug-Ins", "VST");
        }
        else
        {
            yield return "/usr/lib/vst";
            yield return "/usr/local/lib/vst";
            yield return Combine(Home, ".vst");
        }
    }

    public static IEnumerable<string> Vst3()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Combine(CommonFiles, "VST3");
            yield return Combine(CommonFilesX86, "VST3");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Library/Audio/Plug-Ins/VST3";
            yield return Combine(Home, "Library", "Audio", "Plug-Ins", "VST3");
        }
        else
        {
            yield return "/usr/lib/vst3";
            yield return "/usr/local/lib/vst3";
            yield return Combine(Home, ".vst3");
        }
    }

    public static IEnumerable<string> Lv2()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Combine(CommonFiles, "LV2");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Library/Audio/Plug-Ins/LV2";
            yield return Combine(Home, "Library", "Audio", "Plug-Ins", "LV2");
        }
        else
        {
            yield return "/usr/lib/lv2";
            yield return "/usr/local/lib/lv2";
            yield return Combine(Home, ".lv2");
        }
    }

    public static IEnumerable<string> Au()
    {
        if (!OperatingSystem.IsMacOS())
            yield break;

        yield return "/Library/Audio/Plug-Ins/Components";
        yield return Combine(Home, "Library", "Audio", "Plug-Ins", "Components");
    }

    static List<string> Clean(IEnumerable<string> paths)
        => paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(sPathComparer)
            .ToList();

    static string Combine(string root, params string[] parts)
        => string.IsNullOrWhiteSpace(root) ? string.Empty : Path.Combine([root, .. parts]);

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static string CommonFiles => Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);

    static string CommonFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);

    static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    static string ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
}