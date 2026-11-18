using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TuneLab.Foundation;
using TuneLab.SDK;
using TuneLab.VstInstruments.Bridge;
using TuneLab.VstInstruments.Catalog;

namespace TuneLab.VstInstruments;

// One part's render session.
//
// Notes are grouped into pieces separated by silence, exactly like the
// WaveTable engine. Each piece loads (or reuses) one plugin slot, resets it,
// renders block by block with sample-accurate note events, then drains the
// tail. The result is a mono TuneLab audio segment.
//
// Reusing a slot across pieces is deliberate: loading a sampler per piece can
// take seconds. Reset + all-notes-off gives deterministic output for the usual
// instruments; the design document keeps "fresh instance per piece" as an
// option for plugins whose reset is weak.
internal sealed class VstInstrumentSession : IInstrumentSynthesisSession
{
    public VstInstrumentSession(IInstrumentSynthesisContext context, CatalogEntry entry,
        VstInstrumentSettings settings, IReadOnlyList<ParameterInfo> automatedParameters,
        Action? onDisposed = null)
    {
        mContext = context;
        mEntry = entry;
        mSettings = settings;
        mAutomatedParameters = automatedParameters;
        mOnDisposed = onDisposed;

        mClient = VstInstrumentBridgeClient.ForPlugin(entry.Bits, entry.Rid);
        mEventClient = mClient;
        mEventClient.StateChanged += OnBridgeStateChanged;

        context.Notes.WhenAnyItem(
                note => note.StartTime.Modified,
                note => note.EndTime.Modified,
                note => note.Pitch.Modified,
                note => note.Properties.Modified)
            .Subscribe(MarkNoteDirty, mSubscriptions);

        context.Notes.ItemAdded.Subscribe(OnNotesChanged, mSubscriptions);
        context.Notes.ItemRemoved.Subscribe(OnNotesChanged, mSubscriptions);
        context.PartProperties.Modified.Subscribe(OnPartPropertiesChanged, mSubscriptions);
        context.Committed.Subscribe(OnCommitted, mSubscriptions);

        mNeedResegment = true;
    }

    public SynthesisRange? GetNextPendingSynthesisRange(double startTime, double endTime)
        => FindNextDirtyPiece(startTime, endTime) is { } piece
            ? new SynthesisRange(piece.StartTime, piece.EndTime)
            : null;

    public async Task SynthesizeNext(double startTime, double endTime, CancellationToken cancellation = default)
    {
        if (mDisposed || FindNextDirtyPiece(startTime, endTime) is not { } piece)
            return;

        // The snapshot is captured in the synchronous prefix, on the data
        // thread. The worker only sees immutable values.
        var snapshot = mContext.GetSnapshot(piece.Notes);
        var notes = BuildNoteSpecs(snapshot);
        var renderSettings = ReadRenderSettings();
        var timedEvents = BuildEvents(notes, renderSettings);
        var automation = BuildAutomationCurves(snapshot);
        var parameterAutomations = BuildParameterAutomations(snapshot);
        var state = ReadState();

        piece.Dirty = false;
        piece.Failed = false;
        piece.Error = null;
        piece.Synthesizing = true;
        Volatile.Write(ref piece.Progress, 0);
        mStatusChanged.Invoke();

        var progress = new Progress<double>(value =>
        {
            if (mDisposed)
                return;

            Volatile.Write(ref piece.Progress, value);
            mStatusChanged.Invoke();
        });

        await mRenderLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        if (mDisposed)
        {
            piece.Synthesizing = false;
            mRenderLock.Release();
            return;
        }

        try
        {
            var rendered = await Task.Run(
                () => RenderPiece(piece, timedEvents, automation, parameterAutomations, state, renderSettings, progress, cancellation),
                CancellationToken.None).ConfigureAwait(false);

            if (mDisposed || !mPieces.Contains(piece))
                return;

            if (rendered == null)
            {
                // Cancellation is normal scheduling: keep the piece dirty so
                // the host can ask again later.
                piece.Dirty = true;
                return;
            }

            if (rendered.Audio.Length == 0)
            {
                piece.Empty = true;
                piece.Segment?.Dispose();
                piece.Segment = null;
                return;
            }

            piece.Empty = false;
            piece.Segment?.Dispose();
            piece.Segment = mContext.CreateAudioSegment(rendered.SampleOffset, rendered.Audio.Length,
                rendered.SampleRate);
            piece.Segment.Write(0, rendered.Audio);
            piece.Segment.Commit();
        }
        catch (Exception ex)
        {
            piece.Failed = true;
            piece.Error = ex.Message;
        }
        finally
        {
            piece.Synthesizing = false;
            mRenderLock.Release();

            if (mDisposed)
                UnloadSlot();

            if (!mDisposed)
                mStatusChanged.Invoke();
        }
    }

