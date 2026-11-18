using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TuneLab.VstInstruments.Bridge;

// One connected pipe session: read a frame, dispatch it, write a reply.
//
// This layer deliberately knows nothing about CLAP or VST2. It parses the
// protocol, finds the host that owns a slot, and forwards the call. A protocol
// error stays here; a plugin crash kills the helper, which is the intended
// isolation boundary.
internal sealed class BridgeSession : IDisposable
{
    readonly NamedPipeServerStream mPipe;
    readonly NamedPipeServerStream? mEventPipe;
    readonly Hosts.ClapInstrumentHost mClap = new();
    readonly Hosts.Vst2InstrumentHost mVst2 = new();
    readonly Hosts.Vst3InstrumentHost mVst3 = new();
    readonly Hosts.Lv2InstrumentHost mLv2 = new();

    // Slot -> the host that loaded it. Only touched on the bridge main thread.
    readonly Dictionary<int, Hosts.IBridgeInstrumentHost> mOwner = [];

    readonly byte[] mLength = new byte[4];
    readonly SharedAudioRing? mInput;
    readonly SharedAudioRing? mOutput;
    readonly BlockingCollection<string> mFrames = new();
    readonly BlockingCollection<string> mEvents = new(256);
    Thread? mReaderThread;
    Thread? mEventThread;
    bool mRunning = true;

    // State change detection. The bridge owns this so the host does not have
    // to poll: a 500 ms scan compares the plugin's state hash while the host is
    // idle and sends StateChanged on the event pipe when it differs.
    const long StatePollMs = 500;
    const long StateQuietMs = 750;
    readonly Dictionary<int, ulong> mStateHashes = [];
    readonly Dictionary<int, long> mStateQuietUntil = [];
    long mNextStatePoll;

    public BridgeSession(NamedPipeServerStream pipe, SharedAudioRing? input = null, SharedAudioRing? output = null,
        NamedPipeServerStream? eventPipe = null)
    {
        mPipe = pipe;
        mInput = input;
        mOutput = output;
        mEventPipe = eventPipe;

        if (output != null)
        {
            mClap.Attach(output);
            mVst2.Attach(output);
            mVst3.Attach(output);
            mLv2.Attach(output);
        }
    }

    public void Run()
    {
        // Read frames on a dedicated thread so the main thread can keep the
        // CLAP editor window responsive (PeekMessage/DispatchMessage) between
        // protocol messages. All handlers still run on this thread.
        StartReader();
        StartEventLoop();

        try
        {
            while (mRunning)
            {
                if (mFrames.TryTake(out string? json, 10))
                {
                    ProcessFrame(json);
                }
                else if (mFrames.IsCompleted)
                {
                    break;
                }

                mClap.PumpEditor();
                PollStateChanges();
            }
        }
        finally
        {
            StopEventLoop();
        }

        try
        {
            mReaderThread?.Join(500);
        }
        catch (Exception)
        {
        }
    }

    // ── Unsolicited event pipe ──

    void StartEventLoop()
    {
        if (mEventPipe == null)
            return;

        mEventThread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = "bridge-events",
        };

