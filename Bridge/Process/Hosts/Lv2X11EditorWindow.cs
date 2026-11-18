using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// Linux LV2 UI host using the X11UI feature. The plugin creates its widget as
// a child of the X11 window we create; events are pumped on this UI thread.
internal sealed unsafe class Lv2X11EditorWindow : ILv2EditorWindow
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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void WriteProc(IntPtr controller, uint port, uint size, uint format, IntPtr buffer);

    static readonly WriteProc sWrite = Write;
    static readonly IntPtr sWritePointer = Marshal.GetFunctionPointerForDelegate(sWrite);
    static int sX11Threads;

    readonly Lv2InstrumentHost.Slot mSlot;
    readonly ConcurrentQueue<(uint Port, float Value)> mPortEvents = new();
    Thread? mThread;
    IntPtr mDisplay;
    IntPtr mWindow;
    IntPtr mUiLibrary;
    IntPtr mUiHandle;
    IntPtr mCleanup;
    IntPtr mPortEventPointer;
    IntPtr mIdleInterface;
    GCHandle mController;
    volatile bool mClosed;
    volatile bool mExited;
    bool mOpened;
    readonly ManualResetEventSlim mReady = new(false);

    public bool IsAlive => !mExited;

    Lv2X11EditorWindow(Lv2InstrumentHost.Slot slot) => mSlot = slot;

    public static bool TryOpen(Lv2InstrumentHost.Slot slot, string title, out Lv2X11EditorWindow? window)
    {
        var candidate = new Lv2X11EditorWindow(slot);

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
        if (Interlocked.Exchange(ref sX11Threads, 1) == 0)
        {
            try { XInitThreads(); } catch (Exception) { }
        }

        mThread = new Thread(() => Run(title))
        {
            IsBackground = true,
            Name = "lv2-x11-editor",
        };

        mThread.Start();
        return mReady.Wait(TimeSpan.FromSeconds(10)) && mOpened;
    }

    public void BringToFront()
    {
        if (mDisplay != IntPtr.Zero && mWindow != IntPtr.Zero)
        {
            XRaiseWindow(mDisplay, mWindow);
            XFlush(mDisplay);
        }
    }

    public void NotifyPortEvent(uint port, float value)
        => mPortEvents.Enqueue((port, value));

    public void Dispose()
    {
        if (mClosed)
            return;

        mClosed = true;

        try { mThread?.Join(TimeSpan.FromSeconds(3)); } catch (Exception) { }
    }

    void Run(string title)
    {
        try
        {
            mDisplay = XOpenDisplay(IntPtr.Zero);

            if (mDisplay == IntPtr.Zero)
                return;

            int screen = XDefaultScreen(mDisplay);
            IntPtr root = XRootWindow(mDisplay, screen);
            mWindow = XCreateSimpleWindow(mDisplay, root, 80, 80, 640, 480, 0, 0, 0);

            if (mWindow == IntPtr.Zero)
                return;

            XSelectInput(mDisplay, mWindow, 0xFFFF);
            XStoreName(mDisplay, mWindow, title);

            IntPtr deleteAtom = XInternAtom(mDisplay, "WM_DELETE_WINDOW", 0);
            IntPtr protocols = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(protocols, deleteAtom);
            XSetWMProtocols(mDisplay, mWindow, protocols, 1);
            Marshal.FreeHGlobal(protocols);

            XMapWindow(mDisplay, mWindow);
            XFlush(mDisplay);

            OpenUi();
        }
        catch (Exception)
        {
            mOpened = false;
        }

        mReady.Set();

        if (!mOpened)
        {
            CleanupUi();
            CloseDisplay();
            mExited = true;
            return;
        }

        IntPtr eventBuffer = Marshal.AllocHGlobal(256);

        try
        {
            while (!mClosed)
            {
                while (XPending(mDisplay) > 0)
                {
                    XNextEvent(mDisplay, eventBuffer);
                    int type = Marshal.ReadInt32(eventBuffer);

                    // DestroyNotify or WM_DELETE ClientMessage.
                    if (type == 17 || type == 33)
                    {
                        mClosed = true;
                        break;
                    }
                }

                while (!mClosed && mPortEvents.TryDequeue(out var portEvent))
                    SendPortEvent(portEvent);

                Idle();
                XFlush(mDisplay);
                Thread.Sleep(15);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(eventBuffer);
            CleanupUi();
            CloseDisplay();
            mExited = true;
        }
    }

    void OpenUi()
    {
        if (string.IsNullOrEmpty(mSlot.UiBinary))
            return;

        mUiLibrary = NativeLibrary.Load(mSlot.UiBinary!);

        if (!NativeLibrary.TryGetExport(mUiLibrary, "lv2ui_descriptor", out IntPtr entry))
            return;

        var descriptorFn = (delegate* unmanaged[Cdecl]<uint, IntPtr>)entry;
        IntPtr descriptor = descriptorFn(0);

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
            IntPtr features = mSlot.FeatureSet.CreateX11UiFeatures(mDisplay, mWindow);
            mUiHandle = instantiate(descriptor, pluginUri, bundlePath, sWritePointer,
                GCHandle.ToIntPtr(mController), (IntPtr)(&widget), features);
            mCleanup = ui->Cleanup;
            mPortEventPointer = ui->PortEvent;
            mIdleInterface = Lv2Native.GetUiIdleInterface(ui->ExtensionData);

            if (mUiHandle != IntPtr.Zero && widget != IntPtr.Zero)
                XMapWindow(mDisplay, widget);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pluginUri);
            Marshal.FreeCoTaskMem(bundlePath);
        }

        XFlush(mDisplay);
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
        mPortEventPointer = IntPtr.Zero;
        mIdleInterface = IntPtr.Zero;

        if (mController.IsAllocated)
            mController.Free();

        if (mUiLibrary != IntPtr.Zero)
        {
            try { NativeLibrary.Free(mUiLibrary); } catch (Exception) { }
            mUiLibrary = IntPtr.Zero;
        }
    }

    void CloseDisplay()
    {
        if (mDisplay == IntPtr.Zero)
            return;

        if (mWindow != IntPtr.Zero)
        {
            try { XDestroyWindow(mDisplay, mWindow); } catch (Exception) { }
            mWindow = IntPtr.Zero;
        }

        try { XCloseDisplay(mDisplay); } catch (Exception) { }
        mDisplay = IntPtr.Zero;
    }

    static void Write(IntPtr controller, uint port, uint size, uint format, IntPtr buffer)
    {
        if (controller == IntPtr.Zero || size != sizeof(float) || buffer == IntPtr.Zero)
            return;

        try
        {
            var window = (Lv2X11EditorWindow)GCHandle.FromIntPtr(controller).Target!;
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

    void SendPortEvent((uint Port, float Value) portEvent)
    {
        if (mUiHandle == IntPtr.Zero || mPortEventPointer == IntPtr.Zero)
            return;

        float value = portEvent.Value;
        var callback = (delegate* unmanaged[Cdecl]<IntPtr, uint, uint, uint, IntPtr, void>)mPortEventPointer;
        callback(mUiHandle, portEvent.Port, sizeof(float), 0, (IntPtr)(&value));
    }

    const int XInit = 0;

    [DllImport("libX11.so.6")]
    static extern int XInitThreads();

    [DllImport("libX11.so.6")]
    static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport("libX11.so.6")]
    static extern int XDefaultScreen(IntPtr display);

    [DllImport("libX11.so.6")]
    static extern IntPtr XRootWindow(IntPtr display, int screen);

    [DllImport("libX11.so.6")]
    static extern IntPtr XCreateSimpleWindow(IntPtr display, IntPtr parent, int x, int y,
        uint width, uint height, uint borderWidth, ulong border, ulong background);

    [DllImport("libX11.so.6")]
    static extern int XSelectInput(IntPtr display, IntPtr window, long mask);

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    static extern int XStoreName(IntPtr display, IntPtr window, string name);

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    static extern IntPtr XInternAtom(IntPtr display, string name, int onlyIfExists);

    [DllImport("libX11.so.6")]
    static extern int XSetWMProtocols(IntPtr display, IntPtr window, IntPtr protocols, int count);

    [DllImport("libX11.so.6")]
    static extern int XMapWindow(IntPtr display, IntPtr window);

    [DllImport("libX11.so.6")]
    static extern int XRaiseWindow(IntPtr display, IntPtr window);

    [DllImport("libX11.so.6")]
    static extern int XFlush(IntPtr display);

    [DllImport("libX11.so.6")]
    static extern int XPending(IntPtr display);

    [DllImport("libX11.so.6")]
    static extern int XNextEvent(IntPtr display, IntPtr eventBuffer);

    [DllImport("libX11.so.6")]
    static extern int XDestroyWindow(IntPtr display, IntPtr window);

    [DllImport("libX11.so.6")]
    static extern int XCloseDisplay(IntPtr display);
}