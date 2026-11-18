using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using TuneLab.VstInstruments.Bridge;

// Bridge process entry point.
//
// What this process guarantees:
//   1. It only connects when the host says so; after that it only listens to
//      the host.
//   2. When the host disappears, this process disappears too.
//      Windows: the host puts us in its JobObject with KILL_ON_JOB_CLOSE. When
//      the host dies the job handle is closed and the kernel kills us.
//      Unix: the host owns a liveness pipe; a watchdog thread blocks on it. A
//      dead host closes the write end, read returns 0, and we kill our own
//      process group.
//   3. It keeps quiet: the host may capture stderr; every diagnostic goes
//      there and nowhere else.
internal static class BridgeProgram
{
    static int Main(string[] args)
    {
        // Argument contract: --pipe <name>. Without it the process was started
        // by hand; exit immediately. A standalone bridge is meaningless because
        // its shared sections and lifetime belong to the host.
        string? pipeName = null;
        string? livenessHandle = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--pipe")
                pipeName = args[i + 1];
            else if (args[i] == "--liveness")
                livenessHandle = args[i + 1];
        }

        if (pipeName == null)
        {
            Console.Error.WriteLine("vsti-bridge: missing --pipe; this process only runs under TuneLab.");
            return 2;
        }

        // Unix: become our own process group leader and start the watchdog
        // BEFORE waiting for the host to connect. Otherwise a host crash in
        // that window would leave an orphan.
        if (!OperatingSystem.IsWindows() && livenessHandle is { Length: > 0 })
        {
            UnixProcess.PutInOwnProcessGroup();
            StartLivenessWatchdog(livenessHandle);
        }

        Console.CancelKeyPress += (_, e) =>
        {
            // Let Ctrl+C terminate; the handler only stops the console from
            // printing a scary stack trace.
            e.Cancel = false;
        };

        try
        {
            // 64 KiB buffers in each direction. Without them Windows gives a
            // synchronous pipe; while the helper is stuck inside a plugin the
            // writer blocks too.
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024);

            // A second, one-way pipe carries unsolicited events (state changes).
            // Keeping them off the command pipe means a notification can never
            // be consumed as the reply to GetState/ProcessRequest.
            using var eventServer = new NamedPipeServerStream(pipeName + ".events", PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024);

            // The host should connect within seconds. If it does not, exit
            // rather than wait as a zombie.
            if (!server.IsConnected)
            {
                var connect = server.WaitForConnectionAsync(CancellationToken.None);

                if (!connect.Wait(TimeSpan.FromSeconds(30)))
                {
                    Console.Error.WriteLine("vsti-bridge: host never connected; exiting.");
                    return 4;
                }
            }

            // ── Shared audio rings. The host creates them before starting us;
            //    names derive from the pipe name, so the two ends always agree.
            //    The bridge only opens. A mismatch here is fatal, never silent.
            string inName = pipeName + ".in";
            string outName = pipeName + ".out";

            SharedAudioRing input;
            SharedAudioRing output;

            try
            {
                input = SharedAudioRing.Open(inName, null);
                output = SharedAudioRing.Open(outName, pipeName + ".ready");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"vsti-bridge: cannot open the shared audio sections: {ex.Message}");
                return 5;
            }

            using var session = new BridgeSession(server, input, output, eventServer);
            session.Run();

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("vsti-bridge: " + ex);
            return 5;
        }
    }

    // Unix liveness watchdog: block on the read end of the host's pipe. When
    // the host dies its write end is reclaimed by the kernel, read returns 0,
    // and we take down our whole process group.
    //
    // A separate thread is required: while the main loop is stuck inside a
    // plugin, this thread is still blocked on its own pipe and will wake.
    static void StartLivenessWatchdog(string handle)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var pipe = new AnonymousPipeClientStream(PipeDirection.In, handle);
                var buffer = new byte[1];

                // Blocks while the host is alive; 0 = EOF = host gone.
                while (pipe.Read(buffer, 0, 1) > 0)
                {
                }
            }
            catch (Exception)
            {
                // A broken pipe also means the host is gone.
            }

            UnixProcess.KillOwnProcessGroupOrSelf();
        })
        {
            IsBackground = true,
            Name = "bridge-liveness",
        };

        thread.Start();
    }
}
