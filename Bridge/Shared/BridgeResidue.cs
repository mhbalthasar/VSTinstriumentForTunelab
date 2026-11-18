using System;
using System.IO;
using System.Threading;

namespace TuneLab.VstInstruments.Bridge;

// Cleanup for files left behind by a hard kill (Windows does not need this:
// named sections and named events are reclaimed by the kernel with the process).
//
// On Unix two things land on disk and are named after the host PID:
//   - section files: <temp>/tunelab-vstinstrument-bridge/<pipe>.in / .out
//   - the pipe socket: <temp>/CoreFxPipe_<pipe>
//
// Ownership:
//   - sections: the host creates them and deletes them on KillBridge/Dispose;
//     a hard kill leaves them behind.
//   - socket: the bridge creates it and unlinks it on Dispose; SIGKILL leaves
//     it behind.
//
// Rule: if the host PID embedded in the name is dead, the file is residue.
// A reused PID can only make us keep a stale file (conservative), never delete
// a live process's file.
public static class BridgeResidue
{
    static int sSwept;

    /// <summary>Sweep once per process, before the first helper starts.</summary>
    public static void SweepOnce()
    {
        if (OperatingSystem.IsWindows())
            return;

        if (Interlocked.Exchange(ref sSwept, 1) != 0)
            return;

        Sweep();
    }

    /// <summary>Sweep sections and pipe sockets belonging to dead hosts.</summary>
    public static void Sweep()
    {
        if (OperatingSystem.IsWindows())
            return;

        string prefix = BridgeProtocol.PipeName + "-";

        // 1) Section files.
        try
        {
            string dir = SharedAudioRing.UnixSectionDirectory;

            if (Directory.Exists(dir))
            {
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    // Strip .in / .out; what remains is <prefix><rid>-<hostPid>.
                    string bare = Path.GetFileNameWithoutExtension(file);

                    if (!bare.StartsWith(prefix, StringComparison.Ordinal))
                        continue;

                    if (IsDeadOwner(bare))
                        TryDelete(file);
                }
            }
        }
        catch (Exception)
        {
        }

        // 2) Pipe sockets, under the temp root with the CoreFxPipe_ prefix.
        try
        {
            string temp = Path.GetTempPath();

            foreach (string file in Directory.EnumerateFiles(temp, "CoreFxPipe_" + BridgeProtocol.PipeName + "*"))
            {
                string bare = Path.GetFileName(file)["CoreFxPipe_".Length..];

                if (!bare.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                if (IsDeadOwner(bare))
                    TryDelete(file);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Path of a pipe socket on Unix (same formula .NET uses).</summary>
    public static string PipeSocketPath(string pipeName) =>
        Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName);

    /// <summary>
    /// Delete the socket for a pipe name. Called before starting a helper
    /// (a stale socket would make bind fail with EADDRINUSE) and after killing
    /// one (SIGKILL does not run its unlink).
    /// </summary>
    public static void DeletePipeSocket(string? pipeName)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(pipeName))
            return;

        TryDelete(PipeSocketPath(pipeName));
    }

    // The last dash-separated segment is the host PID: <prefix><rid>-<hostPid>.
    static bool IsDeadOwner(string bareName)
    {
        int dash = bareName.LastIndexOf('-');

        if (dash < 0)
            return false;

        if (!int.TryParse(bareName[(dash + 1)..], out int hostPid))
            return false;

        return hostPid > 0 && !UnixProcess.IsAlive(hostPid);
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}
