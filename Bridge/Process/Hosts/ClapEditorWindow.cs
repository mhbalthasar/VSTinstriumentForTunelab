using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// CLAP 插件编辑器的宿主窗口。**跑在桥进程里**，与 Vst2EditorWindow 同一套做法：
// 窗口必须由加载插件的那条进程创建（跨进程 SetParent 在 Windows 上不可靠：菜单、焦点、
// 拖放、DPI 全都会错），而插件 GUI 的 create / set_parent / show / destroy 又必须落在
// 同一条线程上、那条线程还得有消息循环。
//
// 【为什么另起一条 STA 线程】桥的主线程在等宿主的 process 请求（阻塞在共享环上），
// 不能去泵窗口消息；而且插件 GUI 的 create 可能很慢，卡在主线程上就是所有插入一起断音。
//
// 【线程检查】CLAP 规定 GUI 调用要在 main-thread 上。桥的 clap.thread-check 对
// "是不是主线程"一律回答"是"（见 ClapPluginHost），所以插件不会因为我们在另一条线程上
// 而推迟工作 —— 这是桥模式刻意接受的近似（同一进程内本来也只有一条"主"线程）。
//
// 【范围】P-C3 只做 Windows。Linux 要 X11 的 XID + 自己泵 X 事件，macOS 要 NSView，
// 那两条是后续阶段；其它平台如实报"没有界面"，界面自动退到参数面板。
internal sealed unsafe class ClapEditorWindow : IDisposable
{
    const int WmDestroy = 0x0002;
    const int WmSize = 0x0005;
    const int WmClose = 0x0010;
    const int WmApp = 0x8000;
    const int WmBringToFront = WmApp + 1;
    const int WmResize = WmApp + 2;
    const int WmShow = WmApp + 3;
    const int WmHide = WmApp + 4;

    const int SwHide = 0;
    const int SwShow = 5;
    const int SwRestore = 9;

    const uint SwpNoMove = 0x0002;
    const uint SwpNoZOrder = 0x0004;
    const uint SwpNoActivate = 0x0010;
    const uint SwpNoSize = 0x0001;
    const uint SwpShowWindow = 0x0040;
    const uint PmRemove = 0x0001;
    static readonly IntPtr HwndTopmost = new(-1);

    // "win32" 的字节串在 ClapNative 里（非托管、进程生命周期）：clap_window_t 的 api
    // 指针会交给插件，必须活过整个 GUI 会话。

    // 正在 create 的那个编辑器。插件的 create / show 里常会调 host_gui.request_resize
    // 报初始尺寸，那时槽位里还没挂上这个对象（TryOpen 还没返回）—— 用线程静态接住，
    // 否则插件报的尺寸会被丢掉、窗口按默认大小开。
    [ThreadStatic] static ClapEditorWindow? sCurrent;

    public static ClapEditorWindow? Current => sCurrent;

    readonly ClapNative.ClapPlugin* mPlugin;
    readonly ClapNative.ClapPluginGui* mGui;
    readonly string mTitle;

    Thread? mThread;
    IntPtr mHwnd;
    volatile bool mClosed;
    volatile bool mExited;
    volatile bool mCreated;
    volatile bool mGuiReady;
    volatile bool mShown;
    volatile int mChildCount;
    bool mFloating;
    bool mPrestarted;
    bool mMainThread;
    int mPresetWidth;
    int mPresetHeight;
    uint mThreadId;

    static readonly List<ClapEditorWindow> sMainThreadWindows = [];

    // create 期间插件报的尺寸（0 = 没报）。
    int mRequestedWidth;
    int mRequestedHeight;

    readonly ManualResetEventSlim mReady = new(false);
    bool mOpened;

    ClapEditorWindow(ClapNative.ClapPlugin* plugin, ClapNative.ClapPluginGui* gui, string title)
    {
        mPlugin = plugin;
        mGui = gui;
        mTitle = title;
    }

