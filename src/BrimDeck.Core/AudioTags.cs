using System.Buffers.Binary;
using System.Text;

namespace BrimDeck.Core;

// The title, artist and lyrics written in a local audio file's tags: Vorbis comments in FLAC, ID3v2 in MP3 (and an ID3v2 tag
// some FLAC files carry ahead of their stream). Other formats are not read. Lyrics are kept as written, timed (LRC) or not.
public sealed record AudioTags(string Title, string Artist, IReadOnlyList<string> Lyrics)
{
    // A tag holds a few kilobytes of text; a larger block is a picture or damage, and is skipped rather than read.
    private const int MaxBlock = 4 * 1024 * 1024;

    public static AudioTags? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    public static AudioTags? Read(Stream stream)
    {
        var magic = new byte[4];
        if (!Fill(stream, magic)) return null;
        AudioTags? id3 = null;
        // "ID3", the major version, then the revision, flags and a syncsafe size.
        if (magic[0] == 'I' && magic[1] == 'D' && magic[2] == '3')
        {
            var rest = new byte[6];
            if (!Fill(stream, rest)) return null;
            byte version = magic[3], flags = rest[1];
            int size = Syncsafe(rest.AsSpan(2, 4));
            if (size > MaxBlock) return null;
            var body = new byte[size];
            if (!Fill(stream, body)) return null;
            id3 = ReadId3(version, flags, body);
            if (version == 4 && (flags & 0x10) != 0 && !Fill(stream, new byte[10])) return id3; // footer
            if (!Fill(stream, magic)) return id3;
        }
        if (magic[0] == 'f' && magic[1] == 'L' && magic[2] == 'a' && magic[3] == 'C')
            return ReadFlac(stream) is { } flac ? Merge(flac, id3) : id3;
        return id3;
    }

    private static AudioTags Merge(AudioTags first, AudioTags? second) => second is null ? first
        : new(first.Title.Length > 0 ? first.Title : second.Title, first.Artist.Length > 0 ? first.Artist : second.Artist, [.. first.Lyrics, .. second.Lyrics]);

    // The metadata blocks follow "fLaC"; VORBIS_COMMENT (type 4) holds the tags as little-endian length-prefixed "KEY=value".
    private static AudioTags? ReadFlac(Stream stream)
    {
        var head = new byte[4];
        while (Fill(stream, head))
        {
            bool last = (head[0] & 0x80) != 0;
            int type = head[0] & 0x7F, length = head[1] << 16 | head[2] << 8 | head[3];
            if (type == 4 && length <= MaxBlock)
            {
                var block = new byte[length];
                return Fill(stream, block) ? ReadVorbis(block) : null;
            }
            if (last || !stream.CanSeek) return null;
            stream.Seek(length, SeekOrigin.Current);
        }
        return null;
    }

