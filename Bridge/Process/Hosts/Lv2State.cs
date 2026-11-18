using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// Optional LV2 state:StateInterface bridge. The plugin stores properties
// through a host callback; we serialize the property map into the same opaque
// blob shape the rest of the extension already uses. Plugins without the
// extension simply report no state.
internal static unsafe class Lv2State
{
    public const string InterfaceUri = "http://lv2plug.in/ns/ext/state#interface";
    const uint StateSuccess = 0;
    const uint Magic = 0x32564C54; // "TLV2"
    const uint Version = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Interface
    {
        public IntPtr Save;
        public IntPtr Restore;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int StoreProc(IntPtr handle, uint key, IntPtr value, UIntPtr size, uint type, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr RetrieveProc(IntPtr handle, uint key, out UIntPtr size, out uint type, out uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int SaveProc(IntPtr instance, IntPtr store, IntPtr handle, uint flags, IntPtr features);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int RestoreProc(IntPtr instance, IntPtr retrieve, IntPtr handle, uint flags, IntPtr features);

    static readonly StoreProc sStore = Store;
    static readonly RetrieveProc sRetrieve = Retrieve;
    static readonly IntPtr sStorePointer = Marshal.GetFunctionPointerForDelegate(sStore);
    static readonly IntPtr sRetrievePointer = Marshal.GetFunctionPointerForDelegate(sRetrieve);

    public sealed class Map : IDisposable
    {
        public sealed class Entry
        {
            public uint Type;
            public uint Flags;
            public IntPtr Value;
            public UIntPtr Size;
        }

        readonly Dictionary<uint, Entry> mEntries = [];
        GCHandle mHandle;

        public Map()
        {
            mHandle = GCHandle.Alloc(this);
        }

        public IntPtr Handle => GCHandle.ToIntPtr(mHandle);

        public void Store(uint key, IntPtr value, UIntPtr size, uint type, uint flags)
        {
            Remove(key);

            int length = checked((int)size);
            IntPtr copy = length > 0 ? Marshal.AllocHGlobal(length) : IntPtr.Zero;

            if (length > 0 && value != IntPtr.Zero)
                Buffer.MemoryCopy((void*)value, (void*)copy, length, length);

            mEntries[key] = new Entry
            {
                Type = type,
                Flags = flags,
                Value = copy,
                Size = size,
            };
        }

        public Entry? Retrieve(uint key)
            => mEntries.TryGetValue(key, out var entry) ? entry : null;

        public void Clear()
        {
            foreach (var entry in mEntries.Values)
            {
                if (entry.Value != IntPtr.Zero)
                    Marshal.FreeHGlobal(entry.Value);
            }

            mEntries.Clear();
        }

        void Remove(uint key)
        {
            if (mEntries.Remove(key, out var entry) && entry.Value != IntPtr.Zero)
                Marshal.FreeHGlobal(entry.Value);
        }

        public byte[] ToBlob()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(mEntries.Count);

            foreach (var pair in mEntries)
            {
                writer.Write(pair.Key);
                writer.Write(pair.Value.Type);
                writer.Write(pair.Value.Flags);
                writer.Write((uint)pair.Value.Size);
                var bytes = new byte[(int)pair.Value.Size];

                if (pair.Value.Value != IntPtr.Zero && bytes.Length > 0)
                    Marshal.Copy(pair.Value.Value, bytes, 0, bytes.Length);

                writer.Write(bytes);
            }

            writer.Flush();
            return stream.ToArray();
        }

        public static Map FromBlob(byte[] blob)
        {
            var map = new Map();

            try
            {
                using var stream = new MemoryStream(blob, writable: false);
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

                if (reader.ReadUInt32() != Magic || reader.ReadUInt32() != Version)
                    return map;

                uint count = reader.ReadUInt32();

                for (uint i = 0; i < count && stream.Position < stream.Length; i++)
                {
                    uint key = reader.ReadUInt32();
                    uint type = reader.ReadUInt32();
                    uint flags = reader.ReadUInt32();
                    uint size = reader.ReadUInt32();
                    int sizeSigned = (int)size;

                    if (sizeSigned < 0 || sizeSigned > 16 * 1024 * 1024)
                        break;

                    byte[] bytes = reader.ReadBytes(sizeSigned);
                    IntPtr value = bytes.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(bytes.Length);

                    if (bytes.Length > 0)
                        Marshal.Copy(bytes, 0, value, bytes.Length);

                    map.mEntries[key] = new Entry
                    {
                        Type = type,
                        Flags = flags,
                        Value = value,
                        Size = (UIntPtr)(uint)bytes.Length,
                    };
                }
            }
            catch (Exception)
            {
            }

            return map;
        }

        public void Dispose()
        {
            Clear();

            if (mHandle.IsAllocated)
                mHandle.Free();
        }
    }

    public static Interface? GetInterface(IntPtr descriptor, IntPtr extensionData)
    {
        if (extensionData == IntPtr.Zero)
            return null;

        try
        {
            var getExtension = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)extensionData;
            IntPtr uri = Marshal.StringToCoTaskMemUTF8(InterfaceUri);

            try
            {
                IntPtr data = getExtension(uri);

                if (data == IntPtr.Zero)
                    return null;

                var value = *(Interface*)data;
                return value.Save != IntPtr.Zero && value.Restore != IntPtr.Zero ? value : null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(uri);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static byte[]? Save(IntPtr instance, Interface state, IntPtr features = default)
    {
        if (state.Save == IntPtr.Zero)
            return null;

        var map = new Map();

        try
        {
            var save = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, uint, IntPtr, int>)state.Save;
            int status = save(instance, sStorePointer, map.Handle, 0, features);
            return status == StateSuccess ? map.ToBlob() : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            map.Dispose();
        }
    }

    public static bool Restore(IntPtr instance, Interface state, byte[] blob, IntPtr features = default)
    {
        if (state.Restore == IntPtr.Zero)
            return false;

        using var map = Map.FromBlob(blob);

        try
        {
            var restore = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, uint, IntPtr, int>)state.Restore;
            return restore(instance, sRetrievePointer, map.Handle, 0, features) == StateSuccess;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static int Store(IntPtr handle, uint key, IntPtr value, UIntPtr size, uint type, uint flags)
    {
        if (handle == IntPtr.Zero)
            return 1;

        try
        {
            ((Map)GCHandle.FromIntPtr(handle).Target!).Store(key, value, size, type, flags);
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    static IntPtr Retrieve(IntPtr handle, uint key, out UIntPtr size, out uint type, out uint flags)
    {
        size = UIntPtr.Zero;
        type = 0;
        flags = 0;

        if (handle == IntPtr.Zero)
            return IntPtr.Zero;

        try
        {
            var entry = ((Map)GCHandle.FromIntPtr(handle).Target!).Retrieve(key);

            if (entry == null)
                return IntPtr.Zero;

            size = entry.Size;
            type = entry.Type;
            flags = entry.Flags;
            return entry.Value;
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }
}