        mEventThread.Start();
    }

    void EventLoop()
    {
        try
        {
            mEventPipe!.WaitForConnection();

            foreach (string json in mEvents.GetConsumingEnumerable())
            {
                byte[] head = Encoding.UTF8.GetBytes(json);

                if (head.Length <= 0 || head.Length > BridgeProtocol.MaxFrameBytes)
                    continue;

                mEventPipe.Write(BitConverter.GetBytes(head.Length));
                mEventPipe.Write(head);
                mEventPipe.Flush();
            }
        }
        catch (Exception)
        {
            // The host closed the event pipe; the command pipe/job owns the
            // helper lifetime, so there is nothing else to clean up here.
        }
    }

    void StopEventLoop()
    {
        mEvents.CompleteAdding();

        try
        {
            mEventPipe?.Dispose();
        }
        catch (Exception)
        {
        }

        try
        {
            mEventThread?.Join(500);
        }
        catch (Exception)
        {
        }
    }

    void NotifyStateChanged(int slot)
    {
        if (mEventPipe == null || mEvents.IsAddingCompleted)
            return;

        mEvents.TryAdd(JsonSerializer.Serialize(new StateChangedMessage { Slot = slot }, BridgeProtocol.Json));
    }

    // ── State change detection ──

    void PollStateChanges()
    {
        if (mEventPipe == null || mOwner.Count == 0)
            return;

        long now = Environment.TickCount64;

        if (now < mNextStatePoll)
            return;

        mNextStatePoll = now + StatePollMs;

        foreach (var pair in mOwner)
        {
            int slot = pair.Key;

            if (mStateQuietUntil.TryGetValue(slot, out long quietUntil) && now < quietUntil)
                continue;

            byte[]? state;

            try
            {
                state = pair.Value.GetState(slot);
            }
            catch (Exception)
            {
                continue;
            }

            if (state == null)
                continue;

            ulong hash = HashState(state);

            if (mStateHashes.TryGetValue(slot, out ulong previous))
            {
                if (previous == hash)
                    continue;

                mStateHashes[slot] = hash;
                NotifyStateChanged(slot);
            }
            else
            {
                mStateHashes[slot] = hash;
            }
        }
    }

    // Called after an operation where the host itself changed the plugin state
    // (process parameters/notes, SetParameter, SetState, reset). The next idle
    // scan establishes a new baseline without emitting an event, so host-driven
    // automation never looks like a user edit.
    void MarkHostStateChange(int slot)
    {
        mStateHashes.Remove(slot);
        mStateQuietUntil[slot] = Environment.TickCount64 + StateQuietMs;
    }

    void RememberCurrentState(int slot)
    {
        mStateQuietUntil.Remove(slot);

        try
        {
            byte[]? state = OwnerOf(slot)?.GetState(slot);

            if (state == null)
                mStateHashes.Remove(slot);
            else
                mStateHashes[slot] = HashState(state);
        }
        catch (Exception)
        {
            mStateHashes.Remove(slot);
        }
    }

    static ulong HashState(byte[] state)
        => BitConverter.ToUInt64(SHA256.HashData(state), 0);

    void StartReader()
    {
        mReaderThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "bridge-reader",
        };

        mReaderThread.Start();
    }

    void ReadLoop()
    {
        try
        {
            while (mRunning)
            {
                if (!TryReadFrame(out string? json) || json == null)
                    break;

                mFrames.Add(json);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            mFrames.CompleteAdding();
        }
    }

    void ProcessFrame(string? json)
    {
        BridgeMessageKind kind = PeekKind(json);

        switch (kind)
        {
            case BridgeMessageKind.Hello:
                HandleHello(json);
                break;

            case BridgeMessageKind.Ping:
                Write(new PingAck());
                break;

            case BridgeMessageKind.LoadPlugin:
                HandleLoad(json);
                break;

            case BridgeMessageKind.UnloadPlugin:
                HandleUnload(json);
                break;

            case BridgeMessageKind.ProcessRequest:
                HandleProcess(json);
                break;

            case BridgeMessageKind.GetState:
                HandleGetState(json);
                break;

            case BridgeMessageKind.SetState:
                HandleSetState(json);
                break;

            case BridgeMessageKind.GetParameters:
                HandleGetParameters(json);
                break;

            case BridgeMessageKind.SetParameter:
                HandleSetParameter(json);
                break;

            case BridgeMessageKind.Panic:
                HandlePanic(json);
                break;

            case BridgeMessageKind.Reset:
                HandleReset(json);
                break;

            case BridgeMessageKind.OpenEditor:
                HandleOpenEditor(json);
                break;

            case BridgeMessageKind.CloseEditor:
                HandleCloseEditor(json);
                break;

            case BridgeMessageKind.ScanPlugins:
                HandleScan(json);
                break;

            case BridgeMessageKind.Shutdown:
                Write(new ShutdownAck());
                mRunning = false;
                break;

            default:
                Write(new ErrorMessage { Message = "unsupported message " + kind });
                break;
        }
    }

    // ── Handlers ──

    void HandleHello(string? json)
    {
        var message = Deserialize<HelloMessage>(json);

        if (message == null || message.Version != BridgeProtocol.ProtocolVersion)
        {
            string reason = message == null
                ? "bad Hello message"
                : $"protocol version mismatch (host {message.Version}, bridge {BridgeProtocol.ProtocolVersion})";

            Write(new HelloAckMessage { Ok = false, Reason = reason });
            mRunning = false;
            return;
        }

        Write(new HelloAckMessage
        {
            Ok = true,
            Rid = BridgeIdentity.Rid,
            Arch = BridgeIdentity.Arch,
            Formats = BridgeFormats(),
            Native = new NativeAvailability
            {
                Vst3 = Hosts.Vst3InstrumentHost.IsSupported ? "ownvst3.tlvsti" : null,
                Lv2 = "managed",
                Au = OperatingSystem.IsMacOS() && Hosts.Vst3InstrumentHost.IsSupported ? "ownvst3.tlvsti" : null,
            },
        });
    }

    // What this helper can actually host. VST3/LV2/AU are not advertised until
    // their native shims exist; a client must not send a format the helper did
    // not declare.
    static string[] BridgeFormats()
    {
        var formats = new List<string> { PluginFormats.Vst2 };

        if (Environment.Is64BitProcess)
            formats.Add(PluginFormats.Clap);

        // LV2 is a fully managed host, so the win-x86 helper can host 32-bit
        // LV2 bundles. VST3/AU stay 64-bit only (the native host has no x86).
        formats.Add(PluginFormats.Lv2);

        if (Hosts.Vst3InstrumentHost.IsSupported)
            formats.Add(PluginFormats.Vst3);

        // AU is hosted by the same OwnVST3 native library on macOS; elsewhere
        // the AudioToolbox path does not exist.
        if (OperatingSystem.IsMacOS() && Hosts.Vst3InstrumentHost.IsSupported)
            formats.Add(PluginFormats.Au);

        return [.. formats];
    }

    void HandleLoad(string? json)
    {
        var message = Deserialize<LoadPluginMessage>(json);

        if (message == null)
        {
            Write(new ErrorMessage { Message = "bad LoadPlugin message" });
            return;
        }

        if (mOwner.Remove(message.Slot, out var previous))
            previous.Unload(message.Slot);

        var host = HostFor(message.Format);

        if (host == null)
        {
            Write(new LoadPluginResultMessage
            {
                Slot = message.Slot,
                Ok = false,
                Reason = $"this bridge cannot host '{message.Format}'",
            });
            return;
        }

        var result = host.Load(message);

        if (result.Ok)
        {
            mOwner[message.Slot] = host;
            RememberCurrentState(message.Slot);
        }

        Write(new LoadPluginResultMessage
        {
            Slot = message.Slot,
            Ok = result.Ok,
            Reason = result.Reason,
            Name = result.Name,
            Vendor = result.Vendor,
            IsInstrument = result.IsInstrument,
            InputChannels = result.InputChannels,
            OutputChannels = result.OutputChannels,
            LatencySamples = result.LatencySamples,
            TailSamples = result.TailSamples,
            HasEditor = result.HasEditor,
            NeedsOpenGl = false,
            ParameterCount = result.ParameterCount,
        });
    }

    Hosts.IBridgeInstrumentHost? HostFor(string format)
    {
        if (string.Equals(format, PluginFormats.Clap, StringComparison.OrdinalIgnoreCase))
            return mClap;

        if (string.Equals(format, PluginFormats.Vst2, StringComparison.OrdinalIgnoreCase))
            return mVst2;

        if (string.Equals(format, PluginFormats.Vst3, StringComparison.OrdinalIgnoreCase))
            return mVst3;

        if (string.Equals(format, PluginFormats.Lv2, StringComparison.OrdinalIgnoreCase))
            return mLv2;

        if (string.Equals(format, PluginFormats.Au, StringComparison.OrdinalIgnoreCase))
            return OperatingSystem.IsMacOS() ? mVst3 : null;

        return null;
    }

    Hosts.IBridgeInstrumentHost? OwnerOf(int slot)
        => mOwner.TryGetValue(slot, out var host) ? host : null;

    void HandleUnload(string? json)
    {
        var message = Deserialize<UnloadPluginMessage>(json);

        if (message == null)
        {
            Write(new ErrorMessage { Message = "bad UnloadPlugin message" });
            return;
        }

        bool ok = OwnerOf(message.Slot)?.Unload(message.Slot) == true;
        mOwner.Remove(message.Slot);
        mStateHashes.Remove(message.Slot);
        mStateQuietUntil.Remove(message.Slot);

        Write(new UnloadPluginResultMessage { Slot = message.Slot, Ok = ok });
    }

    void HandleProcess(string? json)
    {
        var message = Deserialize<ProcessRequest>(json);

        if (message == null)
        {
            Write(new ErrorMessage { Message = "bad ProcessRequest message" });
            return;
        }

        var host = OwnerOf(message.Slot);

        if (host == null)
        {
            Write(new ProcessResult
            {
                Slot = message.Slot,
                Tag = message.Tag,
                Ok = false,
                Reason = "the slot is not loaded",
            });
            return;
        }

        bool ok = host.Process(message.Slot, message, out string? reason);
        MarkHostStateChange(message.Slot);

        Write(new ProcessResult
        {
            Slot = message.Slot,
            Tag = message.Tag,
            Ok = ok,
            Frames = message.Frames,
            Channels = 2,
            Reason = reason,
        });
    }

    void HandleGetState(string? json)
    {
        var message = Deserialize<GetStateMessage>(json);

        if (message == null)
        {
            Write(new ErrorMessage { Message = "bad GetState message" });
            return;
        }

        byte[]? state = OwnerOf(message.Slot)?.GetState(message.Slot);

        Write(new StateChunkMessage
        {
            Slot = message.Slot,
            Base64 = state == null ? string.Empty : Convert.ToBase64String(state),
        });
    }

    void HandleSetState(string? json)
    {
        var message = Deserialize<SetStateMessage>(json);

        if (message == null)
        {
            Write(new ErrorMessage { Message = "bad SetState message" });
            return;
        }

        bool ok = OwnerOf(message.Slot)?.SetState(message.Slot, message.Base64) == true;

        if (ok)
            MarkHostStateChange(message.Slot);

        Write(new SetStateResultMessage { Slot = message.Slot, Ok = ok });
    }

    void HandleGetParameters(string? json)
    {
        var message = Deserialize<GetParametersMessage>(json);

        if (message == null)
        {
            Write(new ErrorMessage { Message = "bad GetParameters message" });
            return;
        }

        var parameters = OwnerOf(message.Slot)?.GetParameters(message.Slot) ?? [];

        Write(new ParameterListMessage
        {
            Slot = message.Slot,
            Parameters = parameters,
        });
    }

    void HandleSetParameter(string? json)
    {
        var message = Deserialize<SetParameterMessage>(json);

        if (message == null)
        {
            Write(new ErrorMessage { Message = "bad SetParameter message" });
            return;
        }

        bool ok = OwnerOf(message.Slot)?.SetParameter(message.Slot, message.Id, message.Value) == true;

        if (ok)
            MarkHostStateChange(message.Slot);

        Write(new SetParameterResultMessage { Slot = message.Slot, Ok = ok });
    }

    void HandlePanic(string? json)
    {
        var message = Deserialize<PanicMessage>(json);

        if (message == null)
        {
            Write(new ErrorMessage { Message = "bad Panic message" });
            return;
        }

        bool ok = OwnerOf(message.Slot)?.Panic(message.Slot) == true;

        if (ok)
            MarkHostStateChange(message.Slot);

        Write(new PanicResultMessage { Slot = message.Slot, Ok = ok });
    }

    void HandleReset(string? json)
    {
        var message = Deserialize<ResetMessage>(json);

        if (message == null)
        {
            Write(new ErrorMessage { Message = "bad Reset message" });
            return;
        }

        bool ok = OwnerOf(message.Slot)?.Reset(message.Slot) == true;

        if (ok)
            MarkHostStateChange(message.Slot);

        Write(new ResetResultMessage { Slot = message.Slot, Ok = ok });
    }

    void HandleOpenEditor(string? json)
    {
        var message = Deserialize<OpenEditorMessage>(json);

        if (message == null)
        {
            Write(new ErrorMessage { Message = "bad OpenEditor message" });
            return;
        }

        var host = OwnerOf(message.Slot);

        if (host == null)
        {
            Write(new OpenEditorResultMessage
            {
                Slot = message.Slot,
                Ok = false,
                Reason = "the slot is not loaded",
            });
            return;
        }

        var result = host.OpenEditor(message.Slot, message.Title);

        Write(new OpenEditorResultMessage
        {
            Slot = message.Slot,
            Ok = result.Ok,
            Reason = result.Reason,
            ChildWindows = 0,
            WindowHandle = 0,
        });
    }

    void HandleCloseEditor(string? json)
    {
        var message = Deserialize<CloseEditorMessage>(json);

        if (message == null)
        {
            Write(new ErrorMessage { Message = "bad CloseEditor message" });
            return;
        }

        bool ok = OwnerOf(message.Slot)?.CloseEditor(message.Slot) == true;
        Write(new CloseEditorResultMessage { Slot = message.Slot, Ok = ok });
    }

    void HandleScan(string? json)
    {
        var message = Deserialize<ScanPluginsMessage>(json);

        if (message == null)
        {
            Write(new ErrorMessage { Message = "bad ScanPlugins message" });
            return;
        }

        var host = HostFor(message.Format);

        if (host == null)
        {
            Write(new PluginListMessage
            {
                Format = message.Format,
                Reason = $"this bridge cannot scan '{message.Format}'",
            });
            return;
        }

        var entries = host.Scan(message);

        Write(new PluginListMessage
        {
            Format = message.Format,
            Entries = entries,
        });
    }

    // ── Frames ──

    static T? Deserialize<T>(string? json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json ?? "null", BridgeProtocol.Json);
        }
        catch (JsonException)
        {
            return null;
        }
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

    void Write<T>(T message)
    {
        byte[] head = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, BridgeProtocol.Json));

        // An oversized frame must not be written: the peer would reject the
        // length, leave the rest of the frame in the pipe, and every following
        // frame would be misaligned. Replace it with a valid Error frame.
        if (head.Length > BridgeProtocol.MaxFrameBytes)
        {
            WriteCore(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ErrorMessage
            {
                Message = $"a {head.Length}-byte reply exceeds the bridge frame limit of {BridgeProtocol.MaxFrameBytes} bytes",
            }, BridgeProtocol.Json)));

            return;
        }

        WriteCore(head);
    }

    void WriteCore(byte[] head)
    {
        mPipe.Write(BitConverter.GetBytes(head.Length));
        mPipe.Write(head);
        mPipe.Flush();
    }

    bool TryReadFrame(out string? json)
    {
        json = null;

        try
        {
            if (!ReadExact(mLength, 4))
                return false;

            int total = BitConverter.ToInt32(mLength);

            if (total <= 0 || total > BridgeProtocol.MaxFrameBytes)
                return false;

            var frame = new byte[total];

            if (!ReadExact(frame, total))
                return false;

            json = Encoding.UTF8.GetString(frame);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    bool ReadExact(byte[] buffer, int count)
    {
        int read = 0;

        while (read < count)
        {
            int n = mPipe.Read(buffer, read, count - read);

            if (n <= 0)
                return false;

            read += n;
        }

        return true;
    }

    public void Dispose()
    {
        // Unload every slot before the pipe disappears so plugins get their
        // destroy/close callbacks while their library is still loaded.
        foreach (var (slot, host) in mOwner)
        {
            try
            {
                host.Unload(slot);
            }
            catch (Exception)
            {
            }
        }

        mOwner.Clear();

        mRunning = false;

        try
        {
            mFrames.CompleteAdding();
        }
        catch (Exception)
        {
        }

        try
        {
            mPipe.Dispose();
        }
        catch (Exception)
        {
        }

        try
        {
            mReaderThread?.Join(500);
        }
        catch (Exception)
        {
        }

        mInput?.Dispose();
        mOutput?.Dispose();
    }
}
