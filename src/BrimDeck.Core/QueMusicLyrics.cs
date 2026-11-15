using System.Text;
using System.Text.Json;

namespace BrimDeck.Core;

// Lyrics for a local file playing in QueMusic, read the way QueMusic reads them: a .lrc (or timed .txt) of the same name beside
// the file, then the lyrics in the file's tags. Windows reports QueMusic's song without its file, so the file is the newest entry
// of QueMusic's play history, which QueMusic saves to its settings file about two seconds after a song starts (measured). The
// entry is taken only when the file's own title and artist are the song's: the history still names the previous song until then.
// A song QueMusic streams is looked up online, by the platform's song ID the history records where the lookups here take one.
public sealed partial class LyricsService
{
    public const string QueMusicSource = "QueMusic";
    // How long to wait for QueMusic to record the song before the lyric services are asked.
    private static readonly TimeSpan QueMusicWait = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan QueMusicPoll = TimeSpan.FromMilliseconds(500);
    private readonly Func<string> _queMusicSettings;

    private static string QueMusicSettingsFile() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BroNekoX", "QueMusic.ini");

    // The lyrics of a local file, or for a song QueMusic streams the song's ID in the form the lyric services below take it
    // ("" when its platform has none here). Neither when QueMusic has not recorded the song; the lyric services are asked then.
    private sealed record QueMusicMatch(Lyrics? Lyrics, string SongId);

    private async Task<QueMusicMatch> FindQueMusic(MediaTrack track, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + QueMusicWait;
        DateTime written = default;
        while (true)
        {
            var file = new FileInfo(_queMusicSettings());
            if (file.Exists && file.LastWriteTimeUtc != written)
            {
                written = file.LastWriteTimeUtc;
                if (ReadQueMusicLatest(file.FullName) is { } latest)
                {
                    // A streamed song is recorded with its title from the start, and with its platform's own ID.
                    if (latest.Source != -1) { if (SameText(latest.Title, track.Title)) return new(null, QueMusicSongId(latest.Source, latest.Path)); }
                    else if (LocalPath(latest.Path) is { } path && File.Exists(path))
                    {
                        var tags = AudioTags.Read(path);
                        if (IsQueMusicSong(track, path, tags)) return new(LocalLyrics(path, tags, track), "");
                    }
                }
            }
            if (DateTime.UtcNow >= deadline) return new(null, "");
            await Task.Delay(QueMusicPoll, token);
        }
    }

    // QueMusic numbers its built-in platforms (api/ApiActions.h): KuGou 0, NetEase 1, Bilibili 2, QQ Music 3, Spotify 4,
    // Apple Music 5, plug-ins from 100. It records a KuGou song by its file hash, a NetEase song by its ID and a QQ Music song by
    // its mid, which the lookups below take as they take them from those players' own apps. The others are found by title.
    internal static string QueMusicSongId(int source, string id) => source switch
    {
        0 when id.Length == 32 && id.All(char.IsAsciiHexDigit) => "kugou:" + id.ToLowerInvariant(),
        1 when id.Length is > 0 and <= 16 && id.All(char.IsAsciiDigit) => "netease:" + id,
        3 when id.Length is >= 8 and <= 32 && id.All(char.IsAsciiLetterOrDigit) => "qq:" + id,
        _ => "",
    };

    internal sealed record QueMusicEntry(string Path, int Source, string Title);

