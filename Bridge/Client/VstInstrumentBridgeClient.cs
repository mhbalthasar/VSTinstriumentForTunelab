using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TuneLab.VstInstruments.Bridge;

// Result of one rendered block. The helper publishes the audio to the shared
// ring; this object is the host's copy of it.
public sealed class ProcessOutcome
{
    public bool Ok { get; init; }
    public string? Reason { get; init; }
    public float[][] Planes { get; init; } = [];
    public int Channels { get; init; }
    public int Frames { get; init; }

    public static ProcessOutcome Fail(string reason) => new() { Ok = false, Reason = reason };
}

// Host-side bridge client.
//
// Two hard rules, inherited from the reference insert host:
//   1. Start on demand. Nothing runs until a plugin must be loaded.
//   2. The host owns the helper's life. Windows: a Job object with
//      KILL_ON_JOB_CLOSE, assigned by us after Process.Start. Unix: a liveness
//      pipe whose write end only the host holds; the bridge's watchdog kills
//      its own process group when it reaches EOF.
//
// Offline rendering has no real-time deadline, so the block wait is generous;
// a hung plugin still gets the helper killed and the piece reported as failed.
public sealed class VstInstrumentBridgeClient : IDisposable
{
    const string BridgeAssemblyName = "TuneLab.VstInstruments.Bridge";
    const int KillExitWaitMs = 2000;

    static readonly ConcurrentDictionary<string, VstInstrumentBridgeClient> sClients =
        new(StringComparer.OrdinalIgnoreCase);

    // One client (and therefore one helper process) per RID. An x86 plugin can
    // run while the x64 helper is also alive; they must not share a pipe.
    public static VstInstrumentBridgeClient ForRid(string rid)
        => sClients.GetOrAdd(rid, static value => new VstInstrumentBridgeClient(value));

    public static VstInstrumentBridgeClient ForPlugin(int bits, string? rid)
    {
        string target = !string.IsNullOrEmpty(rid) ? rid : BridgeIdentity.RidFor(bits);

        if (string.IsNullOrEmpty(target))
            throw new PlatformNotSupportedException($"no .NET 8 helper runtime exists for {bits}-bit plugins on this platform");

        return ForRid(target);
    }

    public static IReadOnlyCollection<VstInstrumentBridgeClient> All => (IReadOnlyCollection<VstInstrumentBridgeClient>)sClients.Values;

    public static void ShutdownAll()
    {
        foreach (var client in sClients.Values)
            client.Dispose();

        sClients.Clear();
    }

    readonly string mRid;
    readonly int mBitness;

    Process? mProcess;
    JobHandle? mJob;
    NamedPipeClientStream? mPipe;
    NamedPipeClientStream? mEventPipe;
    CancellationTokenSource? mEventCts;
    Task? mEventTask;
    int mEventGeneration;
    AnonymousPipeServerStream? mLiveness;

    string? mPipeName;

    // Raised on the event-reader thread when the helper reports that a plugin's
    // state changed while the host was not driving it. The slot is the bridge
    // slot, not the TuneLab part id.
    public event Action<int>? StateChanged;

    SharedAudioRing? mInput;   // host -> helper; reserved for future audio inputs
    SharedAudioRing? mOutput;  // helper -> host

    readonly SemaphoreSlim mWriteLock = new(1, 1);
    readonly SemaphoreSlim mRoundTrip = new(1, 1);
    readonly object mKillLock = new();

    readonly List<string> mErrorLines = [];

    int mNextSlot;
    int mProcessTag;
    bool mDisposed;

    VstInstrumentBridgeClient(string rid)
    {
        mRid = rid;
        mBitness = rid.EndsWith("x86", StringComparison.OrdinalIgnoreCase) ? 32 : 64;
    }

    public string Rid => mRid;

    public int Bitness => mBitness;

    public bool IsRunning => mPipe?.IsConnected == true;

    // What the helper declared in HelloAck. The extension must not send a
    // format the helper did not list.
    public string[]? BridgeFormats { get; private set; }

    public bool EventChannelConnected => mEventPipe?.IsConnected == true;

