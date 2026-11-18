using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// VST3/AU editor window hosted by the helper.
//
// The vendored OwnVST3 native host creates the plugin view itself; the host
// cannot reparent it. The view belongs to the thread that created it, so this
// class runs an STA thread: CreateEditor -> message pump -> CloseEditor on that
// same thread. The bridge main thread remains free for rendering and protocol.
internal sealed class Vst3EditorWindow : IDisposable
{
    readonly Vst3InstrumentHost.Slot mSlot;

    Thread? mThread;
    IntPtr mHwnd;
    volatile bool mClosed;
    volatile bool mExited;
    readonly ManualResetEventSlim mReady = new(false);
    bool mOpened;

    public IntPtr Handle => mHwnd;

    public bool IsAlive => !mExited;

    Vst3EditorWindow(Vst3InstrumentHost.Slot slot)
    {
        mSlot = slot;
    }

    public static bool TryOpen(Vst3InstrumentHost.Slot slot, out Vst3EditorWindow? window)
    {
        var candidate = new Vst3EditorWindow(slot);

        if (!candidate.Start())
        {
            candidate.Dispose();
            window = null;
            return false;
        }

        window = candidate;
        return true;
    }

    bool Start()
    {
        mThread = new Thread(Run)
        {
            IsBackground = true,
            Name = "vst3-editor",
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

    public void BringToFront()
    {
        IntPtr hwnd = mHwnd;

        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
            return;

        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
    }

    public void Dispose()
    {
        if (mClosed)
            return;

        mClosed = true;

        try
        {
            mThread?.Join(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
        }
    }

    void Run()
    {
        try
        {
            HashSet<IntPtr> before = SnapshotTopLevelWindows();

            bool opened;

            try
            {
                opened = mSlot.Wrapper.CreateEditor(IntPtr.Zero);
            }
            catch (Exception)
            {
                opened = false;
            }

            if (opened && SafeIsEditorOpen())
            {
                for (int attempt = 0; attempt < 40 && mHwnd == IntPtr.Zero && !mClosed; attempt++)
                {
                    mHwnd = FindEditorWindowSince(before);

                    if (mHwnd == IntPtr.Zero)
                        Thread.Sleep(25);
                }

                mOpened = SafeIsEditorOpen();
            }
        }
        catch (Exception)
        {
            mOpened = false;
        }

        mReady.Set();

        if (!mOpened)
        {
            mExited = true;
            return;
        }

        if (mHwnd != IntPtr.Zero && OperatingSystem.IsWindows())
            SetForegroundWindow(mHwnd);

        while (!mClosed)
        {
            if (!SafeIsEditorOpen())
                break;

            try
            {
                mSlot.Wrapper.SafeDispatchMessage();
                DrainMessages();
            }
            catch (Exception)
            {
                break;
            }

            Thread.Sleep(15);
        }

        try
        {
            mSlot.Wrapper.CloseEditor();
        }
        catch (Exception)
        {
        }

        mExited = true;
    }

    bool SafeIsEditorOpen()
    {
        try
        {
            return mSlot.Wrapper.IsEditorOpen;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static HashSet<IntPtr> SnapshotTopLevelWindows()
    {
        var set = new HashSet<IntPtr>();

        if (!OperatingSystem.IsWindows())
            return set;

        IntPtr hwnd = GetTopWindow(IntPtr.Zero);
        int guard = 0;

        while (hwnd != IntPtr.Zero && guard++ < 10000)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);

            if (pid == (uint)Environment.ProcessId)
                set.Add(hwnd);

            hwnd = GetWindow(hwnd, GwHwndNext);
        }

        return set;
    }

    static IntPtr FindEditorWindowSince(HashSet<IntPtr> before)
    {
        if (!OperatingSystem.IsWindows())
            return IntPtr.Zero;

        IntPtr fallback = IntPtr.Zero;
        IntPtr hwnd = GetTopWindow(IntPtr.Zero);
        int guard = 0;

        while (hwnd != IntPtr.Zero && guard++ < 10000)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);

            if (pid == (uint)Environment.ProcessId
                && !before.Contains(hwnd)
                && IsWindowVisible(hwnd))
            {
                var title = new StringBuilder(256);

                if (GetWindowText(hwnd, title, title.Capacity) > 0)
                    return hwnd;

                if (fallback == IntPtr.Zero)
                    fallback = hwnd;
            }

            hwnd = GetWindow(hwnd, GwHwndNext);
        }

        return fallback;
    }

    void DrainMessages()
    {
        if (!OperatingSystem.IsWindows())
            return;

        while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    const uint GwHwndNext = 2;
    const uint PmRemove = 0x0001;
    const int SwRestore = 9;

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

    [DllImport("user32.dll")]
    static extern IntPtr GetTopWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool PeekMessage(out MSG message, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr DispatchMessage(ref MSG message);
}