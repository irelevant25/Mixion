using System.Runtime.CompilerServices;

namespace VoicemeterAlt.Host.Audio;

/// <summary>
/// Single-producer / single-consumer lock-free float ring buffer.
///
/// Capacity is rounded up to the next power of two so the head/tail can be
/// masked instead of mod-reduced. Reads and writes use <see cref="Volatile"/>
/// so the producer's writes publish before the consumer observes them, and
/// vice-versa. After construction it never allocates — the audio path stays
/// off the GC.
///
/// One thread (and only one) writes via <see cref="Write"/>; another thread
/// (and only one) reads via <see cref="Read"/>. Calling either from multiple
/// threads is undefined behaviour.
/// </summary>
public sealed class RingBuffer
{
    private readonly float[] _buffer;
    private readonly int     _mask;

    // long counters that monotonically increase. The actual ring index is
    // (counter & _mask). Using long avoids ABA / wrap-around for any
    // realistic session length.
    private long _head; // next write index
    private long _tail; // next read index

    public int Capacity => _buffer.Length;

    public RingBuffer(int requestedCapacity)
    {
        if (requestedCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedCapacity));

        var cap = 1;
        while (cap < requestedCapacity) cap <<= 1;

        _buffer = new float[cap];
        _mask   = cap - 1;
    }

    /// <summary>Samples currently available to read.</summary>
    public int Available
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (int)(Volatile.Read(ref _head) - Volatile.Read(ref _tail));
    }

    /// <summary>Samples that can still be written without overrunning.</summary>
    public int FreeSpace
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Capacity - Available;
    }

    /// <summary>
    /// Copy <paramref name="src"/> into the buffer. Returns the number of
    /// samples actually written; if the buffer is full the producer drops the
    /// remainder rather than blocking. (Audio path: never block.)
    /// </summary>
    public int Write(ReadOnlySpan<float> src)
    {
        var head = Volatile.Read(ref _head);
        var tail = Volatile.Read(ref _tail);
        var free = Capacity - (int)(head - tail);
        if (free == 0) return 0;

        var toWrite = Math.Min(src.Length, free);
        var idx     = (int)(head & _mask);
        var firstChunk = Math.Min(toWrite, Capacity - idx);

        src.Slice(0, firstChunk).CopyTo(_buffer.AsSpan(idx));
        if (firstChunk < toWrite)
            src.Slice(firstChunk, toWrite - firstChunk).CopyTo(_buffer.AsSpan(0));

        Volatile.Write(ref _head, head + toWrite);
        return toWrite;
    }

    /// <summary>
    /// Drain up to <c>dst.Length</c> samples into <paramref name="dst"/>.
    /// Returns the number actually read.
    /// </summary>
    public int Read(Span<float> dst)
    {
        var tail = Volatile.Read(ref _tail);
        var head = Volatile.Read(ref _head);
        var avail = (int)(head - tail);
        if (avail == 0) return 0;

        var toRead     = Math.Min(dst.Length, avail);
        var idx        = (int)(tail & _mask);
        var firstChunk = Math.Min(toRead, Capacity - idx);

        _buffer.AsSpan(idx, firstChunk).CopyTo(dst);
        if (firstChunk < toRead)
            _buffer.AsSpan(0, toRead - firstChunk).CopyTo(dst.Slice(firstChunk));

        Volatile.Write(ref _tail, tail + toRead);
        return toRead;
    }

    /// <summary>Discards every queued sample. Caller-side, single-threaded.</summary>
    public void Clear()
    {
        Volatile.Write(ref _tail, Volatile.Read(ref _head));
    }
}
