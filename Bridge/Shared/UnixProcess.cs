using System;
using System.Runtime.InteropServices;

namespace TuneLab.VstInstruments.Bridge;

// The few libc calls used for Unix process lifetime management. Windows uses a
// Job object instead and never reaches this file.
//
// Both sides use the same definitions: the bridge calls setpgid on startup and
// runs a liveness watchdog; the host kills the whole process group on timeout.
public static class UnixProcess
{
    const int SIGKILL = 9;

    [DllImport("libc", SetLastError = true)]
    static extern int setpgid(int pid, int pgid);

    [DllImport("libc", SetLastError = true)]
    static extern int kill(int pid, int sig);

    [DllImport("libc", SetLastError = true)]
    static extern int getpid();

    [DllImport("libc", SetLastError = true)]
    static extern int getppid();

    [DllImport("libc", SetLastError = true)]
    static extern int getpgrp();

    /// <summary>
    /// Called by the bridge on startup: become the leader of a new process
    /// group, so the host can kill the bridge and any grandchildren in one go.
    /// </summary>
    public static bool PutInOwnProcessGroup() => setpgid(0, 0) == 0;

    /// <summary>Host: SIGKILL the group whose leader is the bridge PID.</summary>
    public static void KillProcessGroup(int leaderPid)
    {
        if (leaderPid > 0)
            kill(-leaderPid, SIGKILL);
    }

    /// <summary>
    /// Called by the liveness watchdog when the host is gone. Prefer the whole
    /// group; if setpgid did not succeed we are not a leader, so only kill
    /// ourselves rather than risk another group.
    /// </summary>
    public static void KillOwnProcessGroupOrSelf()
    {
        int pid = getpid();

        if (getpgrp() == pid)
            kill(-pid, SIGKILL);
        else
            kill(pid, SIGKILL);
    }

    public static int GetPid() => getpid();

    public static int GetParentPid() => getppid();

    const int EPERM = 1;

    /// <summary>
    /// Host: is this PID alive? kill(pid, 0) sends no signal, only checks
    /// existence/permission. 0 = alive; ESRCH = gone; EPERM = alive but not ours.
    /// </summary>
    public static bool IsAlive(int pid)
    {
        if (pid <= 0)
            return false;

        if (kill(pid, 0) == 0)
            return true;

        return Marshal.GetLastPInvokeError() == EPERM;
    }
}
