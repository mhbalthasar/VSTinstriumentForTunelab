using System.Collections;
using TuneLab.Foundation;
using TuneLab.SDK;

namespace TuneLab.VstInstruments.Tests;

// Minimal TuneLab SDK implementations for the harness. They follow the SDK 1.2
// host-side interfaces (PartId / InvalidatePart / InvalidateNotes / note Id).
// The extension itself still compiles against 1.1; only the fakes need 1.2.
internal sealed class FakeProperty<T> : IReadOnlyNotifiableProperty<T>
{
    public FakeProperty(T value) => Value = value;

    public T Value { get; set; }
    public IActionEvent WillModify => mWillModify;
    public IActionEvent Modified => mModified;

    readonly ActionEvent mWillModify = new();
    readonly ActionEvent mModified = new();
}

internal sealed class FakePropertyObject : IReadOnlyNotifiablePropertyObject
{
    public FakePropertyObject(Dictionary<string, PropertyValue> values) => mValues = values;

    public static FakePropertyObject Empty { get; } = new(new Dictionary<string, PropertyValue>());

    public IActionEvent WillModify => mWillModify;
    public IActionEvent Modified => mModified;

    public IReadOnlyNotifiablePropertyObject Object(string key) => Empty;

    public PropertyValue GetValue(string key, PropertyValue defaultValue)
        => mValues.TryGetValue(key, out var value) ? value : defaultValue;

    readonly Dictionary<string, PropertyValue> mValues;
    readonly ActionEvent mWillModify = new();
    readonly ActionEvent mModified = new();
}

internal sealed class FakeNote : IInstrumentSynthesisNote
{
    public FakeNote(double startTime, double endTime, int pitch)
    {
        StartTime = new FakeProperty<double>(startTime);
        EndTime = new FakeProperty<double>(endTime);
        Pitch = new FakeProperty<int>(pitch);
    }

    public string Id { get; } = Guid.NewGuid().ToString();
    public IReadOnlyNotifiableProperty<double> StartTime { get; }
    public IReadOnlyNotifiableProperty<double> EndTime { get; }
    public IReadOnlyNotifiableProperty<int> Pitch { get; }
    public IReadOnlyNotifiablePropertyObject Properties { get; } = FakePropertyObject.Empty;
    public IInstrumentSynthesisNote? Next => null;
    public IInstrumentSynthesisNote? Previous => null;
}

internal sealed class FakeNoteList : IReadOnlyNotifiableLinkedList<IInstrumentSynthesisNote>
{
    public IActionEvent<IInstrumentSynthesisNote> ItemAdded => mItemAdded;
    public IActionEvent<IInstrumentSynthesisNote> ItemRemoved => mItemRemoved;
    public IActionEvent MembershipModified => mMembershipModified;
    public IEnumerable<IInstrumentSynthesisNote> Items => mNotes;
    public int Count => mNotes.Count;
    public IInstrumentSynthesisNote? First => mNotes.Count == 0 ? null : mNotes[0];
    public IInstrumentSynthesisNote? Last => mNotes.Count == 0 ? null : mNotes[^1];

    public void Add(IInstrumentSynthesisNote note)
    {
        mNotes.Add(note);
        mItemAdded.Invoke(note);
        mMembershipModified.Invoke();
    }

    public IEnumerator<IInstrumentSynthesisNote> GetEnumerator() => mNotes.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    readonly List<IInstrumentSynthesisNote> mNotes = [];
    readonly ActionEvent<IInstrumentSynthesisNote> mItemAdded = new();
    readonly ActionEvent<IInstrumentSynthesisNote> mItemRemoved = new();
    readonly ActionEvent mMembershipModified = new();
}

internal sealed class FakeAudioSegment : IAudioSegment
{
    public FakeAudioSegment(long sampleOffset, int sampleCount, int sampleRate)
    {
        SampleOffset = sampleOffset;
        Samples = new float[Math.Max(0, sampleCount)];
        SampleRate = sampleRate;
    }

    public long SampleOffset { get; private set; }
    public float[] Samples { get; private set; }
    public int SampleRate { get; }
    public bool Committed { get; private set; }
    public bool Disposed { get; private set; }

    public void Write(int offset, ReadOnlySpan<float> samples)
    {
        samples.CopyTo(Samples.AsSpan(offset));
        Committed = false;
    }

    public void Commit() => Committed = true;

