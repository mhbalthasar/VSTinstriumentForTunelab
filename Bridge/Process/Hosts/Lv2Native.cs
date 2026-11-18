using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// LV2 is a C ABI, so this host talks to the bundle binary directly. The feature
// set below covers atom MIDI/control ports, state, worker, options/buf-size and
// the platform UI features used by the bridge.
internal static unsafe class Lv2Native
{
    public const uint AtomSequenceType = 0;
    public const uint MidiEventType = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct Descriptor
    {
        public IntPtr Uri;
        public IntPtr Instantiate;
        public IntPtr ConnectPort;
        public IntPtr Activate;
        public IntPtr Run;
        public IntPtr Deactivate;
        public IntPtr Cleanup;
        public IntPtr ExtensionData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Feature
    {
        public IntPtr Uri;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UridMap
    {
        public IntPtr Handle;
        public IntPtr Map;
        public IntPtr Unmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UridUnmap
    {
        public IntPtr Handle;
        public IntPtr Unmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Atom
    {
        public uint Size;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AtomSequence
    {
        public Atom Atom;
        public uint EventCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AtomEvent
    {
        public long Frames;
        public uint Subframes;
        public uint Type;
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Option
    {
        public uint Context;
        public uint Subject;
        public uint Key;
        public uint Size;
        public uint Type;
        public IntPtr Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WorkerSchedule
    {
        public IntPtr Handle;
        public IntPtr ScheduleWork;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WorkerInterface
    {
        public IntPtr Work;
        public IntPtr WorkResponse;
        public IntPtr EndRun;
    }

    public const string UridMapUri = "http://lv2plug.in/ns/ext/urid#map";
    public const string UridUnmapUri = "http://lv2plug.in/ns/ext/urid#unmap";
    public const string AtomSequenceUri = "http://lv2plug.in/ns/ext/atom#Sequence";
    public const string MidiEventUri = "http://lv2plug.in/ns/ext/midi#MidiEvent";
    public const string AtomChunkUri = "http://lv2plug.in/ns/ext/atom#Chunk";
    public const string AtomFloatUri = "http://lv2plug.in/ns/ext/atom#Float";
    public const string AtomIntUri = "http://lv2plug.in/ns/ext/atom#Int";
    public const string AtomObjectUri = "http://lv2plug.in/ns/ext/atom#Object";
    public const string AtomUridUri = "http://lv2plug.in/ns/ext/atom#URID";
    public const string AtomStringUri = "http://lv2plug.in/ns/ext/atom#String";
    public const string AtomBoolUri = "http://lv2plug.in/ns/ext/atom#Bool";
    public const string AtomDoubleUri = "http://lv2plug.in/ns/ext/atom#Double";
    public const string OptionsOptionsUri = "http://lv2plug.in/ns/ext/options#options";
    public const string BufSizeBoundedUri = "http://lv2plug.in/ns/ext/buf-size#boundedBlockLength";
    public const string BufSizeFixedUri = "http://lv2plug.in/ns/ext/buf-size#fixedBlockLength";
    public const string BufSizeMaxUri = "http://lv2plug.in/ns/ext/buf-size#maxBlockLength";
    public const string BufSizeMinUri = "http://lv2plug.in/ns/ext/buf-size#minBlockLength";
    public const string BufSizeNominalUri = "http://lv2plug.in/ns/ext/buf-size#nominalBlockLength";
    public const string BufSizeSequenceUri = "http://lv2plug.in/ns/ext/buf-size#sequenceSize";
    public const string WorkerScheduleUri = "http://lv2plug.in/ns/ext/worker#schedule";
    public const string WorkerInterfaceUri = "http://lv2plug.in/ns/ext/worker#interface";
    public const string TimePositionUri = "http://lv2plug.in/ns/ext/time#Position";
    public const string TimeFrameUri = "http://lv2plug.in/ns/ext/time#frame";
    public const string TimeSpeedUri = "http://lv2plug.in/ns/ext/time#speed";
    public const string TimeBeatsPerMinuteUri = "http://lv2plug.in/ns/ext/time#beatsPerMinute";
    public const string TimeBeatUnitUri = "http://lv2plug.in/ns/ext/time#beatUnit";
    public const string TimeBeatsPerBarUri = "http://lv2plug.in/ns/ext/time#beatsPerBar";
    public const string TimeBarBeatUri = "http://lv2plug.in/ns/ext/time#barBeat";
    public const string AtomLongUri = "http://lv2plug.in/ns/ext/atom#Long";
    public const string UiWindowsUri = "http://lv2plug.in/ns/extensions/ui#WindowsUI";
    public const string UiX11Uri = "http://lv2plug.in/ns/extensions/ui#X11UI";
    public const string UiCocoaUri = "http://lv2plug.in/ns/extensions/ui#CocoaUI";
    public const string UiParentUri = "http://lv2plug.in/ns/extensions/ui#parent";
    public const string UiIdleInterfaceUri = "http://lv2plug.in/ns/extensions/ui#idleInterface";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint UridMapProc(IntPtr handle, IntPtr uri);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr UridUnmapProc(IntPtr handle, uint urid);

    static readonly UridMapProc sMap = MapCallback;
    static readonly UridUnmapProc sUnmap = UnmapCallback;
    static readonly IntPtr sMapPointer = Marshal.GetFunctionPointerForDelegate(sMap);
    static readonly IntPtr sUnmapPointer = Marshal.GetFunctionPointerForDelegate(sUnmap);

    public static IntPtr UridMapPointer => sMapPointer;

    public static IntPtr UridUnmapPointer => sUnmapPointer;

    static uint MapCallback(IntPtr handle, IntPtr uri)
        => ((Lv2FeatureSet)GCHandle.FromIntPtr(handle).Target!).Map(Marshal.PtrToStringUTF8(uri) ?? string.Empty);

    static IntPtr UnmapCallback(IntPtr handle, uint urid)
        => ((Lv2FeatureSet)GCHandle.FromIntPtr(handle).Target!).Unmap(urid);

    public static void* AtomBody(IntPtr atom)
        => (byte*)atom + sizeof(Atom);

    public static int EventSize => sizeof(AtomEvent);

    // extension_data() is a UI descriptor entry point. Querying it before the
    // first idle call is allowed by the LV2 UI specification.
    public static IntPtr GetUiIdleInterface(IntPtr extensionData)
    {
        if (extensionData == IntPtr.Zero)
            return IntPtr.Zero;

        IntPtr uri = Marshal.StringToCoTaskMemUTF8(UiIdleInterfaceUri);

        try
        {
            var query = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)extensionData;
            return query(uri);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeCoTaskMem(uri);
        }
    }
}

// Per-slot feature set. It owns every unmanaged string/structure so the plugin
// can keep the pointers for its whole lifetime.
internal sealed unsafe class Lv2FeatureSet : IDisposable
{
    readonly Dictionary<string, uint> mUrids = new(StringComparer.Ordinal);
    readonly Dictionary<uint, IntPtr> mUnmapStrings = [];
    readonly List<IntPtr> mAllocations = [];
    readonly List<IntPtr> mFeaturePointers = [];
    GCHandle mHandle;
    IntPtr mFeatures;
    IntPtr mMapStruct;
    IntPtr mUnmapStruct;
    uint mNextUrid = 1;
    bool mDisposed;

    public Lv2FeatureSet(int blockSize, Lv2Worker? worker)
    {
        mHandle = GCHandle.Alloc(this);

        mMapStruct = Alloc(sizeof(Lv2Native.UridMap));
        var map = (Lv2Native.UridMap*)mMapStruct;
        map->Handle = GCHandle.ToIntPtr(mHandle);
        map->Map = Lv2Native.UridMapPointer;
        map->Unmap = Lv2Native.UridUnmapPointer;

        mUnmapStruct = Alloc(sizeof(Lv2Native.UridUnmap));
        var unmap = (Lv2Native.UridUnmap*)mUnmapStruct;
        unmap->Handle = GCHandle.ToIntPtr(mHandle);
        unmap->Unmap = Lv2Native.UridUnmapPointer;

        var features = new List<(string Uri, IntPtr Data)>
        {
            (Lv2Native.UridMapUri, mMapStruct),
            (Lv2Native.UridUnmapUri, mUnmapStruct),
            (Lv2Native.BufSizeBoundedUri, IntPtr.Zero),
        };

        // Options are how buf-size exposes the render limits to a plugin. The
        // values live in unmanaged memory for the plugin's lifetime.
        uint intType = Urid(Lv2Native.AtomIntUri);
        int optionCount = 4;
        IntPtr options = Alloc((optionCount + 1) * sizeof(Lv2Native.Option));
        var option = (Lv2Native.Option*)options;

        SetOption(option + 0, Lv2Native.BufSizeMaxUri, intType, (uint)blockSize);
        SetOption(option + 1, Lv2Native.BufSizeMinUri, intType, 32);
        SetOption(option + 2, Lv2Native.BufSizeNominalUri, intType, (uint)blockSize);
        SetOption(option + 3, Lv2Native.BufSizeSequenceUri, intType, 65536);
        option[optionCount] = default;

        features.Add((Lv2Native.OptionsOptionsUri, options));

        if (worker != null)
            features.Add((Lv2Native.WorkerScheduleUri, worker.ScheduleData));

        mFeatures = Alloc((features.Count + 1) * IntPtr.Size);

        for (int i = 0; i < features.Count; i++)
        {
            IntPtr feature = Alloc(sizeof(Lv2Native.Feature));
            var value = (Lv2Native.Feature*)feature;
            value->Uri = Utf8(features[i].Uri);
            value->Data = features[i].Data;
            *(IntPtr*)(mFeatures + i * IntPtr.Size) = feature;
            mFeaturePointers.Add(feature);
        }

        *(IntPtr*)(mFeatures + features.Count * IntPtr.Size) = IntPtr.Zero;

        // Pre-map the atom/MIDI types most plugins ask for during instantiate.
        Urid(Lv2Native.AtomSequenceUri);
        Urid(Lv2Native.MidiEventUri);
        Urid(Lv2Native.AtomChunkUri);
        Urid(Lv2Native.AtomFloatUri);
        Urid(Lv2Native.AtomObjectUri);
        Urid(Lv2Native.AtomUridUri);
        Urid(Lv2Native.AtomStringUri);
        Urid(Lv2Native.AtomBoolUri);
        Urid(Lv2Native.AtomDoubleUri);
    }

    void SetOption(Lv2Native.Option* option, string key, uint type, uint value)
    {
        IntPtr valuePointer = Alloc(sizeof(uint));
        *(uint*)valuePointer = value;

        option->Context = 0; // LV2_OPTIONS_INSTANCE
        option->Subject = 0;
        option->Key = Urid(key);
        option->Size = sizeof(uint);
        option->Type = type;
        option->Value = valuePointer;
    }

    public IntPtr Features => mFeatures;

    public uint Urid(string uri)
    {
        if (mUrids.TryGetValue(uri, out uint value))
            return value;

        value = mNextUrid++;
        mUrids[uri] = value;
        return value;
    }

    public uint Map(string uri) => Urid(uri);

    public IntPtr Unmap(uint urid)
    {
        if (mUnmapStrings.TryGetValue(urid, out IntPtr pointer))
            return pointer;

        string? uri = null;

        foreach (var pair in mUrids)
        {
            if (pair.Value == urid)
            {
                uri = pair.Key;
                break;
            }
        }

        if (uri == null)
            return IntPtr.Zero;

        pointer = Utf8(uri);
        mUnmapStrings[urid] = pointer;
        return pointer;
    }

    IntPtr Alloc(int bytes)
    {
        IntPtr pointer = Marshal.AllocHGlobal(bytes);
        mAllocations.Add(pointer);
        return pointer;
    }

    IntPtr Utf8(string text)
    {
        int bytes = System.Text.Encoding.UTF8.GetByteCount(text) + 1;
        IntPtr pointer = Alloc(bytes);
        var span = new Span<byte>((void*)pointer, bytes);
        System.Text.Encoding.UTF8.GetBytes(text, span);
        span[^1] = 0;
        return pointer;
    }

    public IntPtr CreateUiFeatures(IntPtr parentWindow)
        => BuildUiFeatures(Lv2Native.UiWindowsUri, parentWindow, parentWindow);

    public IntPtr CreateX11UiFeatures(IntPtr display, IntPtr window)
        => BuildUiFeatures(Lv2Native.UiX11Uri, display, window);

    public IntPtr CreateCocoaUiFeatures(IntPtr view)
        => BuildUiFeatures(Lv2Native.UiCocoaUri, view, view);

    IntPtr BuildUiFeatures(string uiUri, IntPtr uiData, IntPtr parent)
    {
        IntPtr uiFeature = Alloc(sizeof(Lv2Native.Feature));
        var uiValue = (Lv2Native.Feature*)uiFeature;
        uiValue->Uri = Utf8(uiUri);
        uiValue->Data = uiData;

        IntPtr parentFeature = Alloc(sizeof(Lv2Native.Feature));
        var parentValue = (Lv2Native.Feature*)parentFeature;
        parentValue->Uri = Utf8(Lv2Native.UiParentUri);
        parentValue->Data = parent;

        // The host advertises idle support with a NULL data pointer. The UI
        // returns the actual LV2UI_Idle_Interface through extension_data().
        IntPtr idleFeature = Alloc(sizeof(Lv2Native.Feature));
        var idleValue = (Lv2Native.Feature*)idleFeature;
        idleValue->Uri = Utf8(Lv2Native.UiIdleInterfaceUri);
        idleValue->Data = IntPtr.Zero;

        int count = mFeaturePointers.Count;
        IntPtr array = Alloc((count + 4) * IntPtr.Size);

        for (int i = 0; i < count; i++)
            *(IntPtr*)(array + i * IntPtr.Size) = mFeaturePointers[i];

        *(IntPtr*)(array + count * IntPtr.Size) = uiFeature;
        *(IntPtr*)(array + (count + 1) * IntPtr.Size) = parentFeature;
        *(IntPtr*)(array + (count + 2) * IntPtr.Size) = idleFeature;
        *(IntPtr*)(array + (count + 3) * IntPtr.Size) = IntPtr.Zero;
        return array;
    }

    public void Dispose()
    {
        if (mDisposed)
            return;

        mDisposed = true;

        foreach (IntPtr pointer in mAllocations)
        {
            try
            {
                Marshal.FreeHGlobal(pointer);
            }
            catch (Exception)
            {
            }
        }

        mAllocations.Clear();

        if (mHandle.IsAllocated)
            mHandle.Free();
    }
}