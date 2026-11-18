using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace TuneLab.VstInstruments.Bridge;

// Shared-memory audio ring: the data path between the extension and the helper.
//
// Why not send audio over the control pipe? The pipe is fine for control
// messages, but it is a blocking stream. A helper that stalls inside a plugin
// stops reading, and the writer then blocks too. The ring turns that into a
// bounded spin/wait that can time out and be reported, which is what an offline
// renderer needs when a third-party plugin hangs.
//
// This file is compiled by BOTH projects (the extension links it, the bridge
// project includes it). It may only use the BCL and the platform APIs available
// on both sides. Changing the layout is a two-sided change.
//
// Memory ordering: x86/x64 are TSO, so "write the audio, then Volatile.Write the
// sequence" publishes a complete block. ARM64 needs the release barrier that
// Volatile.Write emits. The consumer reads the sequence with Volatile.Read
// before touching the block.
//
// Concurrency: strictly single-producer/single-consumer. One direction, one
// producer, one consumer. No CAS and no locks are needed.
public static class SharedAudioLayout
{
    // Four slots: today a synchronous render has at most one block in flight,
    // but the extra capacity costs little and lets a future pipeline hide the
    // helper's scheduling latency without a protocol change.
    public const int SlotCount = 4;

    // Upper bound per block. The renderer uses 512-frame blocks, so 4096 is
    // generous. Exceeding it is rejected, never truncated: the section size is
    // fixed and an out-of-bounds write would corrupt the mapping.
    public const int MaxBlockFrames = 4096;

    // Stereo output needs two planes; four leaves room for a future sidechain
    // or multi-channel instrument output without changing the section size.
    public const int MaxChannels = 4;

    // Per-block metadata (see BlockHeader).
    public const int BlockHeaderBytes = 16;

    public static readonly int SlotBytes = BlockHeaderBytes + MaxChannels * MaxBlockFrames * 4;

    // Section = header (two longs) + all slots.
    public static readonly int SectionBytes = 16 + SlotCount * SlotBytes;

    // Offset of the audio payload inside a slot.
    public const int BlockAudioOffset = BlockHeaderBytes;
}

// Per-block metadata. Exactly 16 bytes so offsets stay constants.
[StructLayout(LayoutKind.Explicit, Size = SharedAudioLayout.BlockHeaderBytes)]
internal unsafe struct BlockHeader
{
    [FieldOffset(0)] public int Slot;
    [FieldOffset(4)] public int Frames;
    [FieldOffset(8)] public int Channels;

    // Round-trip tag. The producer increments it per block and the consumer
    // only accepts the tag it just sent. Without it, a block left behind by a
    // timeout would be mistaken for the next request's result.
    [FieldOffset(12)] public int Tag;
}

// Section header: two monotonically increasing counters used as a ring index.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SectionHeader
{
    // Blocks the producer has published.
    public long WriteSeq;

    // Blocks the consumer has released back to the ring.
    public long ReadSeq;
}

// One direction of the ring.
//
// The two sides use different halves of the API:
//   host h2b: Publish / (the bridge reads)
//   host b2h: Read + Release
public sealed unsafe class SharedAudioRing : IDisposable
{
    // Spin budget (~tens of microseconds). Enough to absorb a normal scheduling
    // hiccup, short enough not to burn a core when the peer is really stuck.
    const int SpinBudget = 4096;

    readonly MemoryMappedFile mSection;
    readonly MemoryMappedViewAccessor mAccessor;
    readonly SectionHeader* mHeader;
    readonly byte* mBlocks;

    // Producer-only: the next sequence number to publish.
    long mNextWrite;

    readonly EventWaitHandle? mDataAvailable;

    // Unix: the section is a file in the host's private temp directory because
    // .NET does not support named shared memory there. Non-null means this
    // instance created the file and must delete it on Dispose.
    readonly string? mOwnedSectionPath;

    public bool HasGate => mDataAvailable != null;

    // Diagnostics for the extension UI and the bridge log.
    public long Published { get; private set; }

    public long TimedOutReads { get; private set; }

    public long TimedOutWrites { get; private set; }

    SharedAudioRing(MemoryMappedFile section, EventWaitHandle? gate, string? ownedSectionPath)
    {
        mSection = section;
        mDataAvailable = gate;
        mOwnedSectionPath = ownedSectionPath;

        mAccessor = section.CreateViewAccessor(0, SharedAudioLayout.SectionBytes, MemoryMappedFileAccess.ReadWrite);

        byte* pointer = null;
        mAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);

