using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

internal static class QueMusicLyricsTests
{
    private const string Bilingual = "[00:00.00]第一行歌词\n[00:00.00]First line\n[00:05.00]第二行歌词\n[00:05.00]Second line\n[00:10.50]第三行";

    public static async Task Run(string root, Action<string, bool> check)
    {
        string folder = Path.Combine(root, "quemusic"); Directory.CreateDirectory(folder);

        var flac = Flac(("TITLE", "人芝居"), ("ARTIST", "歌手甲"), ("ARTIST", "歌手乙"), ("LYRICS", Bilingual));
        var tags = AudioTags.Read(new MemoryStream(flac));
        check("FLAC Vorbis comments give the title, every artist and the lyrics, past a picture block",
            tags is { Title: "人芝居", Artist: "歌手甲 / 歌手乙", Lyrics: [Bilingual] });
        var ahead = AudioTags.Read(new MemoryStream([.. Id3(3, Frame3("TPE1", Utf16("前置歌手"))), .. Flac(("TITLE", "带前置标签"))]));
        check("An ID3 tag ahead of a FLAC stream is read with the stream's own tags", ahead is { Title: "带前置标签", Artist: "前置歌手" });
        var mp3 = AudioTags.Read(new MemoryStream(Id3(3, [.. Frame3("TIT2", Utf16("内嵌")), .. Frame3("TPE1", Utf16("歌手")),
            .. Frame3("USLT", [1, (byte)'c', (byte)'h', (byte)'i', 0xFF, 0xFE, 0, 0, .. Encoding.Unicode.GetBytes("[00:01.00]USLT 歌词")])])));
        check("ID3v2.3 UTF-16 title, artist and USLT lyrics are read", mp3 is { Title: "内嵌", Artist: "歌手", Lyrics: ["[00:01.00]USLT 歌词"] });
        var v24 = AudioTags.Read(new MemoryStream(Id3(4, [.. Frame4("TIT2", [3, .. Encoding.UTF8.GetBytes("新版")]),
            .. Frame4("TXXX", [3, .. Encoding.UTF8.GetBytes("LYRICS"), 0, .. Encoding.UTF8.GetBytes("[00:02.00]TXXX 歌词")])])));
        check("ID3v2.4 UTF-8 frames and TXXX lyrics are read", v24 is { Title: "新版", Lyrics: ["[00:02.00]TXXX 歌词"] });
        check("A file without tags gives none", AudioTags.Read(new MemoryStream(Encoding.ASCII.GetBytes("RIFF0000WAVE"))) is null);

        string song = Path.Combine(folder, "人芝居.flac"); File.WriteAllBytes(song, flac);
        string other = Path.Combine(folder, "上一首.flac"); File.WriteAllBytes(other, Flac(("TITLE", "上一首"), ("LYRICS", "[00:00.00]上一首的歌词")));
        string settings = Path.Combine(folder, "QueMusic.ini");
        var track = new MediaTrack { Source = "QueMusic", Title = "人芝居", Artist = "歌手甲", End = TimeSpan.FromSeconds(90) };
        using var web = new Web();

        WriteSettings(settings, (song, -1, "人芝居.flac"), (other, -1, "上一首.flac"));
        using (var service = new LyricsService(web.Http, queMusicSettings: () => settings))
        {
            var lyrics = await service.FindAsync(track, true, CancellationToken.None);
            check("QueMusic's newest local file gives its embedded lyrics, without the translation lines and without asking online",
                lyrics.Source == "QueMusic" && lyrics.Synced && lyrics.Lines.Select(l => l.Text).SequenceEqual(["第一行歌词", "第二行歌词", "第三行"])
                && web.Requests == 0);

            File.WriteAllText(Path.ChangeExtension(song, ".lrc"), "﻿[00:00.00]同名文件\r\n[00:03.00]第二行\r\n");
            lyrics = await service.FindAsync(track, true, CancellationToken.None);
            check("A .lrc of the same name comes before the file's own lyrics", lyrics.Lines is [{ Text: "同名文件" }, { Seconds: 3, Text: "第二行" }]);
            File.Delete(Path.ChangeExtension(song, ".lrc"));

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            File.WriteAllBytes(Path.ChangeExtension(song, ".txt"), Encoding.GetEncoding(936).GetBytes("[00:00.00]国标编码的歌词"));
            lyrics = await service.FindAsync(track, true, CancellationToken.None);
            check("A timed .txt in Chinese ANSI is read", lyrics.Lines is [{ Text: "国标编码的歌词" }]);
            File.WriteAllText(Path.ChangeExtension(song, ".txt"), "没有时间轴的说明文字");
            lyrics = await service.FindAsync(track, true, CancellationToken.None);
            check("An untimed .txt is not taken for lyrics", lyrics.Lines.Count == 3);
            File.Delete(Path.ChangeExtension(song, ".txt"));

            // QueMusic records the song about two seconds after it starts; until then the history names the previous one.
            WriteSettings(settings, (other, -1, "上一首.flac"));
            File.SetLastWriteTimeUtc(settings, DateTime.UtcNow.AddMinutes(-1));
            var pending = service.FindAsync(track, true, CancellationToken.None);
            await Task.Delay(1200);
            check("The previous song's file is not taken while QueMusic has yet to record the song", !pending.IsCompleted);
            WriteSettings(settings, (song, -1, "人芝居.flac"), (other, -1, "上一首.flac"));
            lyrics = await pending;
            check("The song's lyrics are read once QueMusic records it", lyrics.Lines.FirstOrDefault()?.Text == "第一行歌词");

            WriteSettings(settings, (song, -1, "人芝居.flac"));
            lyrics = await service.FindAsync(track with { Artist = "别的歌手" }, true, CancellationToken.None);
            check("A file whose artist is not the song's is not taken", lyrics.Lines.Count == 0 && web.Requests > 0);

            int asked = web.Requests;
            WriteSettings(settings, ("https://example.com/stream", 3, "在线歌曲"));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            lyrics = await service.FindAsync(track with { Title = "在线歌曲" }, true, CancellationToken.None);
            check("A song QueMusic streams goes straight to the lyric services", web.Requests > asked && watch.Elapsed < TimeSpan.FromSeconds(3));

            // NetEase, KuGou and QQ Music songs are looked up by the ID QueMusic recorded, not by title.
            var online = track with { Title = "在线歌曲", Artist = "歌手" };
            WriteSettings(settings, ("1234567", 1, "在线歌曲"));
            web.Uris.Clear(); await service.FindAsync(online, true, CancellationToken.None);
            check("A NetEase song QueMusic streams is looked up by its song ID",
                web.Uris.FirstOrDefault()?.AbsoluteUri.Contains("music.163.com/api/song/lyric?id=1234567", StringComparison.Ordinal) == true);
            WriteSettings(settings, ("ABCDEF0123456789ABCDEF0123456789", 0, "在线歌曲"));
            web.Uris.Clear(); await service.FindAsync(online with { Album = "酷狗" }, true, CancellationToken.None);
            check("A KuGou song is looked up by its file hash, in lower case",
                web.Uris.FirstOrDefault() is { Host: "krcs.kugou.com" } kugou && kugou.Query.Contains("keyword=&", StringComparison.Ordinal)
                && kugou.Query.Contains("hash=abcdef0123456789abcdef0123456789", StringComparison.Ordinal));
            WriteSettings(settings, ("002WCV372JMZJw", 3, "在线歌曲"));
            web.Uris.Clear(); await service.FindAsync(online with { Album = "QQ" }, true, CancellationToken.None);
            check("A QQ Music song is looked up by its mid, without a title search",
                web.Uris.Count > 0 && web.Uris.All(uri => uri.Host.EndsWith(".y.qq.com", StringComparison.Ordinal)
                    && !uri.AbsolutePath.Contains("search", StringComparison.Ordinal)
                    && Uri.UnescapeDataString(uri.Query).Contains("002WCV372JMZJw", StringComparison.Ordinal)));
            WriteSettings(settings, ("BV1xx411c7mD", 2, "在线歌曲"));
            web.Uris.Clear(); await service.FindAsync(online with { Album = "哔哩哔哩" }, true, CancellationToken.None);
            check("A song from a platform without an ID lookup here is found by title",
                web.Uris.FirstOrDefault()?.Host == "lrclib.net");

            string untitled = Path.Combine(folder, "无标签歌曲.flac"); File.WriteAllBytes(untitled, Flac(("LYRICS", "[00:00.00]无标签")));
            WriteSettings(settings, (new Uri(untitled).AbsoluteUri, -1, "无标签歌曲.flac"));
            lyrics = await service.FindAsync(track with { Title = "无标签歌曲.flac", Artist = "未知歌手" }, true, CancellationToken.None);
            check("A file without a title matches its file name, and a file URL is read as its path", lyrics.Lines is [{ Text: "无标签" }]);
        }

        using (var service = new LyricsService(web.Http, queMusicSettings: () => throw new InvalidOperationException("Another player read QueMusic's settings")))
        {
            await service.FindAsync(track with { Source = "foobar2000" }, true, CancellationToken.None);
            check("Other players never read QueMusic's settings", true);
        }
    }

