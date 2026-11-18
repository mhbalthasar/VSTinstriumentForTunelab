using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// macOS LV2 UI host using the CocoaUI feature. AppKit is not thread-safe; the
// window, NSView and LV2 UI are created and run on a dedicated thread with an
// NSApplication run loop. This is best-effort and needs a macOS smoke test.
internal sealed unsafe class Lv2CocoaEditorWindow : ILv2EditorWindow
{
    [StructLayout(LayoutKind.Sequential)]
    struct UiDescriptor
    {
        public IntPtr Uri;
        public IntPtr Instantiate;
        public IntPtr Cleanup;
        public IntPtr PortEvent;
        public IntPtr ExtensionData;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CGPoint { public double X; public double Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct CGSize { public double Width; public double Height; }

    [StructLayout(LayoutKind.Sequential)]
    struct CGRect { public CGPoint Origin; public CGSize Size; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void WriteProc(IntPtr controller, uint port, uint size, uint format, IntPtr buffer);

    static readonly WriteProc sWrite = Write;
    static readonly IntPtr sWritePointer = Marshal.GetFunctionPointerForDelegate(sWrite);

    readonly Lv2InstrumentHost.Slot mSlot;
    Thread? mThread;
    IntPtr mApp;
    IntPtr mWindow;
    IntPtr mContentView;
    IntPtr mUiLibrary;
    IntPtr mUiHandle;
    IntPtr mCleanup;
    IntPtr mIdleInterface;
    GCHandle mController;
    volatile bool mClosed;
    volatile bool mExited;
    bool mOpened;
    readonly ManualResetEventSlim mReady = new(false);

    public bool IsAlive => !mExited;

    Lv2CocoaEditorWindow(Lv2InstrumentHost.Slot slot) => mSlot = slot;

    public static bool TryOpen(Lv2InstrumentHost.Slot slot, string title, out Lv2CocoaEditorWindow? window)
    {
        var candidate = new Lv2CocoaEditorWindow(slot);

        if (!candidate.Start(title))
        {
            candidate.Dispose();
            window = null;
            return false;
        }

        window = candidate;
        return true;
    }

    bool Start(string title)
    {
        mThread = new Thread(() => Run(title))
        {
            IsBackground = true,
            Name = "lv2-cocoa-editor",
        };

        mThread.Start();
        return mReady.Wait(TimeSpan.FromSeconds(10)) && mOpened;
    }

    public void BringToFront()
    {
        if (mWindow != IntPtr.Zero)
            SendVoid(mWindow, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
    }

    public void NotifyPortEvent(uint port, float value)
    {
        // AppKit's run loop owns this thread; an arbitrary-thread port_event is
        // not safe. Parameter synchronization is left to the plugin UI's own
        // polling for this first macOS implementation.
        _ = port;
        _ = value;
    }

    public void Dispose()
    {
        if (mClosed)
            return;

        mClosed = true;

        // The UI thread polls mClosed between short NSRunLoop slices, so no
        // cross-thread AppKit call is needed here.
        try { mThread?.Join(TimeSpan.FromSeconds(3)); } catch (Exception) { }
    }

    void Run(string title)
    {
        try
        {
            IntPtr nsApplication = ObjC("NSApplication");
            mApp = SendPtr(nsApplication, Sel("sharedApplication"));

            if (mApp == IntPtr.Zero)
                return;

            SendVoidLong(mApp, Sel("setActivationPolicy:"), 0);
            SendNoArgs(mApp, Sel("finishLaunching"));

            IntPtr nsWindow = ObjC("NSWindow");
            IntPtr nsView = ObjC("NSView");
            mWindow = SendRect(nsWindow, Sel("alloc"));
            var frame = new CGRect { Origin = new CGPoint { X = 80, Y = 80 }, Size = new CGSize { Width = 640, Height = 480 } };
            mWindow = SendInitWindow(mWindow,
                Sel("initWithContentRect:styleMask:backing:defer:"), frame, 15, 2, 0);

            mContentView = SendRect(nsView, Sel("alloc"));
            mContentView = SendInitView(mContentView, Sel("initWithFrame:"), frame);

            SendVoidPtr(mWindow, Sel("setContentView:"), mContentView);
            SendVoidBool(mWindow, Sel("setReleasedWhenClosed:"), 0);
            SendVoid(mWindow, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);

            OpenUi();

            if (!mOpened)
            {
                mReady.Set();
                CleanupUi();

                if (mWindow != IntPtr.Zero)
                    SendNoArgs(mWindow, Sel("close"));

                mExited = true;
                return;
            }
        }
        catch (Exception)
        {
            mOpened = false;
        }

        mReady.Set();

        if (!mOpened)
        {
            CleanupUi();
            mExited = true;
            return;
        }

        try
        {
            // Use short NSRunLoop slices instead of -[NSApplication run] so the
            // UI idle interface and mClosed can be serviced regularly.
            IntPtr runLoop = SendPtr(ObjC("NSRunLoop"), Sel("currentRunLoop"));
            IntPtr nsDate = ObjC("NSDate");
            IntPtr isVisible = Sel("isVisible");
            IntPtr runUntilDate = Sel("runUntilDate:");
            IntPtr dateWithInterval = Sel("dateWithTimeIntervalSinceNow:");

            while (!mClosed && SendBool(mWindow, isVisible) != 0)
            {
                IntPtr date = SendDouble(nsDate, dateWithInterval, 0.016);
                SendVoidPtr(runLoop, runUntilDate, date);
                Idle();
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            CleanupUi();

            if (mWindow != IntPtr.Zero)
                SendNoArgs(mWindow, Sel("close"));

            mExited = true;
        }
    }

    void OpenUi()
    {
        if (string.IsNullOrEmpty(mSlot.UiBinary))
            return;

        mUiLibrary = NativeLibrary.Load(mSlot.UiBinary!);

        IntPtr descriptor = Lv2Native.FindUiDescriptor(mUiLibrary, mSlot.Plugin.UiUri);

        if (descriptor == IntPtr.Zero)
            return;

        var ui = (UiDescriptor*)descriptor;
        var instantiate = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)ui->Instantiate;

        if (instantiate == null)
            return;

        IntPtr pluginUri = Marshal.StringToCoTaskMemUTF8(mSlot.Plugin.Uri);
        IntPtr bundlePath = Marshal.StringToCoTaskMemUTF8(mSlot.Plugin.BundlePath);
        IntPtr widget = IntPtr.Zero;
        mController = GCHandle.Alloc(this);

        try
        {
            IntPtr features = mSlot.FeatureSet.CreateCocoaUiFeatures(mContentView);
            mUiHandle = instantiate(descriptor, pluginUri, bundlePath, sWritePointer,
                GCHandle.ToIntPtr(mController), (IntPtr)(&widget), features);
            mCleanup = ui->Cleanup;
            mIdleInterface = Lv2Native.GetUiIdleInterface(ui->ExtensionData);

            if (mUiHandle != IntPtr.Zero && widget != IntPtr.Zero)
                SendVoidPtr(mContentView, Sel("addSubview:"), widget);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pluginUri);
            Marshal.FreeCoTaskMem(bundlePath);
        }

        mOpened = mUiHandle != IntPtr.Zero;
    }

    void CleanupUi()
    {
        if (mUiHandle != IntPtr.Zero && mCleanup != IntPtr.Zero)
        {
            try
            {
                var cleanup = (delegate* unmanaged[Cdecl]<IntPtr, void>)mCleanup;
                cleanup(mUiHandle);
            }
            catch (Exception)
            {
            }
        }

        mUiHandle = IntPtr.Zero;
        mCleanup = IntPtr.Zero;
        mIdleInterface = IntPtr.Zero;

        if (mController.IsAllocated)
            mController.Free();

        if (mUiLibrary != IntPtr.Zero)
        {
            try { NativeLibrary.Free(mUiLibrary); } catch (Exception) { }
            mUiLibrary = IntPtr.Zero;
        }
    }

    static void Write(IntPtr controller, uint port, uint size, uint format, IntPtr buffer)
    {
        if (controller == IntPtr.Zero || size != sizeof(float) || buffer == IntPtr.Zero)
            return;

        try
        {
            var window = (Lv2CocoaEditorWindow)GCHandle.FromIntPtr(controller).Target!;
            window.SetControl(port, *(float*)buffer);
        }
        catch (Exception)
        {
        }
    }

    void SetControl(uint port, float value)
    {
        if (mSlot.ControlParameters.TryGetValue((int)port, out var control))
            *(float*)control.Buffer = value;
    }

    void Idle()
    {
        if (mUiHandle == IntPtr.Zero || mIdleInterface == IntPtr.Zero)
            return;

        IntPtr idle = *(IntPtr*)mIdleInterface;

        if (idle == IntPtr.Zero)
            return;

        try
        {
            var callback = (delegate* unmanaged[Cdecl]<IntPtr, int>)idle;
            callback(mUiHandle);
        }
        catch (Exception)
        {
        }
    }

    static IntPtr ObjC(string name) => objc_getClass(name);

    static IntPtr Sel(string name) => sel_registerName(name);

    [DllImport("libobjc.dylib", CharSet = CharSet.Ansi)]
    static extern IntPtr objc_getClass(string name);

    [DllImport("libobjc.dylib", CharSet = CharSet.Ansi)]
    static extern IntPtr sel_registerName(string name);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    static extern IntPtr SendPtr(IntPtr receiver, IntPtr selector);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    static extern void SendNoArgs(IntPtr receiver, IntPtr selector);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    static extern void SendVoidBool(IntPtr receiver, IntPtr selector, byte argument);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    static extern void SendVoidLong(IntPtr receiver, IntPtr selector, long argument);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    static extern byte SendBool(IntPtr receiver, IntPtr selector);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    static extern IntPtr SendDouble(IntPtr receiver, IntPtr selector, double argument);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    static extern IntPtr SendRect(IntPtr receiver, IntPtr selector);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    static extern IntPtr SendInitWindow(IntPtr receiver, IntPtr selector, CGRect frame,
        ulong styleMask, ulong backing, byte defer);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    static extern IntPtr SendInitView(IntPtr receiver, IntPtr selector, CGRect frame);
}