    // 桥的主线程调：起编辑器线程并等窗口开出来。
    // 返回 false = 插件没有 win32 界面 / create 或 set_parent 失败 / 超时。
    public static bool TryOpen(ClapNative.ClapPlugin* plugin, ClapNative.ClapPluginGui* gui, string title,
        out ClapEditorWindow? window, out string? reason)
    {
        window = null;
        reason = null;

        if (!OperatingSystem.IsWindows() || plugin == null || gui == null)
        {
            reason = "this plugin has no editor on this platform";
            return false;
        }

        var candidate = new ClapEditorWindow(plugin, gui, title);

        if (!candidate.Start())
        {
            reason = candidate.FailureReason ?? "the plugin refused to open its editor";
            candidate.Dispose();
            return false;
        }

        window = candidate;
        return true;
    }

    public string? FailureReason { get; private set; }

    // gui->create has already been called on the host main thread. This only
    // starts the editor thread that owns the window, set_parent, show and the
    // message loop.
    public static bool TryOpenPrecreated(ClapNative.ClapPlugin* plugin, ClapNative.ClapPluginGui* gui,
        string title, out ClapEditorWindow? window, out string? reason)
    {
        window = null;
        reason = null;

        var candidate = new ClapEditorWindow(plugin, gui, title)
        {
            mPrestarted = true,
        };

        // Some plugins block on get_size when it is called from a different
        // thread. Query it here, on the main thread that created the GUI.
        if (gui->GetSize != null)
        {
            uint w = 0;
            uint h = 0;

            try
            {
                if (gui->GetSize(plugin, &w, &h) != 0 && w >= 120 && w <= 4096 && h >= 80 && h <= 4096)
                {
                    candidate.mPresetWidth = (int)w;
                    candidate.mPresetHeight = (int)h;
                }
            }
            catch (Exception)
            {
            }
        }

        if (!candidate.Start())
        {
            reason = candidate.FailureReason ?? "the plugin refused to open its editor";
            candidate.Dispose();
            return false;
        }

        window = candidate;
        return true;
    }

    // Main-thread GUI: gui->create has already been called here, and this
    // creates the host window, set_parent and show on the same thread. The
    // bridge loop calls PumpAll() between protocol messages.
    public static bool TryOpenOnMainThread(ClapNative.ClapPlugin* plugin, ClapNative.ClapPluginGui* gui,
        string title, out ClapEditorWindow? window, out string? reason)
    {
        window = null;
        reason = null;

        if (!OperatingSystem.IsWindows() || plugin == null || gui == null)
        {
            reason = "this plugin has no editor on this platform";
            return false;
        }

        var candidate = new ClapEditorWindow(plugin, gui, title)
        {
            mPrestarted = true,
            mMainThread = true,
        };

        if (!candidate.CreateOnMainThread(out reason))
        {
            candidate.Dispose();
            return false;
        }

        lock (sMainThreadWindows)
            sMainThreadWindows.Add(candidate);

        window = candidate;
        return true;
    }

    // A floating GUI creates its own top-level window on this (main) thread;
    // the bridge loop pumps its messages.
    public static bool TryOpenFloatingOnMainThread(ClapNative.ClapPlugin* plugin, ClapNative.ClapPluginGui* gui,
        string title, out ClapEditorWindow? window, out string? reason)
    {
        window = null;
        reason = null;

        var candidate = new ClapEditorWindow(plugin, gui, title)
        {
            mPrestarted = true,
            mMainThread = true,
            mFloating = true,
        };

        if (!candidate.ShowFloatingOnMainThread(out reason))
        {
            candidate.Dispose();
            return false;
        }

        lock (sMainThreadWindows)
            sMainThreadWindows.Add(candidate);

        window = candidate;
        return true;
    }

