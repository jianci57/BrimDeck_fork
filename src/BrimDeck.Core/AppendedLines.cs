using System.Buffers;

namespace BrimDeck.Core;

// The lines added to a session log since it was last read. The logs that LogReader and KimiSessions read only grow at their end,
// so each file remembers where its last complete line ended and a refresh reads from there.
internal static class AppendedLines
{
    public delegate void Line(ReadOnlyMemory<byte> line);

    // Hands each complete line after offset to line, and moves offset past it. A line still being written is left for the next
    // refresh. A line longer than maxLine (a huge tool result) is not kept whole: it is passed over in pieces, and offset moves
    // past it only once its end is found, so a long line still being written is looked at again on the next refresh and counted
    // once, when it is complete. dropped is called for such a line that held one of keys, the text every line that counts has:
    // only that leaves a gap in the statistics; others are dropped quietly.
    // The first chunk comes from the shared pool, so a refresh that finds many changed files does not allocate one for each of
    // them. A longer line gets a buffer of its own, collected after the read, instead of leaving large, rarely rented arrays in
    // the pool.
    public static void Read(Stream stream, ref long offset, int chunk, int maxLine, byte[][] keys, Line line, Action dropped)
    {
        // The last bytes of a piece stay in the buffer, so a key split between two reads is still found.
        int keyTail = keys.Max(key => key.Length) - 1;
        stream.Seek(offset, SeekOrigin.Begin);
        // capacity is how much of the buffer is used: the pool may hand out a larger array than asked for, and a line is measured
        // against maxLine, not against the array.
        int capacity = Math.Min(chunk, maxLine);
        var pooled = ArrayPool<byte>.Shared.Rent(capacity);
        var buffer = pooled;
        // The bytes passed over so far of a line over the limit.
        long skipped = 0;
        bool skipping = false, held = false;
        try
        {
            int filled = 0, read;
            while ((read = stream.Read(buffer, filled, capacity - filled)) > 0)
            {
                filled += read;
                int start = 0, newline;
                while ((newline = Array.IndexOf(buffer, (byte)'\n', start, filled - start)) >= 0)
                {
                    if (skipping)
                    {
                        if (held || Holds(buffer.AsSpan(start, newline - start), keys)) dropped();
                        skipping = held = false; offset += skipped; skipped = 0;
                    }
                    else line(buffer.AsMemory(start, newline - start));
                    start = newline + 1;
                }
                offset += start;
                Buffer.BlockCopy(buffer, start, buffer, 0, filled - start);
                filled -= start;
                if (filled < capacity) continue;
                // A single line fills the buffer: it grows up to the limit, beyond which the line is passed over.
                if (capacity < maxLine)
                {
                    capacity = (int)Math.Min(maxLine, capacity * 2L);
                    if (capacity > buffer.Length)
                    {
                        var larger = new byte[capacity];
                        Buffer.BlockCopy(buffer, 0, larger, 0, filled);
                        buffer = larger;
                    }
                }
                else
                {
                    held |= Holds(buffer.AsSpan(0, filled), keys);
                    skipping = true; skipped += filled - keyTail;
                    Buffer.BlockCopy(buffer, filled - keyTail, buffer, 0, keyTail);
                    filled = keyTail;
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(pooled); }
    }

    public static bool Holds(ReadOnlySpan<byte> span, byte[][] keys)
    {
        foreach (var key in keys) if (span.IndexOf(key) >= 0) return true;
        return false;
    }
}
