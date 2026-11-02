using System.Net;
using System.Text;
using BrimDeck.Core;

internal static class KugouTests
{
    internal static async Task Run(Action<string, bool> check)
    {
        Engine(check);
        Profile(check, "KuGou", KugouPatch.Profile, 12233);
        Profile(check, "NetEase", NeteasePatch.Profile, 21631);
        await LyricsLookup(check);
        check("KuGou's session is shown under its name and found by its process",
            MediaAppTarget.DisplayName("kugou", _ => null) == "酷狗音乐" && new MediaTrack { Source = "酷狗音乐" }.IsKugou && new MediaTrack { SongId = "kugou:" + new string('a', 32) }.IsKugou
            && MediaAppTarget.Plays(new MediaAppOwner(null, MediaAppTarget.ProcessName("kugou"), "kugou"), new MediaAppProcess(1, "KuGou", null, "kugou")));
    }

    // Three sites with prefix and suffix context, placed far apart in an otherwise empty file, so each window is found by its
    // bytes wherever it sits. The core is the only part written.
    private static LibcefPatch H(params (string Prefix, string Original, string Patched, string Suffix)[] sites) => new(12233,
        [.. sites.Select(s => new LibcefPatch.Site(Convert.FromHexString(s.Prefix), Convert.FromHexString(s.Original), Convert.FromHexString(s.Patched), Convert.FromHexString(s.Suffix)))]);
    private static readonly LibcefPatch Sample = H(
        ("1122", "AABBCCDD", "90909090", "3344"), ("5566", "EE01", "9091", "7788"), ("", "FEDCBA98", "11223344", "99"));

    // Places each site's original or patched window at a spaced-out offset; the bytes between stay zero.
    private static SparseStream Library(LibcefPatch patch, bool patched, bool[]? perSite = null)
    {
        var file = new SparseStream(0x10000L * (patch.Sites.Count + 2));
        for (int i = 0; i < patch.Sites.Count; i++)
        { file.Position = 0x10000L * (i + 1); file.Write(patch.Sites[i].Window(perSite?[i] ?? patched)); }
        file.Writes = 0; return file;
    }

    private static void Engine(Action<string, bool> check)
    {
        var library = Library(Sample, false);
        check("An unpatched file is recognized", Sample.Classify(library) == LibcefPatchState.Original);
        Sample.Write(library, true);
        check("Patching writes one core per site and nothing else", Sample.Classify(library) == LibcefPatchState.Patched && library.Writes == Sample.Sites.Count
            && library.Equivalent(Library(Sample, true)));
        Sample.Write(library, true);
        check("Patching a patched file writes nothing", library.Writes == Sample.Sites.Count);
        Sample.Write(library, false);
        check("Restoring writes the original bytes back", Sample.Classify(library) == LibcefPatchState.Original && library.Equivalent(Library(Sample, false)));

        var partial = Library(Sample, false, [true, false, false]);
        check("A file patched in part is recognized", Sample.Classify(partial) == LibcefPatchState.Partial);
        partial.Writes = 0; Sample.Write(partial, true);
        check("Patching a part-patched file writes only the sites still original", Sample.Classify(partial) == LibcefPatchState.Patched
            && partial.Writes == Sample.Sites.Count - 1 && partial.Equivalent(Library(Sample, true)));

        // A site whose window is neither form present (one byte of its core changed) is not a known build.
        var mixed = Library(Sample, false); mixed.Position = 0x10000L + Sample.Sites[0].Prefix.Length; mixed.Write([0x55]); mixed.Writes = 0;
        bool refused = false;
        try { Sample.Write(mixed, true); } catch (InvalidDataException) { refused = true; }
        check("A file a different build left unmatched is left alone", Sample.Classify(mixed) == LibcefPatchState.Unsupported && refused && mixed.Writes == 0);
        check("An empty file is not a known build", Sample.Classify(new SparseStream(0x40000)) == LibcefPatchState.Unsupported);

        // The file is searched a chunk at a time: a window across the first chunk's end, or wholly in the bytes the next chunk
        // starts with, is found once and written in place; a second copy elsewhere still makes the file unknown.
        var window = Sample.Sites[0].Window(false);
        bool acrossChunks = true;
        for (int offset = LibcefPatch.ChunkSize - window.Length - 2; offset <= LibcefPatch.ChunkSize + 1; offset++)
        {
            var file = new SparseStream(LibcefPatch.ChunkSize + 0x40000L);
            for (int i = 1; i < Sample.Sites.Count; i++) { file.Position = 0x10000L * i; file.Write(Sample.Sites[i].Window(false)); }
            file.Position = offset; file.Write(window);
            acrossChunks &= Sample.Classify(file) == LibcefPatchState.Original;
            Sample.Write(file, true);
            acrossChunks &= Sample.Classify(file) == LibcefPatchState.Patched
                && file.ReadAt(offset + Sample.Sites[0].Prefix.Length, 4).SequenceEqual(Sample.Sites[0].Patched);
            file.Position = LibcefPatch.ChunkSize + 0x20000L; file.Write(Sample.Sites[0].Window(true));
            acrossChunks &= Sample.Classify(file) == LibcefPatchState.Unsupported;
        }
        check("A window across a chunk boundary is found once and written in place", acrossChunks);
    }