    bool ShowFloatingOnMainThread(out string? reason)
    {
        reason = null;
        mCreated = true;

        try
        {
            bool shown = mGui->Show == null || mGui->Show(mPlugin) != 0;
            mShown = shown;

            if (!shown)
            {
                reason = "gui->show failed";
                return false;
            }

            mOpened = true;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    public static void PumpAll()
    {
        if (!OperatingSystem.IsWindows())
            return;

        ClapEditorWindow[] snapshot;

        lock (sMainThreadWindows)
            snapshot = [.. sMainThreadWindows];

        foreach (var window in snapshot)
            window.Pump();
    }

    void Pump()
    {
        if (mExited)
            return;

        while (PeekMessage(out MSG message, IntPtr.Zero, 0, 0, PmRemove))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    bool CreateOnMainThread(out string? reason)
    {
        reason = null;
        sCurrent = this;

        try
        {
            string className = "TuneLabClapEditor_" + Guid.NewGuid().ToString("N");
            WndProcDelegate wndProc = WindowProc;

            var wndClass = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
                hInstance = GetModuleHandle(null),
                hCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512),
                lpszClassName = className,
            };

            if (RegisterClassEx(ref wndClass) == 0)
            {
                reason = "RegisterClassEx failed";
                return false;
            }

            mCreated = true;

            int width;
            int height;

            if (mPresetWidth > 0)
            {
                width = mPresetWidth;
                height = mPresetHeight;
            }
            else if (!TryGetSize(out width, out height))
            {
                width = 800;
                height = 600;
            }

            IntPtr hwnd = CreateWindowEx(0, className, mTitle, WsOverlappedWindow, 80, 80,
                OuterWidth(width), OuterHeight(height),
                IntPtr.Zero, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                reason = "CreateWindowEx failed";
                mCreated = false;
                return false;
            }

            mHwnd = hwnd;

            ShowWindow(hwnd, SwShow);
            UpdateWindow(hwnd);
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            SetForegroundWindow(hwnd);

            var window = new ClapNative.ClapWindow
            {
                Api = (byte*)ClapNative.Win32Api,
                Specific = hwnd,
            };

            if (mGui->SetParent == null || mGui->SetParent(mPlugin, &window) == 0)
            {
                reason = "gui->set_parent failed";
                DestroyGui();
                DestroyWindow(hwnd);
                mHwnd = IntPtr.Zero;
                return false;
            }

            mGuiReady = true;

            bool shown = mGui->Show == null || mGui->Show(mPlugin) != 0;
            mShown = shown;
            mChildCount = CountChildren(hwnd);

            if (!shown && mChildCount == 0)
            {
                reason = "gui->show failed and no child window was created";
                DestroyGui();
                DestroyWindow(hwnd);
                mHwnd = IntPtr.Zero;
                return false;
            }

            if (TryGetSize(out int actualWidth, out int actualHeight)
                && (actualWidth != width || actualHeight != height))
            {
                ResizeWindow(actualWidth, actualHeight);
            }

            mOpened = true;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
        finally
        {
            sCurrent = null;
        }
    }

    static void Trace(string message) => Console.Error.WriteLine("clap-editor: " + message);

    // 顶层窗口句柄。交给宿主（跨进程）做置前 / 可见性查询。
    public IntPtr Hwnd => mHwnd;

    // 宿主窗口里挂了多少个子窗口。>0 = 插件真的把界面建出来了。
    public int ChildWindowCount => mChildCount;

    // 用户点了 ✕ 之后窗口没了、消息循环也退了。宿主据此重新开窗而不是往死句柄上置前。
    public bool IsAlive => !mExited;

    bool Start()
    {
        mThread = new Thread(Run)
        {
            IsBackground = true,
            Name = "clap-editor",
        };

        try
        {
            if (OperatingSystem.IsWindows()) mThread.SetApartmentState(ApartmentState.STA);
        }
        catch (Exception)
        {
            // 非 Windows / 平台不支持：照跑。
        }

        mThread.Start();

        // 插件建 UI 通常是 100~400ms；给 5 秒上限。超时就把线程留给它自己收拾
        // （后台线程，插件卡在里面也不挡桥退出），桥的主线程必须尽快回去处理音频。
        if (!mReady.Wait(StartTimeout))
        {
            FailureReason = "the editor did not start within 5s";
            return false;
        }

        if (!mOpened)
        {
            FailureReason ??= "the editor did not create a window";
            return false;
        }

        return true;
    }

    static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);

    // 从桥的主线程调：把窗口带到前面（已经打开时）。
    public void BringToFront()
    {
        IntPtr hwnd = mHwnd;
        if (hwnd == IntPtr.Zero)
            return;

        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
    }

    // 插件通过 clap.host-gui.request_resize 要求改尺寸。窗口还没建好时先记下来，
    // Run 用它当初始尺寸（create 期间插件常常先报一次）。
    public void RequestResize(int width, int height)
    {
        if (width < 1 || height < 1)
            return;

        if (mMainThread)
        {
            ResizeWindow(width, height);
            return;
        }

        IntPtr hwnd = mHwnd;
        if (hwnd == IntPtr.Zero)
        {
            mRequestedWidth = width;
            mRequestedHeight = height;
            return;
        }

        PostMessage(hwnd, WmResize, new IntPtr(width), new IntPtr(height));
    }

    public void RequestShow()
    {
        if (mMainThread)
        {
            if (mHwnd != IntPtr.Zero)
                ShowWindow(mHwnd, SwShow);

            return;
        }

        IntPtr hwnd = mHwnd;
        if (hwnd != IntPtr.Zero)
            PostMessage(hwnd, WmShow, IntPtr.Zero, IntPtr.Zero);
    }

    public void RequestHide()
    {
        if (mMainThread)
        {
            if (mHwnd != IntPtr.Zero)
                ShowWindow(mHwnd, SwHide);

            return;
        }

        IntPtr hwnd = mHwnd;
        if (hwnd != IntPtr.Zero)
            PostMessage(hwnd, WmHide, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (mMainThread)
        {
            lock (sMainThreadWindows)
                sMainThreadWindows.Remove(this);

            if (mClosed)
                return;

            mClosed = true;

            IntPtr mainHwnd = mHwnd;
            if (mainHwnd != IntPtr.Zero)
            {
                PostMessage(mainHwnd, WmClose, IntPtr.Zero, IntPtr.Zero);

                for (int i = 0; i < 20 && !mExited; i++)
                {
                    Pump();
                    Thread.Sleep(10);
                }
            }

            if (!mExited)
            {
                DestroyGui();

                if (mainHwnd != IntPtr.Zero)
                    DestroyWindow(mainHwnd);

                mExited = true;
            }

            mHwnd = IntPtr.Zero;
            return;
        }

        if (mClosed)
            return;

        mClosed = true;

        IntPtr hwnd = mHwnd;
        if (hwnd != IntPtr.Zero)
        {
            PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        }
        else if (mThreadId != 0)
        {
            // Floating GUI has no host window; ask its message loop to quit.
            PostThreadMessage(mThreadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        }

        // 必须等编辑器线程退完再让桥去 destroy 插件：gui->destroy 还在那条线程上跑。
        // 上限 3 秒，卡死的插件不该把卸载流程一起拖住。
        try
        {
            mThread?.Join(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
        }
    }

    // ── 编辑器线程 ──

    void Run()
    {
        sCurrent = this;

        try
        {
            RunCore();
        }
        catch (Exception ex)
        {
            Trace("editor thread exception: " + ex);
        }
        finally
        {
            sCurrent = null;
            mExited = true;
            mReady.Set();
        }
    }

    void RunCore()
    {
        Trace("thread start");
        string className = "TuneLabClapEditor_" + Guid.NewGuid().ToString("N");
        WndProcDelegate wndProc = WindowProc;

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = GetModuleHandle(null),
            hCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512),   // IDC_ARROW
            lpszClassName = className,
        };

        if (RegisterClassEx(ref wndClass) == 0)
        {
            FailureReason = "RegisterClassEx failed";
            Trace(FailureReason);
            return;
        }

        Trace("register ok");

        mThreadId = GetCurrentThreadId();

        if (mPrestarted)
        {
            mFloating = false;
            Trace("gui already created on the host main thread");
        }
        else
        {
            // create must precede set_parent; the plugin may ask host_gui for size.
            // Some plugins only implement a floating GUI; try that first, then
            // fall back to the embedded (set_parent) path.
            Trace("calling gui create (floating)");

            if (mGui->Create == null)
            {
                FailureReason = "gui->create is missing";
                Trace(FailureReason);
                return;
            }

            mFloating = mGui->Create(mPlugin, (byte*)ClapNative.Win32Api, 1) != 0;

            if (!mFloating)
            {
                Trace("floating create failed; trying embedded");

                if (mGui->Create(mPlugin, (byte*)ClapNative.Win32Api, 0) == 0)
                {
                    FailureReason = "gui->create failed";
                    Trace(FailureReason);
                    return;
                }
            }

            Trace(mFloating ? "gui created (floating)" : "gui created (embedded)");
        }

        mCreated = true;

        if (mFloating)
        {
            bool floatingShown = mGui->Show == null || mGui->Show(mPlugin) != 0;
            mShown = floatingShown;
            Trace($"floating show={floatingShown}");

            if (!floatingShown)
            {
                FailureReason = "gui->show failed";
                DestroyGui();
                return;
            }

            mOpened = true;
            mReady.Set();

            while (GetMessage(out MSG floatingMessage, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref floatingMessage);
                DispatchMessage(ref floatingMessage);
            }

            mExited = true;
            mReady.Set();
            return;
        }

        int width;
        int height;

        if (mPrestarted && mPresetWidth > 0)
        {
            width = mPresetWidth;
            height = mPresetHeight;
        }
        else if (!TryGetSize(out width, out height))
        {
            width = 800;
            height = 600;
        }

        if (mRequestedWidth > 0)
        {
            width = mRequestedWidth;
            height = mRequestedHeight;
        }

        IntPtr hwnd = CreateWindowEx(0, className, mTitle, WsOverlappedWindow, 80, 80,
            OuterWidth(width), OuterHeight(height),
            IntPtr.Zero, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);

        Trace("CreateWindowEx returned " + hwnd);

        if (hwnd == IntPtr.Zero)
        {
            FailureReason = "CreateWindowEx failed";
            DestroyGui();
            return;
        }

        mHwnd = hwnd;

        ShowWindow(hwnd, SwShow);
        UpdateWindow(hwnd);

        // 置顶 + 前台：窗口在桥进程里，宿主已经 AllowSetForegroundWindow 授权过。
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        SetForegroundWindow(hwnd);

        var window = new ClapNative.ClapWindow
        {
            Api = (byte*)ClapNative.Win32Api,
            Specific = hwnd,
        };

        Trace("calling set_parent");
        if (mGui->SetParent == null || mGui->SetParent(mPlugin, &window) == 0)
        {
            FailureReason = "gui->set_parent failed";
            Trace(FailureReason);

            if (!mPrestarted)
                DestroyGui();

            DestroyWindow(hwnd);
            return;
        }

        Trace("set_parent ok");

        mGuiReady = true;

        Trace("calling show");
        bool shown = mGui->Show == null || mGui->Show(mPlugin) != 0;
        mShown = shown;

        mChildCount = CountChildren(hwnd);
        Trace($"show={shown} children={mChildCount}");

        // show 返回 0 但确实建出了子窗口的插件不少（JUCE 系就见过）：以子窗口为准。
        if (!shown && mChildCount == 0)
        {
            FailureReason = "gui->show failed and no child window was created";
            Trace(FailureReason);

            if (!mPrestarted)
                DestroyGui();

            DestroyWindow(hwnd);
            return;
        }

        // 有些插件只有 set_parent 之后才报得准尺寸，按它调一次。
        if (!mPrestarted && TryGetSize(out int actualWidth, out int actualHeight)
            && (actualWidth != width || actualHeight != height))
        {
            ResizeWindow(actualWidth, actualHeight);
        }

        mOpened = true;
        mReady.Set();

        // GetMessage 返回 0 = WM_QUIT，-1 = 出错；两者都退出。
        while (GetMessage(out MSG message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmSize:
                // 窗口尺寸变了 → 告诉插件（它据此重排 / 重绘自己的子窗口）。
                // mGuiReady 之前不调：建窗时的 WM_SIZE 早于 set_parent。
                if (mGuiReady && mGui->SetSize != null)
                {
                    long packed = lParam.ToInt64();
                    mGui->SetSize(mPlugin, (uint)(packed & 0xFFFF), (uint)((packed >> 16) & 0xFFFF));
                }

                return IntPtr.Zero;

            case WmBringToFront:
                SetForegroundWindow(hwnd);
                return IntPtr.Zero;

            case WmResize:
                ResizeWindow((int)wParam.ToInt64(), (int)lParam.ToInt64());
                return IntPtr.Zero;

            case WmShow:
                ShowWindow(hwnd, SwShow);
                return IntPtr.Zero;

            case WmHide:
                ShowWindow(hwnd, SwHide);
                return IntPtr.Zero;

            case WmClose:
                DestroyGui();
                DestroyWindow(hwnd);
                return IntPtr.Zero;

            case WmDestroy:
                mExited = true;

                if (!mMainThread)
                    PostQuitMessage(0);

                return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    // 只在编辑器线程上调。
    void DestroyGui()
    {
        if (!mCreated)
            return;

        mCreated = false;
        mGuiReady = false;

        try
        {
            if (mShown && mGui->Hide != null)
                mGui->Hide(mPlugin);
        }
        catch (Exception)
        {
        }

        mShown = false;

        try
        {
            if (mGui->Destroy != null)
                mGui->Destroy(mPlugin);
        }
        catch (Exception)
        {
        }
    }

    bool TryGetSize(out int width, out int height)
    {
        width = 800;
        height = 600;

        if (mGui->GetSize == null)
            return false;

        uint w = 0;
        uint h = 0;

        if (mGui->GetSize(mPlugin, &w, &h) == 0)
            return false;

        if (w < 120 || w > 4096 || h < 80 || h > 4096)
            return false;

        width = (int)w;
        height = (int)h;
        return true;
    }

    // 客户区尺寸 → 整窗尺寸（含标题栏与边框）。
    static int OuterWidth(int clientWidth)
    {
        var rect = new RECT { right = clientWidth };
        AdjustWindowRect(ref rect, WsOverlappedWindow, false);
        return rect.right - rect.left;
    }

    static int OuterHeight(int clientHeight)
    {
        var rect = new RECT { bottom = clientHeight };
        AdjustWindowRect(ref rect, WsOverlappedWindow, false);
        return rect.bottom - rect.top;
    }

    void ResizeWindow(int width, int height)
    {
        IntPtr hwnd = mHwnd;
        if (hwnd == IntPtr.Zero)
            return;

        width = Math.Clamp(width, 120, 8192);
        height = Math.Clamp(height, 80, 8192);

        // 已经就是这个尺寸：不要再 SetWindowPos（否则插件 request_resize → WM_SIZE →
        // set_size → request_resize 会来回打转）。
        if (GetClientRect(hwnd, out RECT current)
            && current.right - current.left == width && current.bottom - current.top == height)
        {
            return;
        }

        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, OuterWidth(width), OuterHeight(height),
            SwpNoMove | SwpNoZOrder | SwpNoActivate);
    }

    static int CountChildren(IntPtr parent)
    {
        int count = 0;

        bool Enum(IntPtr hwnd, IntPtr lParam)
        {
            count++;
            return true;
        }

        WndEnumProc proc = Enum;
        EnumChildWindows(parent, proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        return count;
    }

    // ── Win32 ──

    delegate bool WndEnumProc(IntPtr hwnd, IntPtr lParam);

    const uint WsOverlappedWindow = 0x00CF0000;

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
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int x;
        public int y;
    }

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

    delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    static extern bool AdjustWindowRect(ref RECT lpRect, uint dwStyle, bool bMenu);

    [DllImport("user32.dll")]
    static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr hWndParent, WndEnumProc lpEnumFunc, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? lpModuleName);
}
