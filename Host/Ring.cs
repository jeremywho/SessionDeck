namespace SessionDeck.Host;

/// <summary>
/// Bounded scrollback with absolute byte sequence numbers. <see cref="End"/> is the sequence just
/// past the newest byte; a viewer that last saw sequence N asks for everything after N and gets
/// exactly that when the ring still holds it, or the whole ring (and a new start) when it doesn't.
/// </summary>
internal sealed class Ring
{
    readonly byte[] _buf;
    int _head;          // index of the oldest byte
    int _count;
    long _base;         // sequence number of the oldest byte

    public Ring(int capacity) { _buf = new byte[capacity]; }

    public long Start { get { lock (this) return _base; } }
    public long End { get { lock (this) return _base + _count; } }

    public void Append(ReadOnlySpan<byte> data)
    {
        lock (this)
        {
            if (data.Length >= _buf.Length)
            {
                data[^_buf.Length..].CopyTo(_buf);
                _base += _count + data.Length - _buf.Length;
                _head = 0;
                _count = _buf.Length;
                return;
            }
            int overflow = _count + data.Length - _buf.Length;
            if (overflow > 0)
            {
                _head = (_head + overflow) % _buf.Length;
                _count -= overflow;
                _base += overflow;
            }
            int tail = (_head + _count) % _buf.Length;
            int first = Math.Min(data.Length, _buf.Length - tail);
            data[..first].CopyTo(_buf.AsSpan(tail));
            data[first..].CopyTo(_buf);
            _count += data.Length;
        }
    }

    /// <summary>Bytes from sequence <paramref name="after"/> to the end, clamped to what is held.</summary>
    public (long Start, byte[] Data) Since(long after)
    {
        lock (this)
        {
            long from = Math.Clamp(after, _base, _base + _count);
            int skip = (int)(from - _base);
            int len = _count - skip;
            var outp = new byte[len];
            int idx = (_head + skip) % _buf.Length;
            int first = Math.Min(len, _buf.Length - idx);
            Array.Copy(_buf, idx, outp, 0, first);
            Array.Copy(_buf, 0, outp, first, len - first);
            return (from, outp);
        }
    }
}
