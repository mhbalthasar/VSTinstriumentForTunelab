using System;
using System.Threading.Tasks;

namespace TuneLab.VstInstruments.Bridge;

// Upper bound for one request/reply with the helper.
//
// The plugin is third-party native code. Nothing on the managed side can
// interrupt it: it may loop forever in init/process/state/close, show a modal
// dialog, or wait for a resource that never arrives. Without a bound, the
// TuneLab UI thread would wait forever in .GetAwaiter().GetResult().
//
// On timeout the caller kills the helper. The pipe still holds the unanswered
// reply, so reusing it would answer the next request with the wrong message;
// a fresh helper is the only safe state.
//
// Kept separate so the timeout behavior can be unit-tested without a real
// plugin: give it a never-completing task and a fake onTimeout.
internal static class BridgeTimeout
{
    /// <summary>
    /// Run <paramref name="action"/> on the thread pool (so a UI thread never
    /// waits on its own continuation) and wait at most <paramref name="timeout"/>.
    /// On timeout call <paramref name="onTimeout"/> (normally "kill the helper")
    /// and return <paramref name="fallback"/>. Exceptions also return the
    /// fallback; every bridge operation already catches its own failures.
    /// </summary>
    public static T Run<T>(Func<Task<T>> action, TimeSpan timeout, T fallback, Action onTimeout)
    {
        var task = Task.Run(action);

        bool completed;
        try
        {
            completed = task.Wait(timeout);
        }
        catch (Exception)
        {
            return fallback;
        }

        if (!completed)
        {
            onTimeout();
            return fallback;
        }

        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