        mHeader = (SectionHeader*)pointer;
        mBlocks = pointer + sizeof(SectionHeader);
    }

    // Unix: named maps are not supported by .NET. Both sides agree on a file
    // path under the host's private temp directory and map that file.
    static string UnixSectionPath(string sectionName)
    {
        Directory.CreateDirectory(UnixSectionDirectory);
        return Path.Combine(UnixSectionDirectory, sectionName);
    }

    // Directory swept for stale sections after a hard kill.
    public static string UnixSectionDirectory => Path.Combine(Path.GetTempPath(), "tunelab-vstinstrument-bridge");

    /// <summary>Create a section on the producer side.</summary>
    public static SharedAudioRing Create(string sectionName, string? gateName)
    {
        MemoryMappedFile section;
        string? ownedPath = null;

        if (OperatingSystem.IsWindows())
        {
            section = MemoryMappedFile.CreateNew(sectionName, SharedAudioLayout.SectionBytes);
        }
        else
        {
            ownedPath = UnixSectionPath(sectionName);

            // FileMode.Create truncates: a crash may leave a file with stale
            // sequence numbers behind.
            using (var file = new FileStream(ownedPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
                file.SetLength(SharedAudioLayout.SectionBytes);

            section = MemoryMappedFile.CreateFromFile(ownedPath, FileMode.Open, null,
                SharedAudioLayout.SectionBytes, MemoryMappedFileAccess.ReadWrite);
        }

        // ManualReset, not AutoReset: a Set that arrives before a waiter does
        // must remain visible. AutoReset would swallow it and the next wait
        // would never wake.
        var gate = gateName == null ? null : CreateOrOpenGate(gateName, create: true);

        return new SharedAudioRing(section, gate, ownedPath);
    }

    // Named synchronization primitives are not available on Unix in .NET.
    // The consumer falls back to spin + Yield. Correct, just less efficient.
    static EventWaitHandle? CreateOrOpenGate(string gateName, bool create)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        return create
            ? new EventWaitHandle(true, EventResetMode.ManualReset, gateName)
            : EventWaitHandle.OpenExisting(gateName);
    }

    /// <summary>Open an existing section on the consumer side.</summary>
    public static SharedAudioRing Open(string sectionName, string? gateName)
    {
        var section = OperatingSystem.IsWindows()
            ? MemoryMappedFile.OpenExisting(sectionName)
            : MemoryMappedFile.CreateFromFile(UnixSectionPath(sectionName), FileMode.Open, null,
                SharedAudioLayout.SectionBytes, MemoryMappedFileAccess.ReadWrite);

        var gate = gateName == null ? null : CreateOrOpenGate(gateName, create: false);

        return new SharedAudioRing(section, gate, null);
    }

    // ── Producer ──

    /// <summary>
    /// Publish one block (planes concatenated by channel: L0..Ln, R0..Rn).
    /// Returns false when the consumer does not release a slot in time (the
    /// helper is stuck or gone); the caller should treat that as a failed block.
    /// </summary>
    public bool TryPublish(int slot, float[][] planes, int channels, int frames, int waitMs, int tag = 0)
    {
        if (channels <= 0 || frames <= 0 || channels > SharedAudioLayout.MaxChannels
            || frames > SharedAudioLayout.MaxBlockFrames)
        {
            return false;
        }

        if (!WaitForWritable(waitMs))
        {
            TimedOutWrites++;
            return false;
        }

        long sequence = mNextWrite;
        int index = (int)(sequence % SharedAudioLayout.SlotCount);

        var header = (BlockHeader*)(mBlocks + index * SharedAudioLayout.SlotBytes);

        header->Slot = slot;
        header->Frames = frames;
        header->Channels = channels;
        header->Tag = tag;

        float* audio = (float*)(mBlocks + index * SharedAudioLayout.SlotBytes + SharedAudioLayout.BlockAudioOffset);

        int written = 0;
        for (int ch = 0; ch < channels; ch++)
        {
            int count = Math.Min(frames, planes[ch].Length);

            new ReadOnlySpan<float>(planes[ch], 0, count)
                .CopyTo(new Span<float>(audio + written, count));

            written += count;
        }

        // Publish last. Volatile.Write also stops the JIT from moving the data
        // stores after the sequence store.
        Volatile.Write(ref mHeader->WriteSeq, sequence + 1);

        mNextWrite = sequence + 1;
        Published++;

        mDataAvailable?.Set();

        return true;
    }

    // ── Consumer ──

    /// <summary>
    /// Take the oldest published block this consumer has not taken yet.
    /// Returns false on timeout (no data).
    /// </summary>
    public bool TryRead(int waitMs, out int slot, out float[][] planes, out int channels, out int frames,
        out int tag)
    {
        slot = 0;
        channels = 0;
        frames = 0;
        tag = 0;
        planes = [];

        long next = Volatile.Read(ref mHeader->ReadSeq);

        if (!WaitUntil(mHeader, next + 1, waitMs))
        {
            TimedOutReads++;
            return false;
        }

        int index = (int)(next % SharedAudioLayout.SlotCount);
        var header = (BlockHeader*)(mBlocks + index * SharedAudioLayout.SlotBytes);

        slot = header->Slot;
        frames = Math.Min(header->Frames, SharedAudioLayout.MaxBlockFrames);
        channels = Math.Min(header->Channels, SharedAudioLayout.MaxChannels);
        tag = header->Tag;

        float* audio = (float*)(mBlocks + index * SharedAudioLayout.SlotBytes + SharedAudioLayout.BlockAudioOffset);

        int read = 0;
        planes = new float[channels][];
        for (int ch = 0; ch < channels; ch++)
        {
            planes[ch] = new float[frames];

            // Span<float> lengths are element counts, not byte counts.
            new ReadOnlySpan<float>(audio + read, frames).CopyTo(planes[ch]);
            read += frames;
        }

        return true;
    }

    /// <summary>
    /// Read the block whose tag matches <paramref name="tag"/>. Stale blocks
    /// (left over from a timed-out request) are released and skipped.
    /// The caller still owns Release() on success.
    /// </summary>
    public bool TryReadMatching(int tag, int waitMs, out int slot, out float[][] planes,
        out int channels, out int frames)
    {
        slot = 0;
        channels = 0;
        frames = 0;
        planes = [];

        long start = Stopwatch.GetTimestamp();

        while (true)
        {
            int remaining = waitMs;

            if (waitMs > 0)
            {
                long elapsedMs = elapsed(start);
                if (elapsedMs >= waitMs)
                    return false;

                remaining = (int)Math.Max(1, waitMs - elapsedMs);
            }

            if (!TryRead(remaining, out int readSlot, out var readPlanes, out int readChannels,
                out int readFrames, out int readTag))
            {
                return false;
            }

            if (readTag == tag)
            {
                slot = readSlot;
                planes = readPlanes;
                channels = readChannels;
                frames = readFrames;
                return true;
            }

            Release();

            if (waitMs == 0
                && Volatile.Read(ref mHeader->WriteSeq) <= Volatile.Read(ref mHeader->ReadSeq))
            {
                return false;
            }
        }
    }

    /// <summary>Return the block just read to the ring.</summary>
    public void Release()
    {
        long next = Volatile.Read(ref mHeader->ReadSeq);
        Volatile.Write(ref mHeader->ReadSeq, next + 1);
    }

    // ── Waiting ──

    // Wait for the consumer to free a slot. At most SlotCount blocks are in
    // flight, so the condition is "published - released < SlotCount".
    //
    // This path deliberately does not touch the gate: freeing a slot is rare
    // and always happens, so spin + Yield is enough. Using the same gate in
    // both directions would cause cross-wakeups (a real bug in the reference
    // implementation).
    bool WaitForWritable(int waitMs)
    {
        long start = Stopwatch.GetTimestamp();

        while (true)
        {
            long readSeq = Volatile.Read(ref mHeader->ReadSeq);

            if (mNextWrite - readSeq < SharedAudioLayout.SlotCount)
                return true;

            Thread.Yield();

            if (!SpinOrWait(waitMs, start))
                return false;
        }
    }

    // Wait until WriteSeq reaches target. Spin first, then sleep on the gate.
    bool WaitUntil(SectionHeader* counter, long target, int waitMs)
    {
        long start = Stopwatch.GetTimestamp();
        int spins = 0;

        while (true)
        {
            if (Volatile.Read(ref counter->WriteSeq) >= target)
                return true;

            if (spins < SpinBudget)
            {
                Thread.SpinWait(64);
                spins++;
                continue;
            }

            if (mDataAvailable is { } gate)
            {
                long elapsedMs = elapsed(start);

                if (elapsedMs >= waitMs)
                    return false;

                if (gate.WaitOne((int)Math.Max(1, waitMs - elapsedMs)))
                {
                    // Woken does not mean the target arrived (the signal may be
                    // left over from the previous block). Clear it and continue.
                    if (Volatile.Read(ref counter->WriteSeq) >= target)
                    {
                        gate.Reset();
                        return true;
                    }

                    gate.Reset();
                    continue;
                }
            }
            else
            {
                Thread.Yield();
            }

            if (elapsed(start) >= waitMs)
                return false;
        }
    }

    static long elapsed(long startTimestamp)
        => (Stopwatch.GetTimestamp() - startTimestamp) * 1000 / Stopwatch.Frequency;

    // Second stage after spinning: sleep on the gate, or Yield without one.
    bool SpinOrWait(int waitMs, long startTimestamp)
    {
        if (mDataAvailable is { } gate)
        {
            long elapsedMs = elapsed(startTimestamp);

            if (elapsedMs >= waitMs)
                return false;

            gate.WaitOne((int)Math.Max(1, waitMs - elapsedMs));
        }
        else
        {
            Thread.Yield();
        }

        return elapsed(startTimestamp) < waitMs;
    }

    public void Dispose()
    {
        try
        {
            mAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        catch (Exception)
        {
        }

        mAccessor.Dispose();
        mSection.Dispose();
        mDataAvailable?.Dispose();

        // The creator deletes the Unix backing file. The mapping stays valid
        // for the other side until its own handles close.
        if (mOwnedSectionPath is { Length: > 0 } path)
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
}
