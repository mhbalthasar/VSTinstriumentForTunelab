using System;
using System.IO;
using System.Linq;

namespace TuneLab.VstInstruments.Catalog;

// Reads just enough of a plugin binary to decide which helper can load it.
//
// The extension scans from the TuneLab process, which may be x64 while a VST2
// plugin is x86. Loading it in the x64 helper would fail with 0x8007000B, so
// the catalog would never list a perfectly usable 32-bit VST2. The header
// probe lets the loader start the matching helper instead.
//
// A null result means "unknown": the caller falls back to the host RID and the
// helper reports the real load error.
internal static class PluginBinaryProbe
{
    public static string? DetectRid(string path, string format)
    {
        string? binary = ResolveBinary(path, format);

        if (binary == null)
            return null;

        try
        {
            using var stream = File.OpenRead(binary);
            var header = new byte[64];

            if (stream.Read(header, 0, header.Length) < 4)
                return null;

            if (header[0] == 'M' && header[1] == 'Z')
                return PortableExecutable(stream, header);

            if (header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
                return Elf(header);

            uint magic = BitConverter.ToUInt32(header, 0);

            if (magic is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE)
                return MachO(header, magic);
        }
        catch (Exception)
        {
        }

        return null;
    }

    // On macOS and in VST3 bundles the loadable binary is inside the bundle.
    // Windows/Linux CLAP and VST2 are single files.
    static string? ResolveBinary(string path, string format)
    {
        if (File.Exists(path))
            return path;

        if (!Directory.Exists(path))
            return null;

        string macos = Path.Combine(path, "Contents", "MacOS");

        if (Directory.Exists(macos))
            return Directory.EnumerateFiles(macos).FirstOrDefault();

        if (string.Equals(format, Bridge.PluginFormats.Vst3, StringComparison.OrdinalIgnoreCase))
        {
            foreach (string architecture in new[] { "x86_64-win", "x86-win", "arm64-win" })
            {
                string directory = Path.Combine(path, "Contents", architecture);

                if (!Directory.Exists(directory))
                    continue;

                string? candidate = Directory.EnumerateFiles(directory, "*.vst3").FirstOrDefault();

                if (candidate != null)
                    return candidate;
            }
        }

        // An LV2 bundle is a directory; its loadable binary has a platform
        // suffix (.dll/.so/.dylib). The probe must see that binary, otherwise a
        // 32-bit LV2 would be sent to the x64 helper and fail with 0x8007000B.
        if (string.Equals(format, Bridge.PluginFormats.Lv2, StringComparison.OrdinalIgnoreCase))
        {
            foreach (string candidate in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                string extension = Path.GetExtension(candidate);

                if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".so", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    static string? PortableExecutable(FileStream stream, byte[] header)
    {
        int peOffset = BitConverter.ToInt32(header, 0x3C);

        // A corrupt offset must not make us seek across the file.
        if (peOffset <= 0 || peOffset > 16 * 1024 * 1024 || peOffset + 6 > stream.Length)
            return null;

        stream.Position = peOffset;
        var coff = new byte[6];

        if (stream.Read(coff, 0, coff.Length) < coff.Length)
            return null;

        int machine = BitConverter.ToUInt16(coff, 4);

        return machine switch
        {
            0x014C => "win-x86",
            0x8664 => "win-x64",
            0xAA64 => "win-arm64",
            _ => null,
        };
    }

    static string? Elf(byte[] header)
    {
        int elfClass = header[4];
        bool little = header[5] == 1;
        int machine = little
            ? BitConverter.ToUInt16(header, 18)
            : (header[18] << 8) | header[19];

        if (elfClass == 2)
        {
            return machine switch
            {
                62 => "linux-x64",
                183 => "linux-arm64",
                _ => null,
            };
        }

        return machine switch
        {
            3 => "linux-x86",
            40 => "linux-arm",
            _ => null,
        };
    }

    static string? MachO(byte[] header, uint magic)
    {
        bool is64 = magic is 0xFEEDFACF or 0xCFFAEDFE;
        bool little = magic is 0xFEEDFACF or 0xFEEDFACE;

        uint cpu = BitConverter.ToUInt32(header, 4);

        if (!little)
            cpu = Reverse(cpu);

        if (is64)
        {
            return cpu switch
            {
                0x01000007 => "osx-x64",
                0x0100000C => "osx-arm64",
                _ => null,
            };
        }

        return cpu switch
        {
            7 => "osx-x86",
            12 => "osx-arm",
            _ => null,
        };
    }

    static uint Reverse(uint value)
        => (value >> 24) | ((value >> 8) & 0x0000FF00) | ((value << 8) & 0x00FF0000) | (value << 24);
}