    // Each real profile round-trips on a synthetic file built from its own site windows, and names the port it writes.
    private static void Profile(Action<string, bool> check, string name, LibcefPatch patch, int port)
    {
        var library = Library(patch, false);
        check($"{name}: an unpatched file is Original and patches to Patched", patch.Port == port
            && patch.Classify(library) == LibcefPatchState.Original);
        patch.Write(library, true);
        check($"{name}: patching sets every site and restoring puts it back", patch.Classify(library) == LibcefPatchState.Patched
            && library.Writes == patch.Sites.Count && Patched(patch, library));
        patch.Write(library, false);
        check($"{name}: restored file matches the original", patch.Classify(library) == LibcefPatchState.Original && library.Equivalent(Library(patch, false)));
    }
    private static bool Patched(LibcefPatch patch, SparseStream library) => library.Equivalent(Library(patch, true));

    private static async Task LyricsLookup(Action<string, bool> check)
    {
        const string Hash = "d62a437719aba2b0da0a5d2ad5e14e51";
        string lyric = Convert.ToBase64String(Encoding.UTF8.GetBytes("[id:$00000000]\n[00:01.47]我们之间的回忆\n[00:03.60]全部都小心地收集"));
        var requested = new List<Uri>();
        HttpResponseMessage Reply(Uri uri)
        {
            lock (requested) requested.Add(uri);
            string body = uri.Host switch
            {
                "krcs.kugou.com" => "{\"status\":200,\"candidates\":[{\"id\":\"1\",\"accesskey\":\"0123456789ABCDEF0123456789ABCDEF\",\"song\":\"同名歌\",\"singer\":\"别人\",\"duration\":181000},"
                    + "{\"id\":\"511461352\",\"accesskey\":\"68AE8550EA30C9BDA452384B1C5B8E0C\",\"song\":\"把回忆拼好给你\",\"singer\":\"张妙格\",\"duration\":181000}]}",
                "lyrics.kugou.com" => uri.Query.Contains("id=511461352") ? "{\"status\":200,\"content\":\"" + lyric + "\"}" : "{\"status\":200,\"content\":\"\"}",
                _ => "{}"
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
        var song = new MediaTrack { Title = "把回忆拼好给你", Artist = "张妙格", End = TimeSpan.FromSeconds(181), Source = "酷狗音乐", SongId = "kugou:" + Hash };
        using (var http = new HttpClient(new Handler(Reply))) using (var service = new LyricsService(http))
        {
            var lyrics = await service.FindAsync(song, true, default);
            check("A song KuGou plays is looked up by its hash on KuGou first", lyrics.Source == "酷狗音乐" && lyrics.At(2).Current == "我们之间的回忆"
                && requested[0].Host == "krcs.kugou.com" && requested[0].Query.Contains("hash=" + Hash) && requested[0].Query.Contains("duration=181000")
                && requested.All(u => u.Host.EndsWith(".kugou.com", StringComparison.Ordinal) && u.Scheme == "https"));
        }
        requested.Clear();
        using (var http = new HttpClient(new Handler(Reply))) using (var service = new LyricsService(http))
        {
            var lyrics = await service.FindAsync(song with { Source = "Spotify", SongId = "" }, true, default);
            int kugou = requested.FindIndex(u => u.Host == "krcs.kugou.com");
            check("Another player's song asks KuGou last, by name, and takes only the candidate with the same title and artist",
                lyrics.Source == "酷狗音乐" && kugou > 0 && requested.Take(kugou).All(u => u.Host != "lyrics.kugou.com")
                && requested[kugou].Query.Contains("keyword=" + Uri.EscapeDataString("张妙格 - 把回忆拼好给你"))
                && requested.Where(u => u.Host == "lyrics.kugou.com").All(u => u.Query.Contains("id=511461352")));
        }
    }

    private sealed class Handler(Func<Uri, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request.RequestUri!));
    }

    // A file of the given length holding only what was written to it, so a 140 MB library costs a few bytes.
    private sealed class SparseStream(long length) : Stream
    {
        private readonly Dictionary<long, byte> _bytes = [];
        public int Writes;
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = (int)Math.Max(0, Math.Min(count, length - Position));
            for (int i = 0; i < read; i++) buffer[offset + i] = _bytes.GetValueOrDefault(Position + i);
            Position += read; return read;
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++) _bytes[Position + i] = buffer[offset + i];
            Position += count; Writes++;
        }
        public byte[] ReadAt(long offset, int count) { Position = offset; var bytes = new byte[count]; ReadExactly(bytes); return bytes; }
        public bool Equivalent(SparseStream other) => _bytes.Keys.Union(other._bytes.Keys).All(k => _bytes.GetValueOrDefault(k) == other._bytes.GetValueOrDefault(k));
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => length + offset };
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
