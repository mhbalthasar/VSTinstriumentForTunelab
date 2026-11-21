using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// Native Gtk3 LV2 UI host. The actual window and toolkit event loop live
// in a small vendored Suil shim, never in Avalonia. The bridge only forwards
// port events and close/show requests.
internal sealed unsafe class Lv2SuilEditorWindow : ILv2EditorWindow
{
    [StructLayout(LayoutKind.Sequential)]
    struct OpenOptions
    {
        public IntPtr Features;
        public IntPtr Callbacks;
        public IntPtr Controller;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Callbacks
    {
        public IntPtr Write;
        public IntPtr Index;
        public IntPtr Subscribe;
        public IntPtr Unsubscribe;
        public IntPtr Touch;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void WriteProc(IntPtr controller, uint port, uint size, uint protocol, IntPtr buffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint IndexProc(IntPtr controller, IntPtr symbol);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint SubscribeProc(IntPtr controller, uint port, uint protocol, IntPtr features);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint UnsubscribeProc(IntPtr controller, uint port, uint protocol, IntPtr features);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void TouchProc(IntPtr controller, uint port, byte grabbed);

    static readonly WriteProc sWrite = WriteCallback;
    static readonly IndexProc sIndex = IndexCallback;
    static readonly SubscribeProc sSubscribe = SubscribeCallback;
    static readonly UnsubscribeProc sUnsubscribe = UnsubscribeCallback;
    static readonly TouchProc sTouch = TouchCallback;

    readonly Lv2InstrumentHost.Slot mSlot;
    IntPtr mLibrary;
    IntPtr mHandle;
    GCHandle mController;
    IntPtr mCallbacks;
    IntPtr mClose;
    IntPtr mPortEvent;
    IntPtr mShow;

    Lv2SuilEditorWindow(Lv2InstrumentHost.Slot slot)
    {
        mSlot = slot;
    }

    public bool IsAlive => mHandle != IntPtr.Zero;

    public static bool TryOpen(Lv2InstrumentHost.Slot slot, string title, out Lv2SuilEditorWindow? window)
    {
        window = null;

        if (!OperatingSystem.IsLinux()
            || Environment.GetEnvironmentVariable("TUNELAB_VSTI_DISABLE_SUIL") == "1"
            || string.IsNullOrEmpty(slot.Plugin.UiType)
            || string.IsNullOrEmpty(slot.Plugin.UiUri)
            || string.IsNullOrEmpty(slot.UiBinary))
        {
            return false;
        }

        if (slot.Plugin.UiType != Lv2Native.UiGtk3Uri)
            return false;

        const string libraryName = "libtlvsti_suil_gtk3.so";
        const string prefix = "tlvsti_gtk3_";

        string libraryPath = Path.Combine(AppContext.BaseDirectory, libraryName);

        if (!File.Exists(libraryPath))
            return false;

        IntPtr library = IntPtr.Zero;

        try
        {
            library = NativeLibrary.Load(libraryPath);

            if (!NativeLibrary.TryGetExport(library, prefix + "open", out IntPtr open))
            {
                // Keep the shim loaded: its toolkit thread (and static state)
                // outlives individual editor windows.
                return false;
            }

            string closeName = prefix + "close";
            string portName = prefix + "port_event";
            string showName = prefix + "show";

            if (!NativeLibrary.TryGetExport(library, closeName, out IntPtr close)
                || !NativeLibrary.TryGetExport(library, portName, out IntPtr portEvent)
                || !NativeLibrary.TryGetExport(library, showName, out IntPtr show))
            {
                // Keep the shim loaded: its toolkit thread (and static state)
                // outlives individual editor windows.
                return false;
            }

            var candidate = new Lv2SuilEditorWindow(slot)
            {
                mLibrary = library,
                mClose = close,
                mPortEvent = portEvent,
                mShow = show,
            };

            candidate.mController = GCHandle.Alloc(candidate);
            IntPtr callbacks = Marshal.AllocHGlobal(sizeof(Callbacks));
            bool keepCallbacks = false;

            try
            {
                var callbackData = (Callbacks*)callbacks;
                callbackData->Write = Marshal.GetFunctionPointerForDelegate(sWrite);
                callbackData->Index = Marshal.GetFunctionPointerForDelegate(sIndex);
                callbackData->Subscribe = Marshal.GetFunctionPointerForDelegate(sSubscribe);
                callbackData->Unsubscribe = Marshal.GetFunctionPointerForDelegate(sUnsubscribe);
                callbackData->Touch = Marshal.GetFunctionPointerForDelegate(sTouch);

                var options = new OpenOptions
                {
                    Features = slot.FeatureSet.Features,
                    Callbacks = callbacks,
                    Controller = GCHandle.ToIntPtr(candidate.mController),
                };

                IntPtr pluginUri = Marshal.StringToCoTaskMemUTF8(slot.Plugin.Uri);
                IntPtr uiUri = Marshal.StringToCoTaskMemUTF8(slot.Plugin.UiUri);
                IntPtr uiType = Marshal.StringToCoTaskMemUTF8(slot.Plugin.UiType);
                IntPtr bundle = Marshal.StringToCoTaskMemUTF8(slot.Plugin.BundlePath);
                IntPtr binary = Marshal.StringToCoTaskMemUTF8(slot.UiBinary!);
                IntPtr titlePtr = Marshal.StringToCoTaskMemUTF8(title);
                IntPtr error = Marshal.AllocHGlobal(1024);
                IntPtr handle = IntPtr.Zero;

                try
                {
                    var openFn = (delegate* unmanaged[Cdecl]<byte*, byte*, byte*, byte*, byte*,
                        byte*, OpenOptions*, IntPtr*, byte*, int, int>)open;

                    int result = openFn((byte*)pluginUri, (byte*)uiUri, (byte*)uiType,
                        (byte*)bundle, (byte*)binary, (byte*)titlePtr, &options, &handle,
                        (byte*)error, 1024);

                    if (result != 0 || handle == IntPtr.Zero)
                    {
                        candidate.FreeController();
                        // Keep the shim loaded: its toolkit thread (and static state)
                // outlives individual editor windows.
                        return false;
                    }

                    candidate.mHandle = handle;
                    candidate.mCallbacks = callbacks;
                    keepCallbacks = true;

                    window = candidate;
                    return true;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pluginUri);
                    Marshal.FreeCoTaskMem(uiUri);
                    Marshal.FreeCoTaskMem(uiType);
                    Marshal.FreeCoTaskMem(bundle);
                    Marshal.FreeCoTaskMem(binary);
                    Marshal.FreeCoTaskMem(titlePtr);
                    Marshal.FreeHGlobal(error);
                }
            }
            finally
            {
                if (!keepCallbacks)
                    Marshal.FreeHGlobal(callbacks);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void BringToFront()
    {
        if (mHandle != IntPtr.Zero && mShow != IntPtr.Zero)
        {
            var show = (delegate* unmanaged[Cdecl]<IntPtr, void>)mShow;
            show(mHandle);
        }
    }

    public void NotifyPortEvent(uint port, float value)
    {
        if (mHandle != IntPtr.Zero && mPortEvent != IntPtr.Zero)
        {
            var portEvent = (delegate* unmanaged[Cdecl]<IntPtr, uint, float, void>)mPortEvent;
            portEvent(mHandle, port, value);
        }
    }

    public void Dispose()
    {
        if (mHandle != IntPtr.Zero && mClose != IntPtr.Zero)
        {
            var close = (delegate* unmanaged[Cdecl]<IntPtr, void>)mClose;
            close(mHandle);
            mHandle = IntPtr.Zero;
        }

        FreeController();

        if (mCallbacks != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(mCallbacks);
            mCallbacks = IntPtr.Zero;
        }

        // Do not unload the native shim here: its Gtk3 thread keeps
        // running for the lifetime of the helper process.
        mLibrary = IntPtr.Zero;
    }

    void FreeController()
    {
        if (mController.IsAllocated)
            mController.Free();
    }

    void WritePort(uint port, uint size, uint protocol, IntPtr buffer)
    {
        if (protocol != 0 || size != sizeof(float) || buffer == IntPtr.Zero)
            return;

        if (!mSlot.Ports.TryGetValue((int)port, out var runtime) || runtime.Buffer == IntPtr.Zero)
            return;

        *(float*)runtime.Buffer = *(float*)buffer;
    }

    static Lv2SuilEditorWindow? From(IntPtr controller)
        => GCHandle.FromIntPtr(controller).Target as Lv2SuilEditorWindow;

    static void WriteCallback(IntPtr controller, uint port, uint size, uint protocol, IntPtr buffer)
        => From(controller)?.WritePort(port, size, protocol, buffer);

    static uint IndexCallback(IntPtr controller, IntPtr symbol)
    {
        var window = From(controller);
        if (window == null || symbol == IntPtr.Zero)
            return uint.MaxValue;

        string? name = Marshal.PtrToStringUTF8(symbol);

        if (name == null)
            return uint.MaxValue;

        foreach (var pair in window.mSlot.Ports)
        {
            if (string.Equals(pair.Value.Description.Symbol, name, StringComparison.Ordinal))
                return (uint)pair.Key;
        }

        return uint.MaxValue;
    }

    static uint SubscribeCallback(IntPtr controller, uint port, uint protocol, IntPtr features)
        => 0;

    static uint UnsubscribeCallback(IntPtr controller, uint port, uint protocol, IntPtr features)
        => 0;

    static void TouchCallback(IntPtr controller, uint port, byte grabbed)
    {
    }
}