    public IReadOnlyMap<string, SynthesizedParameter> SynthesizedParameters
        => Map<string, SynthesizedParameter>.Empty;

    public IReadOnlyList<SynthesisStatusSegment> Status => BuildStatus();

    public IActionEvent SynthesizedParametersChanged => ActionEvent.Empty;

    public IActionEvent StatusChanged => mStatusChanged;

    public void Dispose()
    {
        if (mDisposed)
            return;

        mDisposed = true;

        if (mEventClient != null)
            mEventClient.StateChanged -= OnBridgeStateChanged;

        mSubscriptions.DisposeAll();

        foreach (var piece in mPieces)
            piece.Segment?.Dispose();

        mPieces.Clear();

        // If a render is in flight, its finally block unloads the slot. If not,
        // do it here; the helper must not keep the plugin alive for a dead part.
        if (mRenderLock.Wait(0))
        {
            try
            {
                UnloadSlot();
            }
            finally
            {
                mRenderLock.Release();
            }
        }

        mOnDisposed?.Invoke();
        mOnDisposed = null;
    }

    // The part panel's Reload button. The next render unloads and reloads the
    // slot; all pieces are marked dirty so the host schedules that render.
    public void RequestReload()
    {
        if (mDisposed)
            return;

        mReloadRequested = true;

        foreach (var piece in mPieces)
        {
            piece.Dirty = true;
            piece.Failed = false;
            piece.Error = null;
            piece.Empty = false;
        }

        mStatusChanged.Invoke();
    }

    // Called after Add/Clear Automation changed the engine's declarations.
    public void NotifyDeclarationsChanged()
    {
        try
        {
            mContext.InvalidatePart();
        }
        catch (Exception)
        {
        }
    }

    public void UpdateAutomatedParameters(IReadOnlyList<ParameterInfo> parameters)
        => mAutomatedParameters = parameters;

    // A part can host more than one instrument extension; a plain "state" key
    // would collide with another extension's field. The instrument id is the
    // stable identity the catalog already uses.
    public static string StatePropertyKey(string instrumentId)
        => "vsti_state_" + instrumentId;

    internal int SlotForTest => mSlot;

