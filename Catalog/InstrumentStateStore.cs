using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TuneLab.VstInstruments.Catalog;

// Optional automatic state cache, keyed by instrument id.
//
// The SDK does not let a button or session write a part property, so inline
// state capture is manual (Capture State shows base64). This store makes the
// captured blob survive anyway: “Capture State” writes it here, and a session
// uses it when the part's vsti_state_<instrumentId> field is empty. The field wins, so
// a project can stay self-contained by pasting the blob into it.
internal static class InstrumentStateStore
{
    public static string StatesDirectory => Path.Combine(CatalogPaths.DataRoot, "states");

    public static string FilePath(string instrumentId)
        => Path.Combine(StatesDirectory, Hash(instrumentId) + ".state");

    public static bool TryLoad(string instrumentId, out byte[] data)
    {
        data = [];

        try
        {
            string path = FilePath(instrumentId);

            if (!File.Exists(path))
                return false;

            data = File.ReadAllBytes(path);
            return data.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Save(string instrumentId, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(StatesDirectory);
            File.WriteAllBytes(FilePath(instrumentId), data);
        }
        catch (Exception)
        {
        }
    }

    public static void Clear(string instrumentId)
    {
        try
        {
            string path = FilePath(instrumentId);

            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    static string Hash(string instrumentId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instrumentId))).ToLowerInvariant();
}