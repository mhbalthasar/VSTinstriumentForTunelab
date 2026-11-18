using System;
using System.Collections.Generic;
using System.Globalization;

namespace TuneLab.VstInstruments;

internal static class StereoModes
{
    public const string MonoSum = "mono_sum";
    public const string MonoSumMinus3Db = "mono_sum_minus3db";
    public const string Left = "left";
    public const string Right = "right";

    public static string Normalize(string? value) => value switch
    {
        MonoSumMinus3Db => MonoSumMinus3Db,
        Left => Left,
        Right => Right,
        _ => MonoSum,
    };
}

internal sealed class VstInstrumentSettings
{
    public const double MinSampleRate = 8000;
    public const double MaxSampleRate = 192000;

    public double SampleRate { get; set; } = 48000;
    public int BlockSize { get; set; } = 512;
    public int PreRollMs { get; set; } = 50;
    public int TailMs { get; set; } = 2000;
    public int ScanDepth { get; set; } = 6;
    public string StereoMode { get; set; } = StereoModes.MonoSum;

    public List<string> ClapPaths { get; set; } = [];
    public List<string> Vst2Paths { get; set; } = [];
    public List<string> Vst3Paths { get; set; } = [];
    public List<string> Lv2Paths { get; set; } = [];
    public List<string> AuPaths { get; set; } = [];

    public VstInstrumentSettings Clone() => new()
    {
        SampleRate = SampleRate,
        BlockSize = BlockSize,
        PreRollMs = PreRollMs,
        TailMs = TailMs,
        ScanDepth = ScanDepth,
        StereoMode = StereoMode,
        ClapPaths = [.. ClapPaths],
        Vst2Paths = [.. Vst2Paths],
        Vst3Paths = [.. Vst3Paths],
        Lv2Paths = [.. Lv2Paths],
        AuPaths = [.. AuPaths],
    };

    public bool SameAs(VstInstrumentSettings other)
    {
        return SampleRate.Equals(other.SampleRate)
            && BlockSize == other.BlockSize
            && PreRollMs == other.PreRollMs
            && TailMs == other.TailMs
            && ScanDepth == other.ScanDepth
            && string.Equals(StereoMode, other.StereoMode, StringComparison.Ordinal)
            && SamePaths(ClapPaths, other.ClapPaths)
            && SamePaths(Vst2Paths, other.Vst2Paths)
            && SamePaths(Vst3Paths, other.Vst3Paths)
            && SamePaths(Lv2Paths, other.Lv2Paths)
            && SamePaths(AuPaths, other.AuPaths);
    }

    static bool SamePaths(List<string> a, List<string> b)
    {
        if (a.Count != b.Count)
            return false;

        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    public static double ParseSampleRate(string? text, double fallback)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? Math.Clamp(value, MinSampleRate, MaxSampleRate)
            : fallback;

    public static int ParseBlockSize(string? text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? Math.Clamp(value, 64, 4096)
            : fallback;
}