    // Test seam: lets the harness exercise the session's auto-capture path
    // without a GUI editor. Production code only raises this from EventLoopAsync.
    internal void RaiseStateChangedForTest(int slot) => RaiseStateChanged(slot);

    public string? LastEventError { get; private set; }

    public string? LastStartError { get; private set; }

    public string? LastTimeoutError { get; private set; }

    public string? LastFrameError { get; private set; }

    public string? LastProtocolError { get; private set; }

    public string? LastBridgeError
    {
        get
        {
            lock (mErrorLines)
            {
                var parts = new List<string>();

                if (LastStartError is { Length: > 0 })
                    parts.Add(LastStartError);

                if (LastTimeoutError is { Length: > 0 })
                    parts.Add(LastTimeoutError);

                if (LastFrameError is { Length: > 0 })
                    parts.Add(LastFrameError);

                if (LastProtocolError is { Length: > 0 })
                    parts.Add(LastProtocolError);

                parts.AddRange(mErrorLines);
                return parts.Count == 0 ? null : string.Join(" | ", parts);
            }
        }
    }

    public void ClearErrors()
    {
        lock (mErrorLines)
            mErrorLines.Clear();

        LastStartError = null;
        LastTimeoutError = null;
        LastFrameError = null;
        LastProtocolError = null;
    }

    public int AllocateSlot() => Interlocked.Increment(ref mNextSlot);

    // ── Lifecycle ──

    public bool EnsureStartedBlocking()
    {
        if (mDisposed)
            return false;

        if (IsRunning)
            return true;

        return RunTimed("the bridge handshake", () => EnsureStartedCoreAsync(CancellationToken.None),
            StartTimeout, false);
    }

