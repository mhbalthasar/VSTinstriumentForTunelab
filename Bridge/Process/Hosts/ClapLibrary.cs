using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// One loaded .clap library, reference counted.
//
// CLAP requires clap_entry.init() to run once per library per process, and
// deinit() only after every instance is gone. The bridge both scans (load,
// ask for descriptors, unload) and plays (instances stay loaded), so without a
// count a scan would deinit and free the library under a live instance.
//
// The bridge is single-threaded (one message at a time), so no lock is needed.
// If the bridge ever gains threads, this assumption must be revisited.
internal static unsafe class ClapLibrary
{
    sealed class Loaded
    {
        public IntPtr Handle;
        public ClapNative.ClapPluginEntry Entry;
        public int RefCount;
    }

    // Key = resolved binary path. Windows and macOS are case-insensitive; Linux
    // is case-sensitive.
    static readonly Dictionary<string, Loaded> sLoaded = new(
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    // A lease: Acquire increments, Dispose decrements. Instances and scans each
    // hold one.
    internal sealed class Lease : IDisposable
    {
        readonly string mKey;
        bool mDisposed;

        internal Lease(string key, IntPtr handle, ClapNative.ClapPluginEntry entry, ClapNative.ClapPluginFactory* factory)
        {
            mKey = key;
            Handle = handle;
            Entry = entry;
            Factory = factory;
        }

        public IntPtr Handle { get; }

        public ClapNative.ClapPluginEntry Entry { get; }

        // The factory pointer is only valid while this lease is alive.
        public ClapNative.ClapPluginFactory* Factory { get; }

        public void Dispose()
        {
            if (mDisposed)
                return;

            mDisposed = true;
            Release(mKey);
        }
    }

    /// <summary>
    /// Load and init a .clap, or increment the count if it is already loaded.
    /// Returns null with a user-facing reason on failure.
    /// </summary>
    public static Lease? Acquire(string path, out string? reason)
    {
        reason = null;

        string? binary = ResolveBinary(path);

        if (binary == null)
        {
            reason = "the .clap has no loadable binary";
            return null;
        }

        if (sLoaded.TryGetValue(binary, out var known))
        {
            known.RefCount++;
            return Build(binary, known);
        }

        IntPtr library;

        try
        {
            library = NativeLibrary.Load(binary);
        }
        catch (Exception ex)
        {
            reason = "cannot load the plugin library: " + ex.Message;
            return null;
        }

        if (!NativeLibrary.TryGetExport(library, "clap_entry", out IntPtr entryPointer))
        {
            NativeLibrary.Free(library);
            reason = "not a CLAP plugin (no clap_entry export)";
            return null;
        }

        var entry = *(ClapNative.ClapPluginEntry*)entryPointer;

        if (entry.Version.Major != ClapNative.SupportedMajor)
        {
            NativeLibrary.Free(library);
            reason = $"built for CLAP {entry.Version.Major}.{entry.Version.Minor}, this host speaks {ClapNative.SupportedMajor}.x";
            return null;
        }

        if (entry.Init == null || entry.GetFactory == null)
        {
            NativeLibrary.Free(library);
            reason = "the plugin declares an incomplete entry point";
            return null;
        }

        byte[] pathBytes = ClapNative.Utf8(path);
        byte initialized;

        fixed (byte* pathPointer = pathBytes)
            initialized = entry.Init(pathPointer);

        if (initialized == 0)
        {
            NativeLibrary.Free(library);
            reason = "the plugin refused to initialize";
            return null;
        }

        var loaded = new Loaded
        {
            Handle = library,
            Entry = entry,
            RefCount = 1,
        };

        var lease = Build(binary, loaded, out string? factoryReason);

        if (lease == null)
        {
            // init succeeded but there is no factory: clean up so no half-loaded
            // entry remains.
            if (entry.Deinit != null)
                entry.Deinit();

            NativeLibrary.Free(library);
            reason = factoryReason ?? "the plugin has no clap.plugin-factory";
            return null;
        }

        sLoaded[binary] = loaded;
        return lease;
    }

    static Lease? Build(string key, Loaded loaded) => Build(key, loaded, out _);

    static Lease? Build(string key, Loaded loaded, out string? reason)
    {
        reason = null;

        byte[] factoryId = ClapNative.Utf8(ClapNative.PluginFactoryId);
        IntPtr factoryPointer;

        fixed (byte* idPointer = factoryId)
            factoryPointer = loaded.Entry.GetFactory(idPointer);

        if (factoryPointer == IntPtr.Zero)
        {
            reason = "the plugin has no clap.plugin-factory";
            return null;
        }

        var factory = (ClapNative.ClapPluginFactory*)factoryPointer;

        if (factory->GetPluginCount == null || factory->GetPluginDescriptor == null
            || factory->CreatePlugin == null)
        {
            reason = "the plugin factory is incomplete";
            return null;
        }

        return new Lease(key, loaded.Handle, loaded.Entry, factory);
    }

    static void Release(string key)
    {
        if (!sLoaded.TryGetValue(key, out var loaded))
            return;

        if (--loaded.RefCount > 0)
            return;

        sLoaded.Remove(key);

        try
        {
            if (loaded.Entry.Deinit != null)
                loaded.Entry.Deinit();
        }
        catch (Exception)
        {
            // The library is about to be unloaded; nothing useful can be done
            // with a plugin exception here.
        }

        NativeLibrary.Free(loaded.Handle);
    }

    /// <summary>
    /// On Windows/Linux a .clap is a file; on macOS it is a bundle directory
    /// (Foo.clap/Contents/MacOS/Foo). The loader wants the inner binary.
    /// </summary>
    public static string? ResolveBinary(string path)
    {
        if (File.Exists(path))
            return path;

        if (!Directory.Exists(path))
            return null;

        string macos = Path.Combine(path, "Contents", "MacOS");

        if (!Directory.Exists(macos))
            return null;

        foreach (string candidate in Directory.EnumerateFiles(macos))
            return candidate;

        return null;
    }
}
