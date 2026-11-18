using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// LV2 worker host. Plugins use this for sample loading and other non-realtime
// work: run() schedules a job, this class calls the plugin's work() on a
// background thread, and work_response() is delivered before the next run() on
// the bridge main thread.
internal sealed unsafe class Lv2Worker : IDisposable
{
    public const uint Success = 0;
    public const uint ErrorUnknown = 1;
    public const uint ErrorNoSpace = 2;

    sealed class Job
    {
        public uint Size;
        public IntPtr Data;
    }

    readonly BlockingCollection<Job> mJobs = new();
    readonly ConcurrentQueue<Job> mResponses = new();
    readonly GCHandle mHandle;
    IntPtr mSchedule;
    IntPtr mInstance;
    IntPtr mWork;
    IntPtr mWorkResponse;
    IntPtr mEndRun;
    Thread? mThread;
    bool mDisposed;

    static readonly ScheduleProc sSchedule = Schedule;
    static readonly RespondProc sRespond = Respond;
    static readonly IntPtr sSchedulePointer = Marshal.GetFunctionPointerForDelegate(sSchedule);
    static readonly IntPtr sRespondPointer = Marshal.GetFunctionPointerForDelegate(sRespond);
    static readonly IntPtr sInterfaceUri = Marshal.StringToCoTaskMemUTF8(Lv2Native.WorkerInterfaceUri);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint ScheduleProc(IntPtr handle, uint size, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint RespondProc(IntPtr handle, uint size, IntPtr data);

    public Lv2Worker()
    {
        mHandle = GCHandle.Alloc(this);
        mSchedule = Marshal.AllocHGlobal(sizeof(Lv2Native.WorkerSchedule));
        var schedule = (Lv2Native.WorkerSchedule*)mSchedule;
        schedule->Handle = GCHandle.ToIntPtr(mHandle);
        schedule->ScheduleWork = sSchedulePointer;
    }

    public IntPtr ScheduleData => mSchedule;

    public void Attach(IntPtr instance, IntPtr extensionData)
    {
        mInstance = instance;

        if (extensionData == IntPtr.Zero)
            return;

        try
        {
            var getExtension = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)extensionData;
            IntPtr data = getExtension(sInterfaceUri);

            if (data == IntPtr.Zero)
                return;

            var value = *(Lv2Native.WorkerInterface*)data;
            mWork = value.Work;
            mWorkResponse = value.WorkResponse;
            mEndRun = value.EndRun;

            if (mWork != IntPtr.Zero)
            {
                mThread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "lv2-worker",
                };

                mThread.Start();
            }
        }
        catch (Exception)
        {
        }
    }

    static uint Schedule(IntPtr handle, uint size, IntPtr data)
    {
        if (handle == IntPtr.Zero)
            return ErrorUnknown;

        try
        {
            var worker = (Lv2Worker)GCHandle.FromIntPtr(handle).Target!;
            return worker.Enqueue(size, data);
        }
        catch (Exception)
        {
            return ErrorUnknown;
        }
    }

    uint Enqueue(uint size, IntPtr data)
    {
        if (mDisposed)
            return ErrorUnknown;

        IntPtr copy = size > 0 ? Marshal.AllocHGlobal((int)size) : IntPtr.Zero;

        if (size > 0 && data != IntPtr.Zero)
            Buffer.MemoryCopy((void*)data, (void*)copy, size, size);

        try
        {
            if (!mJobs.TryAdd(new Job { Size = size, Data = copy }))
            {
                if (copy != IntPtr.Zero)
                    Marshal.FreeHGlobal(copy);

                return ErrorNoSpace;
            }

            return Success;
        }
        catch (Exception)
        {
            if (copy != IntPtr.Zero)
                Marshal.FreeHGlobal(copy);

            return ErrorUnknown;
        }
    }

    void Run()
    {
        try
        {
            foreach (var job in mJobs.GetConsumingEnumerable())
            {
                try
                {
                    var work = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, uint, IntPtr, uint>)mWork;
                    work(mInstance, sRespondPointer, GCHandle.ToIntPtr(mHandle), job.Size, job.Data);
                }
                catch (Exception)
                {
                }
                finally
                {
                    if (job.Data != IntPtr.Zero)
                        Marshal.FreeHGlobal(job.Data);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    static uint Respond(IntPtr handle, uint size, IntPtr data)
    {
        if (handle == IntPtr.Zero)
            return ErrorUnknown;

        try
        {
            var worker = (Lv2Worker)GCHandle.FromIntPtr(handle).Target!;
            IntPtr copy = size > 0 ? Marshal.AllocHGlobal((int)size) : IntPtr.Zero;

            if (size > 0 && data != IntPtr.Zero)
                Buffer.MemoryCopy((void*)data, (void*)copy, size, size);

            worker.mResponses.Enqueue(new Job { Size = size, Data = copy });
            return Success;
        }
        catch (Exception)
        {
            return ErrorUnknown;
        }
    }

    public void DrainResponses()
    {
        if (mWorkResponse == IntPtr.Zero)
            return;

        var workResponse = (delegate* unmanaged[Cdecl]<IntPtr, uint, IntPtr, uint>)mWorkResponse;

        while (mResponses.TryDequeue(out var response))
        {
            try
            {
                workResponse(mInstance, response.Size, response.Data);
            }
            catch (Exception)
            {
            }
            finally
            {
                if (response.Data != IntPtr.Zero)
                    Marshal.FreeHGlobal(response.Data);
            }
        }
    }

    public void EndRun()
    {
        if (mEndRun == IntPtr.Zero)
            return;

        try
        {
            var endRun = (delegate* unmanaged[Cdecl]<IntPtr, uint>)mEndRun;
            endRun(mInstance);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        if (mDisposed)
            return;

        mDisposed = true;
        mJobs.CompleteAdding();

        try
        {
            mThread?.Join(1000);
        }
        catch (Exception)
        {
        }

        while (mJobs.TryTake(out var job))
        {
            if (job.Data != IntPtr.Zero)
                Marshal.FreeHGlobal(job.Data);
        }

        while (mResponses.TryDequeue(out var response))
        {
            if (response.Data != IntPtr.Zero)
                Marshal.FreeHGlobal(response.Data);
        }

        if (mSchedule != IntPtr.Zero)
            Marshal.FreeHGlobal(mSchedule);

        mSchedule = IntPtr.Zero;

        if (mHandle.IsAllocated)
            mHandle.Free();
    }
}