    async Task<bool> EnsureStartedCoreAsync(CancellationToken cancellation)
    {
        if (mDisposed)
            return false;

        // A crashed helper can leave IsConnected true; HasExited is the truth.
        if (mProcess is { HasExited: true })
            KillBridge();

        if (IsRunning)
            return true;

        KillBridge();
        BridgeResidue.SweepOnce();
        ClearErrors();

        if (OperatingSystem.IsWindows())
        {
            mJob ??= JobHandle.Create();

            if (mJob == null)
            {
                LastStartError = "creating the job object failed";
                return false;
            }
        }

        string? bridge = FindBridgeExecutable(mRid);

        if (bridge == null)
        {
            LastStartError = $"no bridge executable for {mRid} beside {AppContext.BaseDirectory}";
            return false;
        }

        string pipeName = BridgeProtocol.PipeName + "-" + mRid + "-" + Environment.ProcessId;
        string inName = pipeName + ".in";
        string outName = pipeName + ".out";

        BridgeResidue.DeletePipeSocket(pipeName);
        mPipeName = pipeName;

        mInput?.Dispose();
        mOutput?.Dispose();

        try
        {
            mInput = SharedAudioRing.Create(inName, null);
            mOutput = SharedAudioRing.Create(outName, pipeName + ".ready");
        }
        catch (Exception ex)
        {
            mInput = null;
            mOutput = null;
            LastStartError = "creating the shared audio rings failed: " + ex.Message;
            return false;
        }

        string livenessArg = string.Empty;

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                mLiveness?.Dispose();
                mLiveness = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
                livenessArg = " --liveness " + mLiveness.GetClientHandleAsString();
            }
            catch (Exception ex)
            {
                mLiveness?.Dispose();
                mLiveness = null;
                LastStartError = "creating the liveness pipe failed: " + ex.Message;
                return false;
            }
        }

        var start = new ProcessStartInfo
        {
            FileName = bridge,
            Arguments = $"--pipe {pipeName}{livenessArg}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
        };

        mProcess = Process.Start(start);

        if (mProcess == null)
        {
            LastStartError = "Process.Start returned no process for the bridge";
            return false;
        }

        // Windows: put the child in our job before it can become an orphan.
        // Unix: the liveness pipe is the orphan defense, no job needed.
        if (OperatingSystem.IsWindows() && (mJob == null || !mJob.Assign(mProcess)))
        {
            KillBridge();
            LastStartError = "assigning the bridge to the job object failed";
            return false;
        }

        mProcess.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data))
                return;

            lock (mErrorLines)
            {
                mErrorLines.Add(e.Data);

                if (mErrorLines.Count > 64)
                    mErrorLines.RemoveAt(0);
            }
        };

        mProcess.BeginErrorReadLine();

        mPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await mPipe.ConnectAsync(3000, cancellation).ConfigureAwait(false);

            string? ack = await ExchangeAsync(JsonSerializer.Serialize(new HelloMessage
            {
                Version = BridgeProtocol.ProtocolVersion,
                Rid = BridgeIdentity.Rid,
                Arch = BridgeIdentity.Arch,
                Bits = BridgeIdentity.Bits,
            }, BridgeProtocol.Json), cancellation).ConfigureAwait(false);

            var hello = Deserialize<HelloAckMessage>(ack);

            if (hello == null || !hello.Ok)
            {
                LastStartError = hello?.Reason ?? "the bridge did not accept the handshake";
                KillBridge();
                return false;
            }

            if (!string.Equals(hello.Rid, mRid, StringComparison.OrdinalIgnoreCase))
            {
                LastStartError = $"the helper for {mRid} reported itself as {hello.Rid}";
                KillBridge();
                return false;
            }

            BridgeFormats = hello.Formats;
            StartEventChannel(pipeName);
            return true;
        }
        catch (Exception ex)
        {
            LastStartError = "connecting to the bridge failed: " + ex.Message;
            KillBridge();
            return false;
        }
    }

    // ── Unsolicited event channel ──

    void StartEventChannel(string pipeName)
    {
        StopEventChannel();
        int generation = Interlocked.Increment(ref mEventGeneration);
        _ = StartEventChannelAsync(pipeName, generation);
    }

    async Task StartEventChannelAsync(string pipeName, int generation)
    {
        LastEventError = null;

        NamedPipeClientStream? pipe = null;

        try
        {
            pipe = new NamedPipeClientStream(".", pipeName + ".events", PipeDirection.In, PipeOptions.Asynchronous);

            using (var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);

            if (generation != Volatile.Read(ref mEventGeneration) || mDisposed || !IsRunning)
            {
                pipe.Dispose();
                return;
            }

            mEventPipe = pipe;
            mEventCts = new CancellationTokenSource();
            var token = mEventCts.Token;
            mEventTask = Task.Run(() => EventLoopAsync(pipe, token));
        }
        catch (Exception ex)
        {
            LastEventError = ex.Message;

            try
            {
                pipe?.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }

    void StopEventChannel()
    {
        Interlocked.Increment(ref mEventGeneration);

        try
        {
            mEventCts?.Cancel();
        }
        catch (Exception)
        {
        }

        try
        {
            mEventPipe?.Dispose();
        }
        catch (Exception)
        {
        }

        mEventCts?.Dispose();
        mEventCts = null;
        mEventPipe = null;
        mEventTask = null;
    }

    async Task EventLoopAsync(PipeStream pipe, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                string? frame = await ReadControlAsync(pipe, token).ConfigureAwait(false);

                if (frame == null)
                    break;

                if (PeekKind(frame) != BridgeMessageKind.StateChanged)
                    continue;

                var message = Deserialize<StateChangedMessage>(frame);

                if (message != null)
                    RaiseStateChanged(message.Slot);
            }
        }
        catch (Exception)
        {
            // The bridge died or the host is shutting down.
        }
    }

    void RaiseStateChanged(int slot)
    {
        try
        {
            StateChanged?.Invoke(slot);
        }
        catch (Exception)
        {
        }
    }

    public void KillBridge()
    {
        lock (mKillLock)
            KillBridgeCore();
    }

    void KillBridgeCore()
    {
        StopEventChannel();

        try
        {
            mPipe?.Dispose();
        }
        catch (Exception)
        {
        }

        mPipe = null;
        BridgeFormats = null;

        try
        {
            mLiveness?.Dispose();
        }
        catch (Exception)
        {
        }

        mLiveness = null;

        if (!OperatingSystem.IsWindows() && mProcess is { HasExited: false } unixBridge)
        {
            try
            {
                UnixProcess.KillProcessGroup(unixBridge.Id);
            }
            catch (Exception)
            {
            }
        }

        if (mProcess is { HasExited: false })
        {
            try
            {
                mProcess.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
        }

        var dying = mProcess;
        mProcess = null;

        if (dying != null)
        {
            try
            {
                dying.WaitForExit(KillExitWaitMs);
            }
            catch (Exception)
            {
            }

            dying.Dispose();
        }

        mInput?.Dispose();
        mOutput?.Dispose();
        mInput = null;
        mOutput = null;

        BridgeResidue.DeletePipeSocket(mPipeName);
        mPipeName = null;
    }

    public void Dispose()
    {
        if (mDisposed)
            return;

        mDisposed = true;
        KillBridge();
        mJob?.Dispose();
        mJob = null;
    }

    // ── Plugin operations ──

    public LoadPluginResultMessage? LoadBlocking(int slot, string format, string pluginId, string locator,
        double sampleRate, int maxBlock, string? state = null, bool instrumentOnly = true)
    {
        if (!IsRunning)
            return null;

        string json = JsonSerializer.Serialize(new LoadPluginMessage
        {
            Slot = slot,
            Format = format,
            PluginId = pluginId,
            Locator = locator,
            SampleRate = sampleRate,
            MaxBlock = maxBlock,
            State = state,
            InstrumentOnly = instrumentOnly,
        }, BridgeProtocol.Json);

        string? reply = RunTimed("loading the plugin", () => ExchangeAsync(json, CancellationToken.None),
            LoadTimeout, (string?)null);

        return reply == null ? null : Deserialize<LoadPluginResultMessage>(reply);
    }

    public bool UnloadBlocking(int slot)
    {
        if (!IsRunning)
            return false;

        string json = JsonSerializer.Serialize(new UnloadPluginMessage { Slot = slot }, BridgeProtocol.Json);
        string? reply = RunTimed("unloading the plugin", () => ExchangeAsync(json, CancellationToken.None),
            UnloadTimeout, (string?)null);

        return Deserialize<UnloadPluginResultMessage>(reply)?.Ok == true;
    }

    // One block. The request/reply travels on the pipe; the audio comes back on
    // the shared ring. The round-trip lock is held across both so a concurrent
    // control call cannot consume the reply that belongs to this block.
    public ProcessOutcome ProcessBlocking(int slot, int frames, MidiEvent[] events, TransportInfo? transport,
        bool bypass, int timeoutMs = 30000, ParameterEvent[]? parameters = null)
    {
        if (!IsRunning || mOutput == null)
            return ProcessOutcome.Fail("the bridge is not running");

        int waitMs = Math.Max(1000, timeoutMs);

        if (!mRoundTrip.Wait(waitMs))
            return ProcessOutcome.Fail("the bridge is busy");

        try
        {
            int tag = Interlocked.Increment(ref mProcessTag);

            string json = JsonSerializer.Serialize(new ProcessRequest
            {
                Slot = slot,
                Frames = frames,
                Bypass = bypass,
                Tag = tag,
                Transport = transport,
                Events = events,
                Parameters = parameters ?? [],
            }, BridgeProtocol.Json);

            string? reply = RunTimed("rendering a block", () => ExchangeCoreAsync(json, CancellationToken.None),
                TimeSpan.FromMilliseconds(waitMs), (string?)null);

            if (reply == null)
                return ProcessOutcome.Fail(LastTimeoutError ?? "the bridge did not answer");

            var result = Deserialize<ProcessResult>(reply);

            if (result == null)
                return ProcessOutcome.Fail("the bridge sent a bad process result");

            if (!result.Ok)
                return ProcessOutcome.Fail(result.Reason ?? "the plugin failed to render the block");

            if (!mOutput.TryReadMatching(tag, waitMs, out _, out var planes, out int channels, out int outFrames))
            {
                KillBridge();
                return ProcessOutcome.Fail("the helper did not publish the block");
            }

            try
            {
                return new ProcessOutcome
                {
                    Ok = true,
                    Planes = planes,
                    Channels = channels,
                    Frames = outFrames,
                };
            }
            finally
            {
                mOutput.Release();
            }
        }
        finally
        {
            mRoundTrip.Release();
        }
    }

    public byte[]? GetStateBlocking(int slot)
    {
        if (!IsRunning)
            return null;

        string json = JsonSerializer.Serialize(new GetStateMessage { Slot = slot }, BridgeProtocol.Json);
        string? reply = RunTimed("reading the plugin state", () => ExchangeAsync(json, CancellationToken.None),
            StateTimeout, (string?)null);

        var chunk = Deserialize<StateChunkMessage>(reply);

        if (chunk == null || string.IsNullOrEmpty(chunk.Base64))
            return null;

        try
        {
            return Convert.FromBase64String(chunk.Base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public bool SetStateBlocking(int slot, byte[] data)
    {
        if (!IsRunning || data.Length == 0)
            return false;

        string json = JsonSerializer.Serialize(new SetStateMessage
        {
            Slot = slot,
            Base64 = Convert.ToBase64String(data),
        }, BridgeProtocol.Json);

        string? reply = RunTimed("writing the plugin state", () => ExchangeAsync(json, CancellationToken.None),
            StateTimeout, (string?)null);

        return Deserialize<SetStateResultMessage>(reply)?.Ok == true;
    }

    public IReadOnlyList<ParameterInfo>? GetParametersBlocking(int slot)
    {
        if (!IsRunning)
            return null;

        string json = JsonSerializer.Serialize(new GetParametersMessage { Slot = slot }, BridgeProtocol.Json);
        string? reply = RunTimed("reading the plugin parameters", () => ExchangeAsync(json, CancellationToken.None),
            ParameterTimeout, (string?)null);

        return Deserialize<ParameterListMessage>(reply)?.Parameters;
    }

    public bool SetParameterBlocking(int slot, long id, double value)
    {
        if (!IsRunning)
            return false;

        string json = JsonSerializer.Serialize(new SetParameterMessage
        {
            Slot = slot,
            Id = id,
            Value = value,
        }, BridgeProtocol.Json);

        string? reply = RunTimed("writing a plugin parameter", () => ExchangeAsync(json, CancellationToken.None),
            ParameterTimeout, (string?)null);

        return Deserialize<SetParameterResultMessage>(reply)?.Ok == true;
    }

    public (bool Ok, string? Reason) OpenEditorBlocking(int slot, string title)
    {
        if (!IsRunning)
            return (false, "the bridge is not running");

        // The helper creates the window in its own process. Windows only lets
        // the foreground process grant that permission; the user just clicked
        // the button, so TuneLab is foreground now.
        if (mProcess is { HasExited: false })
        {
            try
            {
                AllowSetForegroundWindow(mProcess.Id);
            }
            catch (Exception)
            {
            }
        }

        string json = JsonSerializer.Serialize(new OpenEditorMessage
        {
            Slot = slot,
            Title = title,
        }, BridgeProtocol.Json);

        string? reply = RunTimed("opening the plugin editor", () => ExchangeAsync(json, CancellationToken.None),
            EditorTimeout, (string?)null);

        var result = Deserialize<OpenEditorResultMessage>(reply);

        if (result == null)
            return (false, "the bridge did not answer");

        return (result.Ok, result.Reason);
    }

    public bool CloseEditorBlocking(int slot)
    {
        if (!IsRunning)
            return false;

        string json = JsonSerializer.Serialize(new CloseEditorMessage { Slot = slot }, BridgeProtocol.Json);
        string? reply = RunTimed("closing the plugin editor", () => ExchangeAsync(json, CancellationToken.None),
            EditorTimeout, (string?)null);

        return Deserialize<CloseEditorResultMessage>(reply)?.Ok == true;
    }

    public bool PanicBlocking(int slot)
    {
        if (!IsRunning)
            return false;

        string json = JsonSerializer.Serialize(new PanicMessage { Slot = slot }, BridgeProtocol.Json);
        string? reply = RunTimed("stopping the plugin voices", () => ExchangeAsync(json, CancellationToken.None),
            ParameterTimeout, (string?)null);

        return Deserialize<PanicResultMessage>(reply)?.Ok == true;
    }

    public bool ResetBlocking(int slot)
    {
        if (!IsRunning)
            return false;

        string json = JsonSerializer.Serialize(new ResetMessage { Slot = slot }, BridgeProtocol.Json);
        string? reply = RunTimed("resetting the plugin", () => ExchangeAsync(json, CancellationToken.None),
            ParameterTimeout, (string?)null);

        return Deserialize<ResetResultMessage>(reply)?.Ok == true;
    }

    public IReadOnlyList<PluginInfo>? ScanBlocking(string format, IReadOnlyList<string> files,
        bool instrumentOnly = true, TimeSpan? timeout = null)
    {
        if (!IsRunning)
            return null;

        string json = JsonSerializer.Serialize(new ScanPluginsMessage
        {
            Format = format,
            Deep = true,
            InstrumentOnly = instrumentOnly,
            Paths = [.. files],
        }, BridgeProtocol.Json);

        string? reply = RunTimed("the plugin scan", () => ExchangeAsync(json, CancellationToken.None),
            timeout ?? ScanTimeout, (string?)null);

        var message = Deserialize<PluginListMessage>(reply);

        if (message == null)
            return null;

        if (!string.Equals(message.Format, format, StringComparison.OrdinalIgnoreCase))
        {
            LastProtocolError = $"the bridge answered a {format} scan with {message.Format} entries";
            return null;
        }

        if (!string.IsNullOrEmpty(message.Reason))
        {
            LastProtocolError = message.Reason;
            return null;
        }

        return message.Entries;
    }

    // Try a graceful exit; kill either way.
    public void Shutdown()
    {
        if (IsRunning)
        {
            try
            {
                string json = JsonSerializer.Serialize(new ShutdownMessage(), BridgeProtocol.Json);
                RunTimed("shutting the bridge down", () => ExchangeAsync(json, CancellationToken.None),
                    TimeSpan.FromSeconds(3), (string?)null);
            }
            catch (Exception)
            {
            }
        }

        KillBridge();
    }

    // ── Timeouts ──

    static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan EditorTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(60);
    static readonly TimeSpan UnloadTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan ParameterTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(120);

    T RunTimed<T>(string operation, Func<Task<T>> action, TimeSpan timeout, T fallback)
        => BridgeTimeout.Run(action, timeout, fallback, () =>
        {
            LastTimeoutError = $"{operation} did not answer within {timeout.TotalSeconds:0.#}s; the bridge was killed";
            KillBridge();
        });

    // ── Pipe ──

    async Task<string?> ExchangeAsync(string json, CancellationToken cancellation)
    {
        if (!FitsFrame(json, out int bytes))
        {
            LastFrameError =
                $"a {bytes}-byte message exceeds the bridge frame limit of {BridgeProtocol.MaxFrameBytes} bytes";
            return null;
        }

        await mRoundTrip.WaitAsync(cancellation).ConfigureAwait(false);

        try
        {
            string? reply = await ExchangeCoreAsync(json, cancellation).ConfigureAwait(false);

            if (reply == null)
                KillBridge();

            return reply;
        }
        finally
        {
            mRoundTrip.Release();
        }
    }

    async Task<string?> ExchangeCoreAsync(string json, CancellationToken cancellation)
    {
        try
        {
            await SendControlAsync(json, cancellation).ConfigureAwait(false);
            return await ReadControlAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A pipe exception is "the bridge died" on both platforms.
            KillBridge();
            return null;
        }
    }

    async Task SendControlAsync(string json, CancellationToken cancellation)
    {
        var pipe = mPipe ?? throw new IOException("the bridge pipe is closed");
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        await mWriteLock.WaitAsync(cancellation).ConfigureAwait(false);

        try
        {
            await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), cancellation).ConfigureAwait(false);
            await pipe.WriteAsync(bytes, cancellation).ConfigureAwait(false);
            await pipe.FlushAsync(cancellation).ConfigureAwait(false);
        }
        finally
        {
            mWriteLock.Release();
        }
    }

    Task<string?> ReadControlAsync(CancellationToken cancellation)
        => ReadControlAsync(mPipe ?? throw new IOException("the bridge pipe is closed"), cancellation);

    static async Task<string?> ReadControlAsync(PipeStream pipe, CancellationToken cancellation)
    {
        var length = new byte[4];

        if (!await ReadExactAsync(pipe, length, cancellation).ConfigureAwait(false))
            return null;

        int total = BitConverter.ToInt32(length);

        if (total <= 0 || total > BridgeProtocol.MaxFrameBytes)
            return null;

        var frame = new byte[total];

        if (!await ReadExactAsync(pipe, frame, cancellation).ConfigureAwait(false))
            return null;

        return Encoding.UTF8.GetString(frame);
    }

    static async Task<bool> ReadExactAsync(PipeStream pipe, byte[] buffer, CancellationToken cancellation)
    {
        int read = 0;

        while (read < buffer.Length)
        {
            int n = await pipe.ReadAsync(buffer.AsMemory(read), cancellation).ConfigureAwait(false);

            if (n <= 0)
                return false;

            read += n;
        }

        return true;
    }

    static bool FitsFrame(string json, out int bytes)
    {
        bytes = Encoding.UTF8.GetByteCount(json);
        return bytes <= BridgeProtocol.MaxFrameBytes;
    }

    static BridgeMessageKind PeekKind(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? "null");

            if (document.RootElement.TryGetProperty("kind", out var kind)
                && Enum.TryParse<BridgeMessageKind>(kind.ToString(), ignoreCase: true, out var parsed))
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
        }

        return BridgeMessageKind.Error;
    }

    static T? Deserialize<T>(string? json) where T : class
    {
        if (json == null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, BridgeProtocol.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Executable discovery ──

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    public static bool BridgeExecutableExists(string rid) => FindBridgeExecutable(rid) != null;

    static string? FindBridgeExecutable(string rid)
    {
        if (string.IsNullOrEmpty(rid))
            return null;

        string name = OperatingSystem.IsWindows()
            ? BridgeAssemblyName + ".exe"
            : BridgeAssemblyName;

        string baseDirectory = AppContext.BaseDirectory;
        string processDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? baseDirectory;

        // Per-RID subdirectories are the shipping layout: x86 and x64 self-
        // contained runtimes have same-named but different-architecture files
        // and must never share a folder.
        string[] candidates =
        [
            Path.Combine(baseDirectory, "bridge", rid, name),
            Path.Combine(baseDirectory, name),
            Path.Combine(processDirectory, "bridge", rid, name),
            Path.Combine(processDirectory, name),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable("TUNELAB_VSTI_BRIDGE_DIR");

        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            string[] envCandidates =
            [
                Path.Combine(fromEnvironment, rid, name),
                Path.Combine(fromEnvironment, name),
            ];

            foreach (string candidate in envCandidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}

// Windows Job object: the kernel-level guarantee that the helper cannot
// outlive the host. A job handle is process-local, so the child cannot receive
// it on the command line; the host assigns the child after Process.Start.
internal sealed class JobHandle : IDisposable
{
    IntPtr mHandle;

    JobHandle(IntPtr handle) => mHandle = handle;

    public IntPtr Handle => mHandle;

    public bool Assign(Process process)
        => mHandle != IntPtr.Zero && Native.AssignProcessToJobObject(mHandle, process.Handle);

    public static JobHandle? Create()
    {
        IntPtr job = Native.CreateJobObject(IntPtr.Zero, null);

        if (job == IntPtr.Zero)
            return null;

        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new Native.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        if (!Native.SetInformationJobObject(job, Native.JOBOBJECTINFOCLASS.ExtendedLimitInformation,
            ref info, Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            Native.CloseHandle(job);
            return null;
        }

        return new JobHandle(job);
    }

    public void Dispose()
    {
        if (mHandle != IntPtr.Zero)
        {
            // Closing the last handle kills every process still in the job.
            Native.CloseHandle(mHandle);
            mHandle = IntPtr.Zero;
        }
    }

    static class Native
    {
        internal const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        internal enum JOBOBJECTINFOCLASS
        {
            ExtendedLimitInformation = 9,
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public IntPtr MinimumWorkingSetSize;
            public IntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public IntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public IntPtr ProcessMemoryLimit;
            public IntPtr JobMemoryLimit;
            public IntPtr PeakProcessMemoryUsed;
            public IntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(IntPtr hJob, JOBOBJECTINFOCLASS infoClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}