    public async Task<bool> WriteStateToPartAsync(string base64)
    {
        if (mDisposed)
            return false;

        await mRenderLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            if (mDisposed)
                return false;

            bool written = PartStateWriter.TryWrite(mContext, StatePropertyKey(mEntry.Id), base64);

            // The captured state is the state the live slot already has; do not
            // force a reload just because the persisted field now matches it.
            if (written)
                mLoadedState = base64;

            return written;
        }
        finally
        {
            mRenderLock.Release();
        }
    }

    public async Task ClearStateInPartAsync()
    {
        if (mDisposed)
            return;

        await mRenderLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            if (mDisposed)
                return;

            PartStateWriter.TryRemove(mContext, StatePropertyKey(mEntry.Id));
            mLoadedState = null;
        }
        finally
        {
            mRenderLock.Release();
        }
    }

    void OnBridgeStateChanged(int slot)
    {
        if (mDisposed || mSlot == 0 || slot != mSlot)
            return;

        KickAutoCapture();
    }

    void KickAutoCapture()
    {
        Interlocked.Exchange(ref mAutoCaptureAgain, 1);

        if (Interlocked.CompareExchange(ref mAutoCaptureRunning, 1, 0) != 0)
            return;

        _ = AutoCaptureLoopAsync();
    }

    async Task AutoCaptureLoopAsync()
    {
        try
        {
            do
            {
                Interlocked.Exchange(ref mAutoCaptureAgain, 0);

                string? base64 = await CaptureStateAsync().ConfigureAwait(false);

                if (string.IsNullOrEmpty(base64) || mDisposed)
                    continue;

                try
                {
                    InstrumentStateStore.Save(mEntry.Id, Convert.FromBase64String(base64));
                }
                catch (FormatException)
                {
                }

                await WriteStateToPartAsync(base64).ConfigureAwait(false);
            }
            while (!mDisposed && Volatile.Read(ref mAutoCaptureAgain) != 0);
        }
        catch (Exception ex)
        {
            TuneLabContext.Global.GetLogger().Error("Automatic VST state capture failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref mAutoCaptureRunning, 0);

            if (!mDisposed && Volatile.Read(ref mAutoCaptureAgain) != 0)
                KickAutoCapture();
        }
    }

    public async Task<(bool Ok, string? Reason)> OpenEditorAsync()
    {
        if (mDisposed)
            return (false, "the part session is closed");

        var client = VstInstrumentBridgeClient.ForPlugin(mEntry.Bits, mEntry.Rid);

        await mRenderLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            if (mDisposed)
                return (false, "the part session is closed");

            if (mSlot == 0)
            {
                var settings = new RenderSettings
                {
                    SampleRate = mSettings.SampleRate,
                    BlockSize = mSettings.BlockSize,
                };

                if (!EnsureSlot(client, settings, mLoadedState))
                    return (false, "the plugin could not be loaded");
            }

            return await Task.Run(() => client.OpenEditorBlocking(mSlot, mEntry.DisplayName))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            mRenderLock.Release();
        }
    }

    // ── Rendering ──

    RenderedPiece? RenderPiece(Piece piece, List<TimedEvent> timedEvents, AutomationCurves automation,
        List<ParameterAutomationRuntime> parameterAutomations, string? state, RenderSettings settings,
        IProgress<double> progress, CancellationToken cancellation)
    {
        var client = VstInstrumentBridgeClient.ForPlugin(mEntry.Bits, mEntry.Rid);

        if (!EnsureSlot(client, settings, state))
            throw new InvalidOperationException("the plugin slot could not be prepared");

        client.ResetBlocking(mSlot);

        double rate = settings.SampleRate;
        double latencySeconds = Math.Clamp(mPluginLatencySamples, 0, (int)(rate * 10)) / rate;

        double preRollSeconds = settings.PreRollMs / 1000.0 + latencySeconds;
        double renderStart = Math.Max(0, piece.StartTime - preRollSeconds);

        double requestedTailSamples = Math.Max(settings.TailMs / 1000.0 * rate, mPluginTailSamples);
        double tailSeconds = Math.Clamp(requestedTailSamples / rate, 0, 30);
        double renderEnd = piece.EndTime + tailSeconds + latencySeconds;

        long startSample = (long)Math.Round(renderStart * rate);
        int totalFrames = (int)Math.Clamp(Math.Round((renderEnd - renderStart) * rate), 0, (double)MaxRenderSamples);

        if (totalFrames == 0)
            return new RenderedPiece { Audio = [], SampleOffset = startSample, SampleRate = (int)Math.Round(rate) };

        var buffer = new float[totalFrames];
        var blockEvents = new List<MidiEvent>(64);
        var automationState = new AutomationState();
        int eventIndex = 0;
        int blockSize = Math.Clamp(settings.BlockSize, 64, 4096);

        for (int offset = 0; offset < totalFrames; offset += blockSize)
        {
            if (mDisposed || cancellation.IsCancellationRequested)
                return null;

            int frames = Math.Min(blockSize, totalFrames - offset);
            double blockTime = renderStart + offset / rate;
            double blockEnd = blockTime + frames / rate;

            blockEvents.Clear();

            // Automation is evaluated at block boundaries. That is precise
            // enough for MIDI CC and avoids per-sample curve work.
            automation.AppendBlockEvents(automationState, blockTime, blockEvents, settings.MidiChannel);

            var parameterEvents = new List<ParameterEvent>(parameterAutomations.Count);

            foreach (var parameter in parameterAutomations)
            {
                double value = Math.Clamp(Evaluate(parameter.Evaluator, blockTime), parameter.Min, parameter.Max);

                if (!parameter.HasValue || Math.Abs(value - parameter.LastValue) > 1e-9)
                {
                    parameterEvents.Add(new ParameterEvent { Id = parameter.Id, Value = value });
                    parameter.LastValue = value;
                    parameter.HasValue = true;
                }
            }

            while (eventIndex < timedEvents.Count && timedEvents[eventIndex].Time < blockEnd)
            {
                var timed = timedEvents[eventIndex];
                eventIndex++;

                int eventOffset = (int)Math.Round((timed.Time - blockTime) * rate);

                if (eventOffset < 0)
                    eventOffset = 0;

                if (eventOffset >= frames)
                    eventOffset = frames - 1;

                blockEvents.Add(WithOffset(timed.Event, eventOffset));
            }

            var transport = new TransportInfo
            {
                Playing = true,
                Tempo = 120,
                TimeSigNum = 4,
                TimeSigDen = 4,
                SamplePosition = startSample + offset,
                PpqPosition = blockTime * 2.0, // 120 BPM = 2 beats/s
            };

            int timeoutMs = Math.Max(30000, (int)(frames / rate * 1000 * 8));
            var outcome = client.ProcessBlocking(mSlot, frames, [.. blockEvents], transport,
                bypass: false, timeoutMs: timeoutMs, parameters: [.. parameterEvents]);

            if (!outcome.Ok)
                throw new InvalidOperationException(outcome.Reason ?? "the plugin failed to render a block");

            Downmix(outcome.Planes, outcome.Channels, frames, settings, buffer, offset);
            progress.Report((offset + frames) / (double)totalFrames);
        }

        client.PanicBlocking(mSlot);

        return new RenderedPiece
        {
            Audio = buffer,
            SampleOffset = startSample,
            SampleRate = (int)Math.Round(rate),
        };
    }

    // The helper always publishes stereo. The TuneLab segment is mono.
    static void Downmix(float[][] planes, int channels, int frames, RenderSettings settings,
        float[] destination, int offset)
    {
        float gain = (float)Math.Pow(10, settings.GainDb / 20.0);
        float[] left = planes.Length > 0 ? planes[0] : [];
        float[] right = channels > 1 && planes.Length > 1 ? planes[1] : left;

        for (int i = 0; i < frames; i++)
        {
            float l = i < left.Length ? left[i] : 0;
            float r = i < right.Length ? right[i] : 0;

            float mono = settings.StereoMode switch
            {
                StereoModes.Left => l,
                StereoModes.Right => r,
                StereoModes.MonoSumMinus3Db => (l + r) * 0.35355339f,
                _ => (l + r) * 0.5f,
            };

            destination[offset + i] = float.IsFinite(mono) ? mono * gain : 0f;
        }
    }

    bool EnsureSlot(VstInstrumentBridgeClient client, RenderSettings settings, string? state = null)
    {
        if (mReloadRequested)
        {
            mReloadRequested = false;
            UnloadSlot();
        }

        // The helper may have died since the slot was loaded. A cached slot
        // number is meaningless after a restart; reload it.
        if (mSlot != 0 && ReferenceEquals(mClient, client) && !client.IsRunning)
            UnloadSlot();

        if (mSlot != 0 && ReferenceEquals(mClient, client)
            && mLoadedSampleRate.Equals(settings.SampleRate)
            && mLoadedBlockSize == settings.BlockSize
            && string.Equals(mLoadedState, state, StringComparison.Ordinal))
        {
            return true;
        }

        UnloadSlot();

        if (!client.EnsureStartedBlocking())
        {
            throw new InvalidOperationException(
                "the VST instrument bridge could not start: " + (client.LastBridgeError ?? "unknown error"));
        }

        if (client.BridgeFormats == null
            || !client.BridgeFormats.Contains(mEntry.Format, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"this bridge cannot host {mEntry.Format}");
        }

        mClient = client;
        int slot = client.AllocateSlot();

        var result = client.LoadBlocking(slot, mEntry.Format, mEntry.PluginId, mEntry.Locator,
            settings.SampleRate, settings.BlockSize, state: state);

        if (result == null)
        {
            mClient = null;
            throw new InvalidOperationException(
                "loading the plugin did not answer: " + (client.LastBridgeError ?? "no reply"));
        }

        if (!result.Ok)
        {
            mClient = null;
            throw new InvalidOperationException(result.Reason ?? "the plugin refused to load");
        }

        mSlot = slot;
        mLoadedSampleRate = settings.SampleRate;
        mLoadedBlockSize = settings.BlockSize;
        mLoadedState = state;
        mPluginLatencySamples = Math.Max(0, result.LatencySamples);
        mPluginTailSamples = Math.Max(0, result.TailSamples);
        return true;
    }

    void UnloadSlot()
    {
        if (mClient != null && mSlot != 0)
        {
            try
            {
                mClient.UnloadBlocking(mSlot);
            }
            catch (Exception)
            {
            }
        }

        mSlot = 0;
        mClient = null;
        mLoadedSampleRate = 0;
        mLoadedBlockSize = 0;
        mLoadedState = null;
        mPluginLatencySamples = 0;
        mPluginTailSamples = 0;
    }

    // ── Snapshot → events ──

    List<NoteSpec> BuildNoteSpecs(InstrumentSynthesisSnapshot snapshot)
    {
        var notes = new List<NoteSpec>(snapshot.Notes.Count);

        foreach (var note in snapshot.Notes)
        {
            double velocity = note.Properties.GetDouble("velocity", 100);
            double detune = note.Properties.GetDouble("detune", 0);

            notes.Add(new NoteSpec
            {
                StartTime = note.StartTime,
                EndTime = note.EndTime,
                Pitch = note.Pitch,
                Velocity = (int)Math.Clamp(Math.Round(velocity), 1, 127),
                DetuneCents = detune,
            });
        }

        return notes;
    }

    static List<TimedEvent> BuildEvents(List<NoteSpec> notes, RenderSettings settings)
    {
        var events = new List<TimedEvent>(notes.Count * 2);
        int noteId = 1;

        foreach (var note in notes)
        {
            int pitch = Math.Clamp(note.Pitch + settings.Transpose, 0, 127);
            int channel = settings.MidiChannel;

            events.Add(new TimedEvent
            {
                Time = note.StartTime,
                Event = new MidiEvent
                {
                    Type = MidiEventKind.NoteOn,
                    Channel = channel,
                    Note = pitch,
                    Velocity = note.Velocity,
                    NoteId = noteId,
                },
            });

            events.Add(new TimedEvent
            {
                Time = note.EndTime,
                Event = new MidiEvent
                {
                    Type = MidiEventKind.NoteOff,
                    Channel = channel,
                    Note = pitch,
                    Velocity = 64,
                    NoteId = noteId,
                },
            });

            noteId++;
        }

        events.Sort(static (a, b) =>
        {
            int byTime = a.Time.CompareTo(b.Time);

            if (byTime != 0)
                return byTime;

            // At the same instant, release before a new attack: repeated notes
            // must not be cut by a late note-off.
            return EventPriority(a.Event.Type).CompareTo(EventPriority(b.Event.Type));
        });

        return events;
    }

    static int EventPriority(MidiEventKind kind) => kind switch
    {
        MidiEventKind.NoteOff => 0,
        MidiEventKind.AllNotesOff => 0,
        MidiEventKind.AllSoundOff => 0,
        MidiEventKind.ResetControllers => 0,
        MidiEventKind.NoteOn => 2,
        _ => 1,
    };

    static MidiEvent WithOffset(MidiEvent source, int offset) => new()
    {
        Type = source.Type,
        Offset = offset,
        Channel = source.Channel,
        Note = source.Note,
        Velocity = source.Velocity,
        Controller = source.Controller,
        Value = source.Value,
        Program = source.Program,
        NoteId = source.NoteId,
    };

    static AutomationCurves BuildAutomationCurves(InstrumentSynthesisSnapshot snapshot)
    {
        static IAutomationEvaluator? Pick(IReadOnlyMap<string, SynthesisAutomationSnapshot> map, string id)
            => map.TryGetValue(id, out var entry) ? entry.Evaluator : null;

        return new AutomationCurves
        {
            PitchBend = Pick(snapshot.Automations, "pitchbend"),
            Modulation = Pick(snapshot.Automations, "mod"),
            Volume = Pick(snapshot.Automations, "vol"),
            Expression = Pick(snapshot.Automations, "expr"),
            Sustain = Pick(snapshot.Automations, "sustain"),
        };
    }

    List<ParameterAutomationRuntime> BuildParameterAutomations(InstrumentSynthesisSnapshot snapshot)
    {
        var result = new List<ParameterAutomationRuntime>(mAutomatedParameters.Count);

        foreach (var parameter in mAutomatedParameters)
        {
            string key = "param:" + parameter.Id.ToString(CultureInfo.InvariantCulture);

            if (!snapshot.Automations.TryGetValue(key, out var entry))
                continue;

            result.Add(new ParameterAutomationRuntime
            {
                Id = parameter.Id,
                Min = parameter.Min,
                Max = parameter.Max,
                Evaluator = entry.Evaluator,
            });
        }

        return result;
    }

    static double Evaluate(IAutomationEvaluator evaluator, double time)
    {
        var positions = new double[1] { time };
        var results = new double[1];
        evaluator.Evaluate(positions, results);
        return results[0];
    }

    public async Task<string?> CaptureStateAsync()
    {
        if (mDisposed)
            return null;

        var client = VstInstrumentBridgeClient.ForPlugin(mEntry.Bits, mEntry.Rid);

        await mRenderLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            if (mDisposed)
                return null;

            if (mSlot == 0)
            {
                var settings = new RenderSettings
                {
                    SampleRate = mSettings.SampleRate,
                    BlockSize = mSettings.BlockSize,
                };

                if (!EnsureSlot(client, settings, mLoadedState))
                    return null;
            }

            var data = await Task.Run(() => client.GetStateBlocking(mSlot)).ConfigureAwait(false);
            return data == null ? null : Convert.ToBase64String(data);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            mRenderLock.Release();
        }
    }

    string? ReadState()
    {
        string value = GetString(mContext.PartProperties, StatePropertyKey(mEntry.Id), string.Empty).Trim();

        if (value.Length > 0)
        {
            try
            {
                Convert.FromBase64String(value);
                return value;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        if (InstrumentStateStore.TryLoad(mEntry.Id, out var cached) && cached.Length > 0)
            return Convert.ToBase64String(cached);

        return null;
    }

    RenderSettings ReadRenderSettings()
    {
        var part = mContext.PartProperties;

        double sampleRate = Math.Clamp(GetStringDouble(part, "sample_rate", mSettings.SampleRate), 8000, 192000);
        int channel = (int)Math.Clamp(GetDouble(part, "midi_channel", 1), 1, 16) - 1;

        return new RenderSettings
        {
            SampleRate = sampleRate,
            BlockSize = Math.Clamp(mSettings.BlockSize, 64, 4096),
            PreRollMs = Math.Clamp(GetDouble(part, "pre_roll_ms", mSettings.PreRollMs), 0, 500),
            TailMs = Math.Clamp(GetDouble(part, "tail_ms", mSettings.TailMs), 0, 30000),
            GainDb = Math.Clamp(GetDouble(part, "gain_db", 0), -24, 12),
            Transpose = (int)Math.Clamp(GetDouble(part, "transpose", 0), -24, 24),
            MidiChannel = channel,
            StereoMode = StereoModes.Normalize(GetString(part, "stereo_mode", mSettings.StereoMode)),
        };
    }

    static string GetString(IReadOnlyNotifiablePropertyObject properties, string key, string defaultValue)
    {
        var value = properties.GetValue(key, PropertyValue.Create(defaultValue));
        return value.ToString(out var text) ? text : defaultValue;
    }

    static double GetStringDouble(IReadOnlyNotifiablePropertyObject properties, string key, double defaultValue)
    {
        var value = properties.GetValue(key,
            PropertyValue.Create(defaultValue.ToString(CultureInfo.InvariantCulture)));

        return value.ToString(out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : defaultValue;
    }

    static double GetDouble(IReadOnlyNotifiablePropertyObject properties, string key, double defaultValue)
        => properties.GetValue(key, PropertyValue.Create(defaultValue)).ToDouble(out var value)
            ? value
            : defaultValue;

    // ── Segmentation ──

    Piece? FindNextDirtyPiece(double startTime, double endTime)
    {
        if (mNeedResegment)
            Resegment();

        foreach (var piece in mPieces)
        {
            if (!piece.Dirty || piece.Failed || piece.Synthesizing)
                continue;

            if (piece.EndTime < startTime || piece.StartTime > endTime)
                continue;

            return piece;
        }

        return null;
    }

    void Resegment()
    {
        mNeedResegment = false;

        var groups = new List<List<IInstrumentSynthesisNote>>();
        List<IInstrumentSynthesisNote>? current = null;
        double groupMaxEnd = 0;

        foreach (var note in mContext.Notes)
        {
            double start = note.StartTime.Value;
            double end = note.EndTime.Value;

            if (current is null || start > groupMaxEnd)
            {
                current = [];
                groups.Add(current);
                groupMaxEnd = end;
            }
            else
            {
                groupMaxEnd = Math.Max(groupMaxEnd, end);
            }

            current.Add(note);
        }

        var newPieces = new List<Piece>(groups.Count);

        foreach (var group in groups)
        {
            var existing = mPieces.FirstOrDefault(piece => piece.Notes.SequenceEqual(group));

            if (existing is not null)
            {
                mPieces.Remove(existing);
                existing.StartTime = group[0].StartTime.Value;
                existing.EndTime = group.Max(note => note.EndTime.Value);
                newPieces.Add(existing);
            }
            else
            {
                newPieces.Add(new Piece
                {
                    Notes = group,
                    StartTime = group[0].StartTime.Value,
                    EndTime = group.Max(note => note.EndTime.Value),
                    Dirty = true,
                });
            }
        }

        foreach (var piece in mPieces)
            piece.Segment?.Dispose();

        mPieces.Clear();
        mPieces.AddRange(newPieces);
        mStatusChanged.Invoke();
    }

    // ── Status ──

    IReadOnlyList<SynthesisStatusSegment> BuildStatus()
    {
        var result = new List<SynthesisStatusSegment>(mPieces.Count);

        foreach (var piece in mPieces)
        {
            var status = piece.Failed ? SynthesisSegmentStatus.Failed
                : piece.Synthesizing ? SynthesisSegmentStatus.Synthesizing
                : piece.Dirty || (!piece.Empty && piece.Segment is null) ? SynthesisSegmentStatus.Pending
                : SynthesisSegmentStatus.Synthesized;

            result.Add(new SynthesisStatusSegment
            {
                StartTime = piece.StartTime,
                EndTime = piece.EndTime,
                Status = status,
                Message = piece.Failed ? piece.Error : piece.Synthesizing ? "rendering" : null,
                Progress = piece.Synthesizing ? Volatile.Read(ref piece.Progress) : 0,
            });
        }

        return result;
    }

    // ── Change tracking ──

    void MarkNoteDirty(IInstrumentSynthesisNote note)
    {
        foreach (var piece in mPieces)
        {
            if (!piece.Notes.Contains(note))
                continue;

            piece.Dirty = true;
            piece.Failed = false;
            piece.Error = null;
            piece.Empty = false;
        }

        // Note boundaries may change the overlap grouping.
        mNeedResegment = true;
    }

    void OnNotesChanged(IInstrumentSynthesisNote note) => mNeedResegment = true;

    void OnPartPropertiesChanged()
    {
        foreach (var piece in mPieces)
        {
            piece.Dirty = true;
            piece.Failed = false;
            piece.Error = null;
            piece.Empty = false;
        }

        mStatusChanged.Invoke();
    }

    void OnCommitted()
    {
        if (mNeedResegment)
            Resegment();
    }

    // ── Types ──

    sealed class Piece
    {
        public required IReadOnlyList<IInstrumentSynthesisNote> Notes;
        public double StartTime;
        public double EndTime;
        public bool Dirty;
        public bool Failed;
        public bool Synthesizing;
        public bool Empty;
        public string? Error;
        public double Progress;
        public IAudioSegment? Segment;
    }

    sealed class NoteSpec
    {
        public double StartTime;
        public double EndTime;
        public int Pitch;
        public int Velocity;
        public double DetuneCents;
    }

    sealed class TimedEvent
    {
        public double Time;
        public MidiEvent Event = null!;
    }

    sealed class RenderedPiece
    {
        public float[] Audio = [];
        public long SampleOffset;
        public int SampleRate;
    }

    sealed class RenderSettings
    {
        public double SampleRate;
        public int BlockSize;
        public double PreRollMs;
        public double TailMs;
        public double GainDb;
        public int Transpose;
        public int MidiChannel;
        public string StereoMode = StereoModes.MonoSum;
    }

    sealed class AutomationCurves
    {
        public IAutomationEvaluator? PitchBend;
        public IAutomationEvaluator? Modulation;
        public IAutomationEvaluator? Volume;
        public IAutomationEvaluator? Expression;
        public IAutomationEvaluator? Sustain;

        public void AppendBlockEvents(AutomationState state, double time, List<MidiEvent> events, int channel)
        {
            if (PitchBend is { } bend)
            {
                // The track is in semitones. MIDI pitch bend is 14-bit; map
                // +/-2 semitones to the full range and clamp the rest.
                int value = (int)Math.Clamp(8192 + Evaluation(bend, time) / 2.0 * 8192, 0, 16383);

                if (value != state.PitchBend)
                {
                    events.Add(new MidiEvent
                    {
                        Type = MidiEventKind.PitchBend,
                        Channel = channel,
                        Value = value,
                    });
                    state.PitchBend = value;
                }
            }

            AppendController(Modulation, state.Modulation, 1, time, events, channel,
                value => state.Modulation = value);

            AppendController(Volume, state.Volume, 7, time, events, channel,
                value => state.Volume = value);

            AppendController(Expression, state.Expression, 11, time, events, channel,
                value => state.Expression = value);

            if (Sustain is { } sustain)
            {
                int value = Evaluation(sustain, time) >= 64 ? 127 : 0;

                if (value != state.Sustain)
                {
                    events.Add(new MidiEvent
                    {
                        Type = MidiEventKind.ControlChange,
                        Channel = channel,
                        Controller = 64,
                        Value = value,
                    });
                    state.Sustain = value;
                }
            }
        }

        static void AppendController(IAutomationEvaluator? evaluator, int last, int controller, double time,
            List<MidiEvent> events, int channel, Action<int> store)
        {
            if (evaluator == null)
                return;

            int value = (int)Math.Clamp(Math.Round(Evaluation(evaluator, time)), 0, 127);

            if (value == last)
                return;

            events.Add(new MidiEvent
            {
                Type = MidiEventKind.ControlChange,
                Channel = channel,
                Controller = controller,
                Value = value,
            });
            store(value);
        }

        static double Evaluation(IAutomationEvaluator evaluator, double time)
        {
            var positions = new double[1] { time };
            var results = new double[1];
            evaluator.Evaluate(positions, results);
            return results[0];
        }
    }

    sealed class AutomationState
    {
        public int PitchBend = -1;
        public int Modulation = -1;
        public int Volume = -1;
        public int Expression = -1;
        public int Sustain = -1;
    }

    sealed class ParameterAutomationRuntime
    {
        public long Id;
        public double Min;
        public double Max;
        public IAutomationEvaluator Evaluator = null!;
        public double LastValue;
        public bool HasValue;
    }

    // Ten minutes at 192 kHz is already 460 MB of float; a piece longer than
    // this is almost certainly a sustained drone, and the host can split it.
    const int MaxRenderSeconds = 600;

    readonly IInstrumentSynthesisContext mContext;
    readonly CatalogEntry mEntry;
    readonly VstInstrumentSettings mSettings;
    volatile IReadOnlyList<ParameterInfo> mAutomatedParameters;
    readonly DisposableManager mSubscriptions = new();
    readonly List<Piece> mPieces = [];
    readonly ActionEvent mStatusChanged = new();
    readonly SemaphoreSlim mRenderLock = new(1, 1);

    Action? mOnDisposed;
    VstInstrumentBridgeClient? mClient;
    VstInstrumentBridgeClient? mEventClient;
    int mAutoCaptureRunning;
    int mAutoCaptureAgain;
    int mSlot;
    double mLoadedSampleRate;
    int mLoadedBlockSize;
    string? mLoadedState;
    int mPluginLatencySamples;
    int mPluginTailSamples;
    int MaxRenderSamples => (int)Math.Min(int.MaxValue - 1, MaxRenderSeconds * 192000L);

    bool mNeedResegment;
    volatile bool mReloadRequested;
    volatile bool mDisposed;
}