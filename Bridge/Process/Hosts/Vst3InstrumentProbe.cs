using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// OwnVST3's IsInstrument result is not reliable for every VST3 (Surge XT is a
// known false negative). VST3 puts the class subcategories in the binary as
// ASCII/UTF-16 literals, so a read-only marker probe is a useful second source.
// It never loads or executes plugin code.
internal static class Vst3InstrumentProbe
{
    static readonly string[] sBundleBinaryDirectories =
    [
        Path.Combine("Contents", "x86_64-win"),
        Path.Combine("Contents", "x86-win"),
        Path.Combine("Contents", "arm64-win"),
        Path.Combine("Contents", "x86_64-linux"),
        Path.Combine("Contents", "x86-linux"),
        Path.Combine("Contents", "arm64-linux"),
        Path.Combine("Contents", "MacOS"),
    ];

    public static bool LooksLikeInstrument(string path)
    {
        try
        {
            foreach (string binary in EnumerateBinaries(path))
            {
                if (ContainsMarker(binary, "Instrument|"))
                    return true;
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    static IEnumerable<string> EnumerateBinaries(string path)
    {
        if (File.Exists(path))
        {
            yield return path;
            yield break;
        }

        if (!Directory.Exists(path))
            yield break;

        // A .vst3/.component is a bundle: only the loadable binary carries the
        // class metadata. Searching resources would produce false positives.
        foreach (string relative in sBundleBinaryDirectories)
        {
            string directory = Path.Combine(path, relative);

            if (!Directory.Exists(directory))
                continue;

            foreach (string file in Directory.EnumerateFiles(directory))
            {
                string extension = Path.GetExtension(file);

                if (extension.Equals(".vst3", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".so", StringComparison.OrdinalIgnoreCase)
                    || extension.Length == 0)
                {
                    yield return file;
                }
            }
        }
    }

    static bool ContainsMarker(string file, string marker)
    {
        byte[] ascii = Encoding.ASCII.GetBytes(marker);
        byte[] utf16 = Encoding.Unicode.GetBytes(marker);
        int overlap = Math.Max(ascii.Length, utf16.Length) - 1;
        byte[] buffer = new byte[(1 << 20) + overlap];

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);

        int carry = 0;

        while (true)
        {
            int read = stream.Read(buffer, carry, buffer.Length - carry);

            if (read <= 0)
                return false;

            var chunk = buffer.AsSpan(0, carry + read);

            if (chunk.IndexOf(ascii) >= 0 || chunk.IndexOf(utf16) >= 0)
                return true;

            carry = Math.Min(overlap, chunk.Length);
            chunk[^carry..].CopyTo(buffer);
        }
    }
}