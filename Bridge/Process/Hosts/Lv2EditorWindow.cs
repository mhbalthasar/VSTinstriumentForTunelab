using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// Windows LV2 UI host. The UI descriptor receives a parent HWND through the
// ui:WindowsUI feature; the plugin creates a child window inside our frame.
// LV2 UI must be created, pumped and destroyed on one UI thread, so this class
// owns an STA thread just like the VST2/VST3 editor hosts.
internal sealed unsafe class Lv2EditorWindow : ILv2EditorWindow
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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void CleanupProc(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void PortEventProc(IntPtr handle, uint port, uint size, uint format, IntPtr buffer);

    static readonly WriteProc sWrite = Write;
    static readonly IntPtr sWritePointer = Marshal.GetFunctionPointerForDelegate(sWrite);

    readonly Lv2InstrumentHost.Slot mSlot;
    readonly ConcurrentQueue<(uint Port, float Value)> mPortEvents = new();
    Thread? mThread;
    IntPtr mHwnd;
    IntPtr mWidget;
    IntPtr mLibrary;
    IntPtr mUiHandle;
    IntPtr mPortEventPointer;
    IntPtr mIdleInterface;
    GCHandle mController;
    volatile bool mClosed;
    volatile bool mExited;
    bool mOpened;
    readonly ManualResetEventSlim mReady = new(false);

    public bool IsAlive => !mExited;

    public void NotifyPortEvent(uint port, float value)
        => mPortEvents.Enqueue((port, value));

    Lv2EditorWindow(Lv2InstrumentHost.Slot slot)
    {
        mSlot = slot;
    }

    public static bool TryOpen(Lv2InstrumentHost.Slot slot, string title, out Lv2EditorWindow? window)
    {
        var candidate = new Lv2EditorWindow(slot);
        if (!candidate.Start(title))
        {
            candidate.Dispose();
            window = null;
            return false;
        }

        window = candidate;
        return true;
    }

    public void BringToFront()
    {
        if (mHwnd != IntPtr.Zero)
        {
            ShowWindow(mHwnd, SwRestore);
            SetForegroundWindow(mHwnd);
        }
    }

    public void Dispose()
    {
        if (mClosed)
            return;

        mClosed = true;

        if (mHwnd != IntPtr.Zero)
            PostMessage(mHwnd, WmClose, IntPtr.Zero, IntPtr.Zero);

        try
        {
            mThread?.Join(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
        }
    }

    bool Start(string title)
    {
        mThread = new Thread(() => Run(title))
        {
            IsBackground = true,
            Name = "lv2-editor",
        };

        try
        {
            if (OperatingSystem.IsWindows())
                mThread.SetApartmentState(ApartmentState.STA);
        }
        catch (Exception)
        {
        }

        mThread.Start();
        return mReady.Wait(TimeSpan.FromSeconds(10)) && mOpened;
    }

    void Run(string title)
    {
        string className = "TuneLabLv2Editor_" + Guid.NewGuid().ToString("N");
        WndProcDelegate wndProc = WindowProc;
        IntPtr hInstance = GetModuleHandle(null);

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = hInstance,
            hCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512),
            lpszClassName = className,
        };

        if (RegisterClassEx(ref wndClass) == 0)
        {
            mReady.Set();
            return;
        }

        mHwnd = CreateWindowEx(0, className, title,
            WsOverlappedWindow, 80, 80, 640, 480,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (mHwnd == IntPtr.Zero)
        {
            mReady.Set();
            return;
        }

        ShowWindow(mHwnd, SwShow);
        UpdateWindow(mHwnd);
        SetForegroundWindow(mHwnd);

        try
        {
            OpenUi(title);
        }
        catch (Exception)
        {
            mOpened = false;
        }

        mReady.Set();

        if (!mOpened)
        {
            CleanupUi();

            if (mHwnd != IntPtr.Zero)
            {
                DestroyWindow(mHwnd);
                mHwnd = IntPtr.Zero;
            }

            mExited = true;
            return;
        }

        BringToFront();

        while (!mClosed)
        {
            if (!PeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
            {
                while (!mClosed && mPortEvents.TryDequeue(out var portEvent))
                    SendPortEvent(portEvent);

                Idle();
                Thread.Sleep(15);
                continue;
            }

            if (message.message == WmQuit)
                break;

            TranslateMessage(ref message);
            DispatchMessage(ref message);
            Idle();
        }

        CleanupUi();
        mExited = true;
    }

    void OpenUi(string title)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(mSlot.UiBinary))
            return;

        mLibrary = NativeLibrary.Load(mSlot.UiBinary!);

        if (!NativeLibrary.TryGetExport(mLibrary, "lv2ui_descriptor", out IntPtr descriptorEntry))
            return;

        var descriptorFn = (delegate* unmanaged[Cdecl]<uint, IntPtr>)descriptorEntry;
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
            IntPtr features = mSlot.FeatureSet.CreateUiFeatures(mHwnd);

            mUiHandle = instantiate(descriptor, pluginUri, bundlePath, sWritePointer,
                GCHandle.ToIntPtr(mController), (IntPtr)(&widget), features);
            mCleanup = ui->Cleanup;
            mPortEventPointer = ui->PortEvent;
            mIdleInterface = Lv2Native.GetUiIdleInterface(ui->ExtensionData);
            mWidget = widget;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pluginUri);
            Marshal.FreeCoTaskMem(bundlePath);
        }

        if (mUiHandle == IntPtr.Zero)
            return;

        if (mWidget != IntPtr.Zero)
        {
            MoveWindow(mWidget, 0, 0, 640, 480, true);
            ShowWindow(mWidget, SwShow);
        }
        else
        {
            // Some UIs only remember the parent HWND and create the widget
            // later. A small visible frame still gives them a target.
            ShowWindow(mHwnd, SwShow);
        }

        mOpened = true;
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

        if (mLibrary != IntPtr.Zero)
        {
            try
            {
                NativeLibrary.Free(mLibrary);
            }
            catch (Exception)
            {
            }

            mLibrary = IntPtr.Zero;
        }
    }

    IntPtr mCleanup;

    static void Write(IntPtr controller, uint port, uint size, uint format, IntPtr buffer)
    {
        if (controller == IntPtr.Zero || size != sizeof(float) || buffer == IntPtr.Zero)
            return;

        try
        {
            var window = (Lv2EditorWindow)GCHandle.FromIntPtr(controller).Target!;
            float value = *(float*)buffer;
            window.SetControl(port, value);
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

    void DrainMessages()
    {
        while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    void SetControl(uint port, float value)
    {
        if (mSlot.ControlParameters.TryGetValue((int)port, out var control))
            *(float*)control.Buffer = value;
    }

    const int WsOverlappedWindow = 0x00CF0000;
    const int SwShow = 5;
    const int SwRestore = 9;
    const int WmClose = 0x0010;
    const int WmQuit = 0x0012;
    const uint PmRemove = 0x0001;

    delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmClose)
        {
            DestroyWindow(hwnd);
            return IntPtr.Zero;
        }

        if (message == 0x0002)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
        IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    static extern bool UpdateWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool PeekMessage(out MSG message, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr DispatchMessage(ref MSG message);
}