    // QueMusic keeps its play history, newest first, as JSON in the playHistory value of its INI settings.
    internal static QueMusicEntry? ReadQueMusicLatest(string settingsFile)
    {
        string text;
        try
        {
            using var stream = new FileStream(settingsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (IniValue(text, "Options", "playHistory") is not { } json) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) return null;
                string path = entry.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
                int source = entry.TryGetProperty("source", out var s) && s.TryGetInt32(out int value) ? value : -1;
                string title = entry.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
                return path.Length > 0 ? new(path, source, title) : null;
            }
        }
        catch (JsonException) { }
        return null;
    }

    // A value as Qt's QSettings writes it to an INI file: a string with a comma, quote or other special character is quoted,
    // with C-style escapes inside.
    internal static string? IniValue(string text, string section, string key)
    {
        bool inSection = false;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith('[')) { inSection = line.Trim() == "[" + section + "]"; continue; }
            if (!inSection) continue;
            int equals = line.IndexOf('=');
            if (equals <= 0 || line[..equals].Trim() != key) continue;
            return Unescape(line[(equals + 1)..].Trim());
        }
        return null;
    }

    private static string Unescape(string value)
    {
        var result = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '"') continue;
            if (c != '\\' || i + 1 >= value.Length) { result.Append(c); continue; }
            char next = value[++i];
            switch (next)
            {
                case 'a': result.Append('\a'); break;
                case 'b': result.Append('\b'); break;
                case 'f': result.Append('\f'); break;
                case 'n': result.Append('\n'); break;
                case 'r': result.Append('\r'); break;
                case 't': result.Append('\t'); break;
                case 'v': result.Append('\v'); break;
                case 'x':
                    {
                        int start = i + 1, end = start;
                        while (end < value.Length && end - start < 4 && Uri.IsHexDigit(value[end])) end++;
                        if (end == start) { result.Append('x'); break; }
                        result.Append((char)Convert.ToInt32(value[start..end], 16)); i = end - 1;
                        break;
                    }
                case >= '0' and <= '7':
                    {
                        int start = i, end = start;
                        while (end < value.Length && end - start < 3 && value[end] is >= '0' and <= '7') end++;
                        result.Append((char)Convert.ToInt32(value[start..end], 8)); i = end - 1;
                        break;
                    }
                default: result.Append(next); break;
            }
        }
        return result.ToString();
    }

    private static string? LocalPath(string path)
    {
        if (!path.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return Path.IsPathFullyQualified(path) ? path : null;
        return Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;
    }

    // QueMusic shows a file's tag title and artist, or its file name when it has no title.
    internal static bool IsQueMusicSong(MediaTrack track, string path, AudioTags? tags)
    {
        if (tags is { Title.Length: > 0 }) { if (!SameText(tags.Title, track.Title)) return false; }
        else if (!SameText(Path.GetFileNameWithoutExtension(path), track.Title) && !SameText(Path.GetFileName(path), track.Title)) return false;
        if (tags is not { Artist.Length: > 0 } || track.Artist.Length == 0) return true;
        static string[] Names(string text) => text.Split(['/', ';', ',', '、', '&'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var known = Names(track.Artist);
        return Names(tags.Artist).Any(name => known.Any(other => SameText(name, other)));
    }

    private static bool SameText(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static Lyrics? LocalLyrics(string path, AudioTags? tags, MediaTrack track)
    {
        double duration = track.Duration.TotalSeconds;
        string stem = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path));
        foreach (var extension in new[] { ".lrc", ".txt" })
        {
            if (ReadText(stem + extension) is not { } text) continue;
            var timed = WithoutTranslations(Lyrics.Parse(text, null, duration, QueMusicSource));
            if (timed.Lines.Count > 0 || timed.Instrumental) return timed;
        }
        foreach (var text in tags?.Lyrics ?? [])
        {
            var lyrics = WithoutTranslations(Lyrics.Parse(text, text, duration, QueMusicSource));
            if (lyrics.Lines.Count > 0 || lyrics.Instrumental) return lyrics;
        }
        return null;
    }

    // A bilingual LRC follows each line with its translation at the same time; the panel shows the line as written.
    private static Lyrics WithoutTranslations(Lyrics lyrics)
    {
        if (!lyrics.Synced || lyrics.Lines.Count < 2) return lyrics;
        var lines = new List<LyricLine>(lyrics.Lines.Count);
        foreach (var line in lyrics.Lines)
            if (lines.Count == 0 || lines[^1].Seconds != line.Seconds) lines.Add(line);
        return lines.Count == lyrics.Lines.Count ? lyrics : lyrics with { Lines = lines };
    }

    // A lyric file is UTF-8, or in Chinese ANSI (GB18030) when it is not valid UTF-8.
    private static string? ReadText(string path)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxResponseBytes) return null;
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(54936).GetString(bytes);
        }
    }
}