    // QSettings writes the JSON as a quoted INI value with backslashes and quotes escaped (measured on QueMusic 0.7.2).
    private static void WriteSettings(string path, params (string Path, int Source, string Title)[] entries)
    {
        string json = JsonSerializer.Serialize(entries.Select(e => new { artist = "", cover = "", duration = 0, path = e.Path, source = e.Source, time = 1794582738334, title = e.Title }),
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        string value = "\"" + json.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        File.WriteAllText(path, $"[LastMedia]\nname=\n\n[Options]\nhistoryLimit=200\nplayHistory={value}\nlastQueue=[]\n\n[Style]\ntheme=0\n", new UTF8Encoding(false));
    }

    private static byte[] Flac(params (string Key, string Value)[] comments)
    {
        var vorbis = new List<byte>();
        void Int(int value) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, value); vorbis.AddRange(b); }
        var vendor = Encoding.UTF8.GetBytes("test"); Int(vendor.Length); vorbis.AddRange(vendor); Int(comments.Length);
        foreach (var (key, value) in comments) { var c = Encoding.UTF8.GetBytes(key + "=" + value); Int(c.Length); vorbis.AddRange(c); }
        static byte[] Block(int type, bool last, int length) => [(byte)(type | (last ? 0x80 : 0)), (byte)(length >> 16), (byte)(length >> 8), (byte)length];
        return [.. "fLaC"u8, .. Block(0, false, 34), .. new byte[34], .. Block(6, false, 100), .. new byte[100], .. Block(4, true, vorbis.Count), .. vorbis];
    }

    private static byte[] Utf16(string text) => [1, 0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)];
    private static byte[] Frame3(string id, byte[] data) { var size = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, data.Length); return [.. Encoding.ASCII.GetBytes(id), .. size, 0, 0, .. data]; }
    private static byte[] Frame4(string id, byte[] data) => [.. Encoding.ASCII.GetBytes(id), .. Syncsafe(data.Length), 0, 0, .. data];
    private static byte[] Id3(byte version, byte[] frames) => [.. "ID3"u8, version, 0, 0, .. Syncsafe(frames.Length), .. frames];
    private static byte[] Syncsafe(int n) => [(byte)(n >> 21 & 0x7F), (byte)(n >> 14 & 0x7F), (byte)(n >> 7 & 0x7F), (byte)(n & 0x7F)];

    private sealed class Web : IDisposable
    {
        private int _requests;
        public int Requests => _requests;
        public readonly List<Uri> Uris = [];
        public HttpClient Http { get; }
        public Web() => Http = new HttpClient(new Handler(this));
        public void Dispose() => Http.Dispose();
        private sealed class Handler(Web web) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref web._requests);
                lock (web.Uris) web.Uris.Add(request.RequestUri!);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }
    }
}