    private static AudioTags? ReadVorbis(byte[] block)
    {
        var span = block.AsSpan();
        if (span.Length < 8) return null;
        int vendor = BinaryPrimitives.ReadInt32LittleEndian(span);
        if (vendor < 0 || vendor > span.Length - 8) return null;
        span = span[(4 + vendor)..];
        int count = BinaryPrimitives.ReadInt32LittleEndian(span); span = span[4..];
        var titles = new List<string>(); var artists = new List<string>(); var lyrics = new List<string>();
        for (int i = 0; i < count && span.Length >= 4; i++)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(span);
            if (length < 0 || length > span.Length - 4) break;
            string comment = Encoding.UTF8.GetString(span.Slice(4, length)); span = span[(4 + length)..];
            int equals = comment.IndexOf('=');
            if (equals <= 0) continue;
            string key = comment[..equals].ToUpperInvariant(), value = comment[(equals + 1)..];
            if (key == "TITLE") titles.Add(value);
            else if (key == "ARTIST") artists.Add(value);
            // QueMusic reads every key that starts with LYRICS; foobar2000 and others write UNSYNCEDLYRICS.
            else if (key.StartsWith("LYRICS", StringComparison.Ordinal) || key == "UNSYNCEDLYRICS") lyrics.Add(value);
        }
        return new(string.Join(" / ", titles.Select(t => t.Trim()).Where(t => t.Length > 0)),
            string.Join(" / ", artists.Select(a => a.Trim()).Where(a => a.Length > 0)), lyrics);
    }

    private static AudioTags ReadId3(byte version, byte flags, byte[] body)
    {
        string title = "", artist = ""; var lyrics = new List<string>();
        if (version is < 2 or > 4) return new(title, artist, lyrics);
        // Version 2.3 and earlier remove unsynchronisation from the whole tag; 2.4 marks it on each frame.
        if (version < 4 && (flags & 0x80) != 0) body = Resync(body);
        int position = 0;
        if (version >= 3 && (flags & 0x40) != 0 && body.Length >= 4)
        {
            int extended = version == 4 ? Syncsafe(body.AsSpan(0, 4)) : BinaryPrimitives.ReadInt32BigEndian(body) + 4;
            position = Math.Clamp(extended, 0, body.Length);
        }
        int headerLength = version == 2 ? 6 : 10;
        while (position + headerLength <= body.Length && body[position] != 0)
        {
            string id = Encoding.ASCII.GetString(body, position, version == 2 ? 3 : 4);
            int size = version switch
            {
                2 => body[position + 3] << 16 | body[position + 4] << 8 | body[position + 5],
                3 => BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(position + 4)),
                _ => Syncsafe(body.AsSpan(position + 4, 4)),
            };
            int frameFlags = version == 2 ? 0 : body[position + 9];
            position += headerLength;
            if (size < 0 || size > body.Length - position) break;
            var data = body.AsSpan(position, size); position += size;
            // Compressed or encrypted frames are not read.
            if (version == 3 && (frameFlags & 0xC0) != 0 || version == 4 && (frameFlags & 0x0C) != 0) continue;
            byte[] frame = data.ToArray();
            if (version == 4)
            {
                if ((frameFlags & 0x02) != 0) frame = Resync(frame);
                if ((frameFlags & 0x01) != 0) frame = frame.Length >= 4 ? frame[4..] : [];
            }
            if (frame.Length == 0) continue;
            switch (id)
            {
                case "TIT2" or "TT2" when title.Length == 0: title = JoinValues(Text(frame[0], frame.AsSpan(1))); break;
                case "TPE1" or "TP1" when artist.Length == 0: artist = JoinValues(Text(frame[0], frame.AsSpan(1))); break;
                case "USLT" or "ULT" when frame.Length > 4:
                    {
                        var rest = frame.AsSpan(4); // encoding, then a three-letter language
                        int end = Terminator(frame[0], rest);
                        if (end >= 0) lyrics.Add(Text(frame[0], rest[(end + Width(frame[0]))..]).TrimEnd('\0'));
                        break;
                    }
                case "TXXX" or "TXX":
                    {
                        var rest = frame.AsSpan(1);
                        int end = Terminator(frame[0], rest);
                        if (end < 0) break;
                        string description = Text(frame[0], rest[..end]);
                        if (description.StartsWith("LYRICS", StringComparison.OrdinalIgnoreCase) || description.Equals("UNSYNCEDLYRICS", StringComparison.OrdinalIgnoreCase))
                            lyrics.Add(Text(frame[0], rest[(end + Width(frame[0]))..]).TrimEnd('\0'));
                        break;
                    }
            }
        }
        return new(title, artist, lyrics);
    }

    // ID3v2.4 separates several values of one text frame with a null.
    private static string JoinValues(string text) => string.Join(" / ", text.Split('\0').Select(v => v.Trim()).Where(v => v.Length > 0));

    private static int Width(byte encoding) => encoding is 1 or 2 ? 2 : 1;

    private static int Terminator(byte encoding, ReadOnlySpan<byte> data)
    {
        if (Width(encoding) == 1) return data.IndexOf((byte)0);
        for (int i = 0; i + 1 < data.Length; i += 2) if (data[i] == 0 && data[i + 1] == 0) return i;
        return -1;
    }

    private static string Text(byte encoding, ReadOnlySpan<byte> data) => encoding switch
    {
        0 => Encoding.Latin1.GetString(data),
        1 => data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(data[2..])
            : data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE ? Encoding.Unicode.GetString(data[2..]) : Encoding.Unicode.GetString(data),
        2 => Encoding.BigEndianUnicode.GetString(data),
        _ => Encoding.UTF8.GetString(data),
    };

    // Unsynchronisation inserts a zero after every 0xFF; removing it restores the data.
    private static byte[] Resync(byte[] data)
    {
        var result = new List<byte>(data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            result.Add(data[i]);
            if (data[i] == 0xFF && i + 1 < data.Length && data[i + 1] == 0) i++;
        }
        return [.. result];
    }

    private static int Syncsafe(ReadOnlySpan<byte> bytes) => (bytes[0] & 0x7F) << 21 | (bytes[1] & 0x7F) << 14 | (bytes[2] & 0x7F) << 7 | (bytes[3] & 0x7F);

    private static bool Fill(Stream stream, Span<byte> buffer)
    {
        try { stream.ReadExactly(buffer); return true; }
        catch (EndOfStreamException) { return false; }
    }
}