    public void Resize(long sampleOffset, int sampleCount)
    {
        var resized = new float[Math.Max(0, sampleCount)];
        long copyStart = Math.Max(SampleOffset, sampleOffset);
        long copyEnd = Math.Min(SampleOffset + Samples.Length, sampleOffset + resized.Length);

        if (copyEnd > copyStart)
        {
            Array.Copy(Samples, (int)(copyStart - SampleOffset), resized,
                (int)(copyStart - sampleOffset), (int)(copyEnd - copyStart));
        }

        Samples = resized;
        SampleOffset = sampleOffset;
        Committed = false;
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeSettingsContext : IExtensionSettingsContext
{
    public FakeSettingsContext(PropertyObject settings) => Settings = settings;

    public PropertyObject Settings { get; }
}

internal sealed class FakePartView : IInstrumentSynthesisPartView
{
    public string InstrumentId { get; init; } = string.Empty;
    public string PartId { get; init; } = string.Empty;
    public IReadOnlyList<IInstrumentSynthesisNoteView> Notes => [];
    public PropertyObject PartProperties { get; } = PropertyObject.Empty;
    public IReadOnlyMap<string, IAutomationEvaluator> Automations => Map<string, IAutomationEvaluator>.Empty;
}

internal sealed class FakePartPropertyContext : IInstrumentSynthesisPartPropertyContext
{
    public IReadOnlyList<IInstrumentSynthesisPartView> Parts { get; init; } = [];
}

// Mimics the host's writable data object shape used by PartStateWriter:
// context.Part.Properties.SetValue(string, PropertyValue).
internal sealed class FakeWritableProperties
{
    public Dictionary<string, PropertyValue> Values { get; } = [];

    public object SetValue(string key, PropertyValue value)
    {
        Values[key] = value;
        return value;
    }

    public void RemoveValue(string key)
        => Values.Remove(key);
}

internal sealed class FakeWritablePart
{
    public FakeWritableProperties Properties { get; } = new();
}

internal sealed class FakeWritableContext
{
    public FakeWritablePart Part { get; } = new();
}

internal sealed class FakeContext : IInstrumentSynthesisContext
{
    public FakeContext(string instrumentId, FakeNoteList notes, FakePropertyObject partProperties)
    {
        InstrumentId = instrumentId;
        Notes = notes;
        PartProperties = partProperties;
    }

    public string InstrumentId { get; }
    public string PartId { get; } = "fake-part";
    public IReadOnlyNotifiableLinkedList<IInstrumentSynthesisNote> Notes { get; }
    public IReadOnlyNotifiablePropertyObject PartProperties { get; }

    // The real host exposes InstrumentSynthesisContext.Part; the fake mirrors
    // that shape so PartStateWriter can write state back in the harness.
    public FakeWritablePart Part { get; } = new();
    public IReadOnlyMap<string, ISynthesisAutomation> Automations => Map<string, ISynthesisAutomation>.Empty;
    public IActionEvent Committed => mCommitted;
    public List<FakeAudioSegment> Segments { get; } = [];

    public InstrumentSynthesisSnapshot GetSnapshot(IReadOnlyList<IInstrumentSynthesisNote> notes)
    {
        var list = new List<InstrumentSynthesisNoteSnapshot>(notes.Count);

        foreach (var note in notes)
        {
            list.Add(new InstrumentSynthesisNoteSnapshot
            {
                StartTime = note.StartTime.Value,
                EndTime = note.EndTime.Value,
                Pitch = note.Pitch.Value,
                Properties = PropertyObject.Empty,
            });
        }

        return new InstrumentSynthesisSnapshot
        {
            Notes = list,
            PartProperties = PropertyObject.Empty,
            Automations = Map<string, SynthesisAutomationSnapshot>.Empty,
        };
    }

    public IAudioSegment CreateAudioSegment(long sampleOffset, int sampleCount, int sampleRate)
    {
        var segment = new FakeAudioSegment(sampleOffset, sampleCount, sampleRate);
        Segments.Add(segment);
        return segment;
    }

    public void InvalidatePart() { }

    public void InvalidateNotes(IEnumerable<string> noteIds) { }

    readonly ActionEvent mCommitted = new();
}

// No-op logger for the catalog loader.
internal sealed class NullLogger : ILogger
{
    public void Debug(object? value) { }
    public void Info(object? value) { }
    public void Warning(object? value) { }
    public void Error(object? value) { }
}