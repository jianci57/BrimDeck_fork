using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;
using BrimDeck.Native;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

int checks = 0;
void Check(string name, bool condition) { if (!condition) throw new InvalidOperationException("FAIL " + name); checks++; Console.WriteLine("PASS " + name); }
async Task Until(string name, Func<bool> test, int milliseconds = 4000)
{
    var end = Environment.TickCount64 + milliseconds;
    while (!test() && Environment.TickCount64 < end) await Task.Delay(20);
    Check(name, test());
}
NeteaseBridgeTests.Run(Check);
KugouConnectionTests.Run(Check);
using var self = Process.GetCurrentProcess();
var owner = new NeteaseProcess(self.Id, self.StartTime.ToUniversalTime().Ticks, self.MainModule!.FileName, null, 0);
var root = Path.Combine(Path.GetTempPath(), "BrimDeck-MediaTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    byte[] table = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(); NeteaseLog.Decode(table);
    byte[] inverse = new byte[256]; for (int i = 0; i < 256; i++) inverse[table[i]] = (byte)i;
    byte[] Encode(string s) => Encoding.UTF8.GetBytes(s).Select(b => inverse[b]).ToArray();
    long tick = Environment.TickCount64 - 10000;
    string Line(string payload) => $"[{self.Id}:1:0921/210000:{++tick}:INFO:app.cpp(1)] 【playing】,{payload}\n";
    string Track(string title, string id = "42") => "\"playOneTrackInPlayingList\",{\"track\":{\"id\":\"" + id + "\",\"name\":\"" + title + "\",\"duration\":180000,\"artists\":[],\"album\":{}}}";
    string path = Path.Combine(root, "cloudmusic.elog");
    await File.WriteAllBytesAsync(path, Encode(Line(Track("首曲")) + Line("\"native播放资源load完成，开始播放\",{\"songId\":\"42\"}") + Line("\"setPlayingPosition\",18.75") + Line("\"pause\",\"42_first\",\"42\"")));
    using (var log = new NeteaseLogReader(owner, path))
    {
        log.Start();
        await Until("Startup tail replay reconstructs paused media", () => log.State.Track is { State: MediaState.Paused, Title: "首曲" });
        Check("Reader holds exact position and only one file offset", Math.Abs(log.State.Track!.ReportedPosition.TotalSeconds - 18.751) < .01 && log.Offset == new FileInfo(path).Length);
        string missing = Path.Combine(root, "temporarily-missing.elog");
        File.Move(path, missing);
        await Until("Missing log clears estimated progress instead of advancing stale state", () => log.State.Track is { PositionKnown: false, State: MediaState.Unknown });
        File.Move(missing, path);
        await Until("Readable log replays after a file interruption", () => log.State.Track is { PositionKnown: true, State: MediaState.Paused });
        var encoded = Encode(Line(Track("中文跨字节", "43")));
        int cut = encoded.Length - 8;
        await using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) await append.WriteAsync(encoded.AsMemory(0, cut));
        await Task.Delay(100);
        Check("Partial lines cannot replace current media", log.State.Track?.Title == "首曲");
        await using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) await append.WriteAsync(encoded.AsMemory(cut));
        await Until("Watcher processes appended UTF8 without polling the entire log", () => log.State.Track?.Title == "中文跨字节");
        await File.WriteAllBytesAsync(path, Encode(Line(Track("轮转曲目", "44"))));
        await Until("Truncation rebuilds the current state", () => log.State.Track?.Title == "轮转曲目");
        string replacement = Path.Combine(root, "replacement.elog");
        await File.WriteAllBytesAsync(replacement, Encode(Line(Track("同长替换", "45"))));
        File.Move(replacement, path, true);
        await Until("Replaced files are detected by identity as well as length", () => log.State.Track?.Title == "同长替换");
        log.Dispose();
        Check("Disposal releases the log watcher", !log.Watching);
        await File.WriteAllBytesAsync(path, Encode(Line(Track("不应读取", "46")))); await Task.Delay(100);
        Check("Disposed reader does not process later writes", log.State.Track?.Title == "同长替换");
    }
    {
        // A long-running song: its start record lies before the tail, followed by unrelated log lines.
        string Filler(int bytes) { var text = new StringBuilder(); while (text.Length < bytes) text.Append($"[{self.Id}:1:0921/210000:{++tick}:INFO:ui.cpp(1)] 界面刷新 {new string('x', 200)}\n"); return text.ToString(); }
        string Song(string id) => Line(Track("长时间播放", id)) + Line("\"native播放资源load完成，开始播放\",{\"songId\":\"" + id + "\"}") + Line("\"setPlayingPosition\",30");
        string Toggle(string id) => Line("\"resume\",\"" + id + "_play\",\"" + id + "\"") + Line("\"pause\",\"" + id + "_play\",\"" + id + "\"");
        async Task<NeteaseLogReader> Replay(string name, string content)
        {
            string file = Path.Combine(root, name);
            await File.WriteAllBytesAsync(file, Encode(content));
            var reader = new NeteaseLogReader(owner, file, tailBytes: 4096, maxReplayBytes: 65536); reader.Start();
            await Until(name + " finishes its startup replay", () => reader.Offset == new FileInfo(file).Length);
            return reader;
        }
        long Size(string name) => new FileInfo(Path.Combine(root, name)).Length;
        using (var reader = await Replay("widened.elog", Song("50") + Filler(20000) + Toggle("50")))
        {
            await Until("A start record beyond the tail is found by widening the replay", () => reader.State.Track is { Title: "长时间播放", State: MediaState.Paused, PositionKnown: true });
            Check("The widened replay keeps the song's timeline", reader.State.Track!.Duration.TotalSeconds == 180 && reader.ReplayStart < Size("widened.elog") - 4096);
        }
        using (var reader = await Replay("bounded.elog", Song("51") + Filler(100000) + Toggle("51")))
        {
            await Task.Delay(100);
            Check("Widening stops at the maximum replay size", reader.State.Track is null && reader.ReplayStart == Size("bounded.elog") - 65536);
        }
        using (var reader = await Replay("quiet.elog", Song("52") + Filler(20000)))
        {
            await Task.Delay(100);
            Check("A tail without playback events is not widened", reader.State.Track is null && reader.ReplayStart == Size("quiet.elog") - 4096);
        }
    }
    using (var qq = new QqMediaConnection())
    {
        var accept = typeof(QqMediaConnection).GetMethod("Accept", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Send(string text) { using var json = JsonDocument.Parse(text); accept.Invoke(qq, [json.RootElement]); }
        Send("""{"reply":"login","code":0}""");
        Send("""{"notify":"song","data":{"title":"QQ song","singer":"Artist","album":"Album","picurl":"https://y.gtimg.cn/cover.jpg"}}""");
        Check("Actual QQ song payload has no invented song id or modes", qq.Snapshot is { SongId: null, CanShuffle: false, CanRepeat: false });
        Send("""{"notify":"progress","data":{"playtime":1000,"duration":180000}}""");
        Check("QQ pipe publishes duration and seek capability", qq.Snapshot is { CanSeek: true } && qq.Snapshot.Position?.TotalSeconds == 1);
        Send("""{"notify":"song","data":{"title":"Next","singer":"Artist","songmid":"003abc123XYZ"}}""");
        Check("Known QQ mid supplies exact lyrics identity and clears old time", qq.Snapshot is { SongId: "qq:003abc123XYZ", Position: null, TimelinePending: true, CanSeek: false });
        var beforeLyric = qq.Snapshot;
        Send("""{"notify":"lyric","data":{"lyric":"only one line"}}""");
        Check("Malformed QQ lyrics cannot replace current state", ReferenceEquals(qq.Snapshot, beforeLyric));
        Send("""{"notify":"lyric","data":{"text":"第一句","karaoke":[{"start":45455,"duration":162,"text":"第"},{"start":45617,"duration":150,"text":"一句"}]}}""");
        Check("QQ current line is available without full lyrics or a timeline", qq.Snapshot is { CurrentLyric: "第一句", SyncedLyrics: null, Position: null });
        Check("QQ full karaoke line retains its full-song timestamp", qq.Snapshot?.CurrentLyricStart?.TotalMilliseconds == 45455);
        beforeLyric = qq.Snapshot;
        Send("""{"notify":"lyric","data":{"text":"第一句","karaoke":[{"start":45617,"duration":150,"text":"一"}]}}""");
        Check("QQ word-only changes do not republish the same lyric line", ReferenceEquals(qq.Snapshot, beforeLyric));
        Send("""{"notify":"lyric","data":{"text":"第一句","karaoke":[{"start":85455,"duration":162,"text":"第一句"}]}}""");
        Check("Repeated QQ lyric text updates when its full-song cue changes", !ReferenceEquals(qq.Snapshot, beforeLyric) && qq.Snapshot?.CurrentLyricStart?.TotalMilliseconds == 85455);
        Send("""{"notify":"status","data":{"value":4}}""");
        Check("QQ pause preserves the current line and cue", qq.Snapshot is { State: MediaState.Paused, CurrentLyric: "第一句" } && qq.Snapshot.CurrentLyricStart?.TotalMilliseconds == 85455);
        Send("""{"notify":"lyric","data":{"text":"第二句"}}""");
        Check("QQ updates lyrics while paused and clears a missing cue", qq.Snapshot is { CurrentLyric: "第二句", CurrentLyricStart: null });
        Send("""{"notify":"lyric","data":{"text":""}}""");
        Check("QQ empty lyric notification clears the previous line", qq.Snapshot?.CurrentLyric == "");
        Send("""{"notify":"lyric","data":{"text":"上一首歌词","karaoke":[{"start":120000,"text":"上一首歌词"}]}}""");
        Send("""{"notify":"song","data":{"title":"Next","singer":"Artist","songmid":"OtherSongId123"}}""");
        Check("QQ same-title song identity change clears old lyrics and cue", qq.Snapshot is { SongId: "qq:OtherSongId123", CurrentLyric: null, CurrentLyricStart: null });
        Send("""{"notify":"lyric","data":{"text":"当前句","karaoke":[{"start":12000,"text":"当前句"}]}}""");
        Send("""{"notify":"song","data":{"title":"Next","singer":"Artist","songmid":"OtherSongId123"}}""");
        Check("QQ duplicate metadata retains the current lyric and cue", qq.Snapshot?.CurrentLyric == "当前句" && qq.Snapshot.CurrentLyricStart?.TotalMilliseconds == 12000);
        Send("""{"notify":"status","data":{"value":2}}""");
        Check("QQ stopped state clears the current lyric and cue", qq.Snapshot is { CurrentLyric: null, CurrentLyricStart: null });
        Send("""{"notify":"lyric","data":{"text":"上一首歌词"}}""");
        Send("""{"notify":"song","data":{"title":"Third","singer":"Artist"}}""");
        Check("QQ track change without an ID clears old lyrics", qq.Snapshot is { Title: "Third", CurrentLyric: null });
        Check("QQ modes cannot dispatch", await qq.CommandAsync(MediaCommand.Shuffle, MediaState.Paused) == DesktopCommandResult.Unavailable && await qq.CommandAsync(MediaCommand.Repeat, MediaState.Paused) == DesktopCommandResult.Unavailable);
    }
    using var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start(); int port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
    var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
    await using var server = builder.Build(); server.UseWebSockets();
    WebSocket? active = null; string binding = "", lastCommand = ""; int commands = 0, connections = 0; bool loseReply = false;
    var write = new SemaphoreSlim(1, 1);
    async Task Wire(object value)
    {
        await write.WaitAsync();
        try { if (active is { State: WebSocketState.Open } socket) await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value).AsMemory(), WebSocketMessageType.Text, true, default); }
        finally { write.Release(); }
    }
    object Snapshot(string id = "42", double? position = 18000, bool canLike = false, bool liked = false) => new { title = "CDP song", artist = "Artist", album = "Album", songId = id, durationMs = 180000, positionMs = position, state = "paused", mode = "playCycle", canControl = true, canSeek = true, canMode = true, canLike, liked };
    server.MapGet("/json", () => Results.Json(new[] { new { type = "page", webSocketDebuggerUrl = $"ws://127.0.0.1:{port}/devtools/page/test" } }));
    server.Map("/devtools/page/test", async context =>
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync(); active = socket; Interlocked.Increment(ref connections);
        var bytes = new byte[32768]; using var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var read = await socket.ReceiveAsync(bytes.AsMemory(), default);
                if (read.MessageType == WebSocketMessageType.Close) break;
                message.Write(bytes, 0, read.Count); if (!read.EndOfMessage) continue;
                using var json = JsonDocument.Parse(message.ToArray()); message.SetLength(0);
                var packet = json.RootElement; int id = packet.GetProperty("id").GetInt32(); string method = packet.GetProperty("method").GetString()!;
                if (method == "Runtime.addBinding") binding = packet.GetProperty("params").GetProperty("name").GetString()!;
                object value = new { };
                if (method == "Runtime.evaluate")
                {
                    string expression = packet.GetProperty("params").GetProperty("expression").GetString()!;
                    if (expression.Contains(".command(")) { commands++; lastCommand = expression; if (loseReply) continue; value = new { dispatched = true }; }
                    else if (expression.Contains(".keepalive(")) value = true;
                    else value = new { ready = true, snapshot = Snapshot() };
                }
                await Wire(new { id, result = new { result = new { value } } });
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(active, socket)) active = null; }
    });
    await server.StartAsync();
    var debugOwner = owner with { DebugPort = port };
    Check("Debug endpoint validates listener PID and start time", NeteaseCdpConnection.OwnsPort(debugOwner) && !NeteaseCdpConnection.OwnsPort(debugOwner with { Started = owner.Started + 1 }));
    using (var cdp = new NeteaseCdpConnection(debugOwner))
    {
        await cdp.StartAsync(); await Until("CDP discovers and attaches the playback bridge", () => cdp.Ready);
        Check("CDP initial snapshot has real position and all controls", cdp.Snapshot is { CanSeek: true, CanShuffle: true, CanRepeat: true, SongId: "netease:42" } && cdp.Snapshot.Position?.TotalSeconds == 18);
        await Wire(new { method = "Runtime.bindingCalled", @params = new { name = binding, executionContextId = 1, payload = JsonSerializer.Serialize(Snapshot("43", null)) } });
        await Until("CDP track change clears stale position", () => cdp.Snapshot is { SongId: "netease:43", Position: null, CanSeek: false });
        Check("CDP unavailable seek does not dispatch", await cdp.SeekAsync(TimeSpan.FromSeconds(5)) == DesktopCommandResult.Unavailable && commands == 0);
        await Wire(new { method = "Runtime.bindingCalled", @params = new { name = binding, executionContextId = 1, payload = JsonSerializer.Serialize(Snapshot("43", 22000)) } });
        await Until("CDP binding restores real progress", () => cdp.Snapshot?.Position?.TotalSeconds == 22);
        Check("CDP command reports exactly one dispatch", await cdp.CommandAsync(MediaCommand.Next, MediaState.Paused) == DesktopCommandResult.Sent && commands == 1);
        loseReply = true;
        Check("Lost CDP command reply remains uncertain and is not resent", await cdp.CommandAsync(MediaCommand.Next, MediaState.Paused) == DesktopCommandResult.Uncertain && commands == 2);
        loseReply = false;
        Check("CDP like is unavailable while the page reports it cannot like", await cdp.CommandAsync(MediaCommand.Like, MediaState.Paused) == DesktopCommandResult.Unavailable && commands == 2);
        await Wire(new { method = "Runtime.bindingCalled", @params = new { name = binding, executionContextId = 1, payload = JsonSerializer.Serialize(Snapshot("43", 22000, canLike: true, liked: true)) } });
        await Until("CDP snapshot carries the liked state", () => cdp.Snapshot is { CanLike: true, Liked: true });
        Check("CDP like names the current song on the current bridge", await cdp.CommandAsync(MediaCommand.Like, MediaState.Paused) == DesktopCommandResult.Sent && commands == 3
            && lastCommand.StartsWith($"window.{NeteaseCdpConnection.BridgeObject}.command(", StringComparison.Ordinal) && lastCommand.Contains("\"command\":\"like\"") && lastCommand.Contains("\"songId\":\"43\""));
        await Wire(new { method = "Runtime.executionContextsCleared", @params = new { } });
        await Until("CDP reconnects after page reload", () => connections >= 2 && cdp.Ready, 6000);
    }
    await server.StopAsync();
    Console.WriteLine($"{checks} native media checks passed.");
}
finally { Directory.Delete(root, true); }
