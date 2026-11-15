using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Native;

namespace ReadmeMedia;

// Renders the README images with BrimDeck's own panel code and invented data. Nothing is read from this computer:
// every snapshot, song and cover below is made up, the usage service points at an empty folder, the network is
// replaced by a handler that always fails, and media detection is never started. The panel window stays transparent
// and off screen; each image is taken from its visual tree with RenderTargetBitmap.
//
// The clipboard is never watched (ShelfService.Apply is not called) and no device is asked for its battery (the devices' sources
// are never made): both pages draw the invented entries below.
//
// Usage: ReadmeMedia <output folder> [zh-CN|en-US] [stills,themes,monitor,devices,shelf,hero,settings]  (settings only when named)
internal static class Program
{
    internal const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "readme-media");
        var language = args.Length > 1 ? args[1] : Loc.Chinese;
        Directory.CreateDirectory(output);
        int exit = 0;
        var app = new App();
        app.InitializeComponent();
        // App raises Startup as soon as the dispatcher runs, even without Application.Run. BrimDeck's OnStartup would
        // start a real instance with real data, or ask the running one to open its settings. OnStartup calls the base
        // method first, so an exception from the Startup event ends it before anything else happens.
        app.Startup += (_, _) => throw new StartupSkipped();
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) => { if (e.Exception is StartupSkipped) e.Handled = true; };
        // The one setting OnStartup applies before anything else: the narrow scroll bars of the panel and settings.
        app.Resources[SystemParameters.VerticalScrollBarWidthKey] = 7.0;
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try { await new Renderer(app, output, language, args.Length > 2 ? args[2].Split(',') : []).RunAsync(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();
        return exit;
    }

    internal static object? Field(object target, string name) => target.GetType().GetField(name, Any)!.GetValue(target);
    internal static void SetField(object target, string name, object? value) => target.GetType().GetField(name, Any)!.SetValue(target, value);
    internal static object? Call(object target, string name, Type[] types, params object?[] args)
        => (target.GetType().GetMethod(name, Any, types) ?? throw new MissingMethodException(target.GetType().Name, name)).Invoke(target, args);
    internal static void SetProperty(object target, string name, object? value) => target.GetType().GetProperty(name, Any)!.SetValue(target, value);
}

internal sealed class StartupSkipped : Exception;

internal sealed class Offline : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new HttpRequestException("The README renderer works offline.");
}

internal sealed class NoDesktop : IDesktopSources
{
    public string? ReadCursorToken(string database) => null;
    public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
}

internal sealed record Frame(BitmapSource Image, double Time);

internal sealed class Renderer(App app, string output, string language, string[] scenes)
{
    private bool Wants(string scene) => scenes.Length == 0 || scenes.Contains(scene);
    private MainWindow _deck = null!;
    private object _shelf = null!;
    private FrameworkElement _stage = null!;
    private DeckSettings _settings = null!;
    private readonly DateTimeOffset _now = DateTimeOffset.Now;
    private BitmapSource _cover = null!;
    private MediaTrack _track = null!, _other = null!;
    private readonly List<Frame> _frames = [];
    // Called with the clip's time before each frame of a recording is taken.
    private Action<double>? _onFrame;
    private readonly Dictionary<string, object> _marks = [];
    private double _clock;
    private bool _english;

    public async Task RunAsync()
    {
        Loc.Use(language); _english = Loc.IsEnglish;
        var folder = Path.Combine(Path.GetTempPath(), "BrimDeck.ReadmeMedia." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var offline = new HttpClient(new Offline());
            _settings = BuildSettings();
            Program.SetProperty(app, "Settings", _settings);
            Program.SetProperty(app, "Store", new SettingsStore(folder));
            Program.SetProperty(app, "Prices", new Pricing(folder, offline));
            Program.SetProperty(app, "Secrets", new ProviderSecrets(folder));
            Program.SetProperty(app, "Usage", new UsageService(new NoDesktop(), DataLocations.Resolve(folder, folder, folder, folder, folder, folder), offline));
            // The panel subscribes to the lyrics as it is made; this one finds none offline and reads no Apple Music cache.
            Program.SetProperty(app, "Lyrics", new LyricsService(offline, () => [], Path.Combine(folder, "apple-music-lyrics.json")));
            // The toolbar reads the update notice on every render.
            var backend = Type.GetType("BrimDeck.Updates.GitHubUpdates, BrimDeck")!.GetMethod("Create", [typeof(string)])!.Invoke(null, [folder]);
            Program.SetProperty(app, "Updates", new AppUpdates((IAppUpdateBackend)backend!, folder));
            // The clipboard and file transfer: the service is made on an empty folder of this run and never told to record.
            _shelf = Activator.CreateInstance(Type.GetType("BrimDeck.ShelfService, BrimDeck")!, Program.Any, null, [folder, Dispatcher.CurrentDispatcher], null)!;
            Program.SetProperty(app, "Shelf", _shelf);
            FillShelf(folder);
            // The devices: the object the panels read, made without its constructor, so none of its sources exists. Disposed, it
            // ignores every demand; its roster holds the invented readings.
            var devices = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(Type.GetType("BrimDeck.Native.DeviceBatteries, BrimDeck")!);
            var roster = new DeviceRoster();
            foreach (var group in InventedDevices().GroupBy(device => device.Source)) roster.Set(group.Key, [.. group]);
            Program.SetField(devices, "_roster", roster);
            Program.SetField(devices, "_disposed", true);
            Program.SetProperty(devices, "Settled", true);
            Program.SetProperty(app, "Devices", devices);

            _deck = new MainWindow(app) { Opacity = 0, IsHitTestVisible = false };
            Program.SetProperty(app, "Deck", _deck);
            // The window's own Loaded handler starts media detection, pointer tracking and the first read of real data;
            // none of that may run here. Loaded may also never come while the displays are off, so nothing waits for it.
            var store = typeof(UIElement).GetProperty("EventHandlersStore", Program.Any)!.GetValue(_deck);
            var handlers = (RoutedEventHandlerInfo[]?)store?.GetType().GetMethod("GetRoutedEventHandlers", Program.Any)!.Invoke(store, [FrameworkElement.LoadedEvent]);
            foreach (var handler in handlers ?? []) _deck.RemoveHandler(FrameworkElement.LoadedEvent, handler.Handler);
            // Foreground-window changes would re-apply the settings in the middle of a capture.
            Program.SetField(Program.Field(_deck, "_host")!, "ContextChanged", null);
            // Keep the transparent window off screen; ApplySettings centres it on the primary screen each time.
            _deck.LocationChanged += (_, _) => { if (_deck.Left > -10000) _deck.Left = -20000; };
            // The one-minute read would overwrite the invented state. The timer itself still runs for the countdown ring.
            Program.SetField(Program.Field(_deck, "_refresh")!, "Tick", null);
            _deck.ApplySettings();
            await Settle();
            _stage = (FrameworkElement)_deck.Content;

            _cover = Cover();
            (_track, _other) = Tracks();
            _deck.SetSnapshots(Snapshots());
            Program.Call(_deck, "ApplyMediaUpdate", [typeof(MediaTrack), typeof(IReadOnlyList<MediaTrack>), typeof(BitmapSource), typeof(bool)], _track, new List<MediaTrack> { _track, _other }, _cover, false);
            Program.SetField(_deck, "_trackNoticeUntil", default(DateTimeOffset));
            _deck.ApplySettings();
            await Settle();

            if (Wants("stills")) await Stills();
            if (Wants("themes")) await Themes();
            if (Wants("monitor")) await Monitor();
            if (Wants("devices")) await DevicesScene();
            if (Wants("shelf")) await ShelfScene();
            if (Wants("hero")) await Hero();
            // The "配额与统计" page: one row per application with its sources, name and colours.
            // Not used by the README at present; rendered only when asked for by name.
            if (scenes.Contains("settings")) await SettingsPage("settings", 3);
            // The music page at its presets and at the ends of its size range; for checking layout changes, not for the README.
            if (scenes.Contains("music-sizes")) await MusicSizes();
            // The clipboard page side by side and stacked, at three sizes, and the stacked cards trading places; for checking, not for the README.
            if (scenes.Contains("shelf-settings-stacked")) { _settings.ShelfArrangement = ShelfArrangement.Stacked; await SettingsPage("shelf-layout-settings-stacked", 17); }
            if (scenes.Contains("shelf-layout")) { await ShelfLayouts(); await SettingsPage("shelf-layout-settings", 17); }
            // Every way the two columns show their entries, with their states; for checking, not for the README.
            if (scenes.Contains("shelf-views")) await ShelfViews();
            // What the pictures do not show, measured: see ShelfMeasures.
            if (scenes.Contains("shelf-measures")) await ShelfMeasures();
        }
        finally { Directory.Delete(folder, true); }
    }

    private DeckSettings BuildSettings()
    {
        var settings = new DeckSettings
        {
            Language = language, Style = CompactStyle.Notch, UsagePage = true, MusicPage = true, Width = 760, Height = 240,
            MusicWidth = 440, MusicHeight = 200, OpenDelay = 0, CloseDelay = 3_600_000, AnimationDuration = 360, Animations = true,
            Maximized = WindowBehavior.Normal, Borderless = WindowBehavior.Normal, Exclusive = WindowBehavior.Normal,
            MusicTrackNotice = false, QuotaAlerts = true, UsageAutoSync = true, MusicText = CompactMusicText.None,
            // The file transfer's count joins the collapsed row only in the image that is about it (DevicesScene).
            NotchTransfer = false, CapsuleTransfer = false,
            // Off by default; the monitor page's image shows them.
            MonitorTemperatures = true,
            Apps =
            [
                new AppEntry { QuotaSource = ProviderId.Claude, UsageSource = ProviderId.Claude },
                new AppEntry { QuotaSource = ProviderId.Codex, UsageSource = ProviderId.Codex },
                new AppEntry { QuotaSource = ProviderId.Cursor, UsageSource = ProviderId.Cursor },
                new AppEntry { QuotaSource = ProviderId.Antigravity, UsageSource = ProviderId.Antigravity },
            ]
        };
        return settings;
    }

    // ---------- Invented data ----------

    // Five models in all, so the model details list fits the panel without a scroll bar.
    private List<ProviderSnapshot> Snapshots()
    {
        DateTimeOffset In(double days, double hours, double minutes) => _now.AddDays(days).AddHours(hours).AddMinutes(minutes).AddSeconds(30);
        ProviderSnapshot Snapshot(ProviderId id, string plan, params Quota[] quotas) => new(id)
        {
            Plan = plan, Quotas = quotas.ToList(), LiveQuota = true, QuotaTime = _now, UsageAvailable = true,
            StatusLabel = Loc.T("已连接", "Connected"), Status = Loc.T("已连接", "Connected")
        };
        var claude = Snapshot(ProviderId.Claude, "max",
            new Quota("5 小时额度", 34, In(0, 2, 13), 300), new Quota("每周额度", 58, In(3, 5, 0), 10080));
        claude.Entries = Entries(1, ["claude-opus-5-5", "claude-sonnet-5"], [.65, .35], 26_400_000, 17.84m);
        var codex = Snapshot(ProviderId.Codex, "plus",
            new Quota("5 小时额度", 22, In(0, 3, 41), 300), new Quota("每周额度", 41, In(4, 2, 0), 10080));
        codex.Entries = Entries(2, ["gpt-5.5-codex"], [1], 11_200_000, 6.37m);
        var cursor = Snapshot(ProviderId.Cursor, "pro",
            new Quota("Cursor 模型", 76, In(12, 6, 0)), new Quota("其他模型", 18, In(12, 6, 0)));
        cursor.Entries = Entries(3, ["auto"], [1], 3_900_000, 2.41m);
        var antigravity = Snapshot(ProviderId.Antigravity, "Google AI Pro",
            new Quota("Gemini · 5 小时", 12, In(0, 4, 5), 300), new Quota("Gemini · 每周", 29, In(5, 1, 0), 10080),
            new Quota("Claude · 5 小时", 47, In(0, 1, 26), 300), new Quota("Claude · 每周", 63, In(5, 1, 0), 10080));
        antigravity.Entries = Entries(4, ["gemini-3-pro"], [1], 5_600_000, 3.12m);
        return [claude, codex, cursor, antigravity];
    }

    // Today's records add up to the given totals; the six days before hold between 3.5 and 6.5 times as much in all.
    private List<TokenEntry> Entries(int seed, string[] models, double[] shares, long todayTokens, decimal todayCost)
    {
        var random = new Random(seed);
        var result = new List<TokenEntry>();
        var today = DateTime.Today;
        double todayHours = Math.Max(.5, (DateTime.Now - today).TotalHours - .1);
        for (int day = 0; day < 7; day++)
        {
            double factor = day == 0 ? 1 : .6 + random.NextDouble() * .5;
            long dayTokens = (long)(todayTokens * factor);
            decimal dayCost = Math.Round(todayCost * (decimal)factor, 2);
            for (int m = 0; m < models.Length; m++)
            {
                const int pieces = 6;
                for (int i = 0; i < pieces; i++)
                {
                    long total = (long)(dayTokens * shares[m] / pieces);
                    long output = total / 40, input = total / 12, cacheWrite = total / 20, cacheRead = total - output - input - cacheWrite;
                    var time = day == 0 ? today.AddHours(todayHours * (i + .5) / pieces) : today.AddDays(-day).AddHours(9 + i * 2 + random.NextDouble());
                    result.Add(new TokenEntry($"{seed}:{day}:{m}:{i}", new DateTimeOffset(time), models[m], input, cacheRead, cacheWrite, 0, output,
                        Math.Round(dayCost * (decimal)shares[m] / pieces, 4)));
                }
            }
        }
        return result;
    }

    private (MediaTrack Playing, MediaTrack Paused) Tracks()
    {
        var utc = DateTimeOffset.UtcNow;
        string lyrics = _english ? EnglishLyrics : ChineseLyrics;
        var playing = new MediaTrack
        {
            Id = "demo:netease", Source = "网易云音乐", Title = Loc.T("晚风邮局", "Evening Post"), Artist = Loc.T("纸灯乐队", "Paper Lanterns"),
            Album = Loc.T("海边来信", "Letters from the Shore"), EmbeddedLyrics = lyrics, State = MediaState.Playing,
            CanToggle = true, CanPrevious = true, CanNext = true, CanSeek = true, CanShuffle = true, CanRepeat = true, Repeat = MediaRepeat.List, CanLike = true, Liked = true,
            Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(232), MaxSeek = TimeSpan.FromSeconds(232),
            ReportedPosition = TimeSpan.FromSeconds(83), PositionAt = utc, Rate = 1
        };
        var paused = new MediaTrack
        {
            Id = "demo:qq", Source = "QQ 音乐", Title = Loc.T("慢车", "Slow Train"), Artist = Loc.T("白日梦", "Daydreamers"),
            State = MediaState.Paused, CanToggle = true, CanPrevious = true, CanNext = true, CanSeek = true,
            Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(201), ReportedPosition = TimeSpan.FromSeconds(40), PositionAt = utc
        };
        return (playing, paused);
    }

    private const string ChineseLyrics = """
        [00:00.00]晚风邮局 - 纸灯乐队
        [00:18.00]黄昏把街道慢慢折起
        [00:26.00]路灯一盏一盏替我数着
        [00:34.00]信封里装着整个夏天
        [00:42.00]还有没说完的那句话
        [00:52.00]把今天的晚风寄给你
        [01:00.00]寄到你窗前的那棵树
        [01:08.00]如果它轻轻敲你的窗
        [01:16.00]那是我在说晚安
        [01:24.00]海浪替我盖上邮戳
        [01:32.00]星星在信纸上排成一行
        [01:40.00]不必急着拆开回信
        [01:48.00]等潮水退了再读
        [01:58.00]把今天的晚风寄给你
        [02:06.00]寄到很远很远的地方
        [02:14.00]如果你刚好也在想念
        [02:22.00]就把灯留给我
        """;

    private const string EnglishLyrics = """
        [00:00.00]Evening Post - Paper Lanterns
        [00:18.00]Dusk folds the street up slowly
        [00:26.00]The lamps count the hours for me
        [00:34.00]A whole summer in one envelope
        [00:42.00]And the words I never said
        [00:52.00]I'm mailing you tonight's breeze
        [01:00.00]To the tree outside your window
        [01:08.00]If it taps upon the glass
        [01:16.00]That's me saying goodnight
        [01:24.00]The waves press on the postmark
        [01:32.00]Stars line up across the page
        [01:40.00]No need to write back yet
        [01:48.00]Read it when the tide goes out
        [01:58.00]I'm mailing you tonight's breeze
        [02:06.00]To somewhere far away
        [02:14.00]If you're missing me as well
        [02:22.00]Leave a light on for me
        """;

    // Wireless devices that do not exist: a mouse, a keyboard getting low, earbuds with their case, a controller charging and a headset.
    private static List<DeviceBattery> InventedDevices() =>
    [
        new("demo:mouse", "MX Master 3S", DeviceKind.Mouse, DeviceSource.Logitech, 82),
        new("demo:keyboard", "ATK Z87 Pro", DeviceKind.Keyboard, DeviceSource.Hid, 23),
        new("bt:A1B2C3D4E5F6", "AirPods Pro", DeviceKind.Earbuds, DeviceSource.Apple, 71)
        {
            Parts = [new PartBattery(BatteryPart.Left, 74, false), new PartBattery(BatteryPart.Right, 71, false), new PartBattery(BatteryPart.Case, 55, false)]
        },
        new("demo:controller", Loc.T("Xbox 无线控制器", "Xbox Wireless Controller"), DeviceKind.Controller, DeviceSource.Gaming, 64) { Charging = true },
        new("demo:headset", "Arctis Nova 7", DeviceKind.Headphones, DeviceSource.SteelSeries, 9)
    ];

    // The clipboard's entries and the file transfer's files. The files are empty files of this run, made with the sizes shown;
    // their icons are the ones this computer shows for their types, as the monitor page's process icons are.
    private void FillShelf(string folder)
    {
        var data = (ShelfData)_shelf.GetType().GetProperty("Data", Program.Any)!.GetValue(_shelf)!;
        string[] colors = ["#9CB9FF", "#98DBB0", "#E5A385", "#B9A8FF"];
        var work = Shelf.AddCategory(data, ShelfSide.Clipboard, Loc.T("工作", "Work"), colors);
        var code = Shelf.AddCategory(data, ShelfSide.Clipboard, Loc.T("代码", "Code"), colors);
        work.Color = colors[1]; code.Color = colors[3];
        ClipEntry Clip(ClipKind kind, string text, double minutes, string source, params Guid[] categories) => new()
        {
            Kind = kind, Text = text, Copied = _now.AddMinutes(-minutes), Source = source, Categories = [.. categories]
        };
        var picture = Clip(ClipKind.Image, "", 9, "mspaint");
        picture.PixelWidth = 1280; picture.PixelHeight = 720;
        Program.Call(_shelf, "WritePicture", [typeof(Guid), typeof(byte[])], picture.Id, Png(Cover(1280, 720)));
        var phrase = Clip(ClipKind.Text, Loc.T("您好，邮件已收到，我会在今天内回复。", "Thanks, I have your message and will reply today."), 2880, "");
        phrase.Manual = true; phrase.Categories.Add(ShelfData.Favorites);
        data.Clips.AddRange(
        [
            Clip(ClipKind.Text, Loc.T("周三上午十点评审 1.3.0 的更新说明", "Review the 1.3.0 release notes on Wednesday at 10"), 1, "notepad", work.Id),
            Clip(ClipKind.Link, "https://github.com/MoFeng2223/BrimDeck/releases", 4, "chrome"),
            Clip(ClipKind.Code, "var total = entries.Sum(entry => entry.Tokens);\nvar average = total / 7;\nreturn average;", 6, "Code", code.Id),
            picture,
            Clip(ClipKind.Color, "#9CB9FF", 15, "Code"),
            Clip(ClipKind.Text, Loc.T("会议室 3，带上打印好的排期表\n需要投影仪", "Room 3, bring the printed schedule\nA projector is needed"), 32, "notepad", work.Id),
            Clip(ClipKind.Code, "git fetch origin && git rebase origin/dev", 47, "WindowsTerminal", code.Id),
            phrase
        ]);

        var send = Shelf.AddCategory(data, ShelfSide.Files, Loc.T("待发送", "To send"), colors);
        var setup = Shelf.AddCategory(data, ShelfSide.Files, Loc.T("安装包", "Installers"), colors);
        send.Color = colors[2]; setup.Color = colors[0];
        var files = Path.Combine(folder, "files");
        Directory.CreateDirectory(files);
        const long KB = 1024, MB = 1024 * 1024;
        (string Name, long Size, Guid? Category)[] invented =
        [
            (Loc.T("发布说明 1.3.0.docx", "Release notes 1.3.0.docx"), 86 * KB, send.Id),
            (Loc.T("界面截图.png", "Screenshot.png"), (long)(1.4 * MB), send.Id),
            ("BrimDeck-1.3.0-Portable-x64.zip", 58 * MB, setup.Id),
            (Loc.T("季度数据.xlsx", "Quarterly figures.xlsx"), 312 * KB, null),
            (Loc.T("演示录屏.mp4", "Demo recording.mp4"), 24 * MB, null),
            (Loc.T("会议纪要.txt", "Meeting notes.txt"), 4 * KB, null)
        ];
        for (int i = 0; i < invented.Length; i++)
        {
            var (name, size, category) = invented[i];
            var path = Path.Combine(files, name);
            using (var stream = File.Create(path)) stream.SetLength(size);
            data.Files.Add(new TransferFile { Path = path, Added = _now.AddMinutes(-3 - i * 11), Categories = category is { } id ? [id] : [] });
        }
    }

    private static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // A made-up album cover: an evening sky over the sea.
    private static BitmapSource Cover() => Cover(512, 512);

    // The same drawing at another size, for the picture in the clipboard.
    private static BitmapSource Cover(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(width / 512.0, height / 512.0));
            dc.DrawImage(Square(), new Rect(0, 0, 512, 512));
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Square()
    {
        const int size = 512;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var sky = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(0x2A, 0x2D, 0x52), 0), new(Color.FromRgb(0x7C, 0x4E, 0x78), .45), new(Color.FromRgb(0xE8, 0x8F, 0x6E), .72), new(Color.FromRgb(0xF4, 0xC0, 0x8A), .78)
            }, new Point(0, 0), new Point(0, 1));
            dc.DrawRectangle(sky, null, new Rect(0, 0, size, size));
            var glow = new RadialGradientBrush(Color.FromArgb(0x90, 0xFF, 0xD9, 0xA8), Color.FromArgb(0, 0xFF, 0xD9, 0xA8)) { RadiusX = .5, RadiusY = .5 };
            dc.DrawEllipse(glow, null, new Point(size * .5, size * .66), 190, 150);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xE2, 0xB6)), null, new Point(size * .5, size * .66), 70, 70);
            var sea = new LinearGradientBrush(Color.FromRgb(0x3A, 0x3F, 0x6E), Color.FromRgb(0x18, 0x1B, 0x33), 90);
            dc.DrawRectangle(sea, null, new Rect(0, size * .7, size, size * .3));
            var shine = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xD2, 0xA0));
            double[] widths = [110, 84, 60, 40, 26, 14];
            for (int i = 0; i < widths.Length; i++)
            {
                double y = size * .72 + i * 14;
                dc.DrawRoundedRectangle(shine, null, new Rect(size * .5 - widths[i] / 2, y, widths[i], 4), 2, 2);
            }
            var hill = new StreamGeometry();
            using (var g = hill.Open())
            {
                g.BeginFigure(new Point(0, size * .7), true, true);
                g.BezierTo(new Point(size * .08, size * .6), new Point(size * .2, size * .58), new Point(size * .3, size * .7), true, true);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x24, 0x22, 0x40)), null, hill);
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }

    // ---------- Scenes ----------

    private async Task Stills()
    {
        const double scale = 2;
        _deck.SetExpanded(true, true);
        await Settle(); SetSyncRing(.4);
        await Save("usage", scale);

        // Opening the details and each app added to them dissolve the page into its new content (MainWindow.RenderUsage); without
        // animations it is drawn at once, rather than captured half faded in.
        _settings.Animations = false;
        Program.Call(_deck, "OpenModelDetails", [typeof(ProviderId), typeof(int)], ProviderId.Claude, 7);
        foreach (var id in new[] { ProviderId.Codex, ProviderId.Cursor, ProviderId.Antigravity })
            Program.Call(_deck, "ToggleModelApp", [typeof(ProviderId)], id);
        await Settle();
        await Save("details", scale);
        Program.Call(_deck, "ShowModelDetails", [typeof(bool)], false);
        await Settle();
        _settings.Animations = true;

        SelectPage(DeckPage.Music);
        await Settle();
        await Save("music", scale, 1.3);
        SelectPage(DeckPage.Usage);

        _deck.SetExpanded(false, true);
        await Settle();
        await Save("compact-notch", scale, .7);

        _settings.MusicText = CompactMusicText.Lyrics; Refresh();
        await Settle();
        await Save("compact-notch-lyrics", scale, .9);
        _settings.MusicText = CompactMusicText.None;

        _settings.Style = CompactStyle.Capsule; Refresh();
        await Settle();
        await Save("compact-capsule", scale, 1.1);

        _settings.Style = CompactStyle.Line; Refresh();
        await Settle();
        await Save("compact-line", scale);

        _settings.Style = CompactStyle.Notch; Refresh();
        await QuotaAlert(scale);
        await ClockStyles();
    }

    // The quota notice replaces the collapsed row and scrolls through it when the row is too short, so it is shown on a notch
    // just wide enough for the whole notice plus a 12 DIP margin, with the same invented data. A notice that fits is the row of
    // its parts itself; one that scrolls is a clipping Canvas around that row (MainWindow.LayoutCompactAlert).
    private async Task QuotaAlert(double scale)
    {
        var claude = _settings.Apps[0];
        var alert = (System.Windows.Controls.Panel)_deck.FindName("CompactAlert")!;
        async Task Show(double width)
        {
            _settings.NotchWidth = width; Refresh();
            Program.Call(_deck, "ShowAlert", [typeof(AppEntry), typeof(string), typeof(double), typeof(DateTimeOffset?)],
                claude, Loc.T("5 小时", "5-hour"), 91.0, (DateTimeOffset?)_now.AddHours(2).AddMinutes(13).AddSeconds(30));
            await Settle();
        }
        // The notch otherwise takes the width of its usual content, whatever width is set.
        _settings.NotchAutoWidth = false;
        double fit = 0;
        for (double width = CollapsedLimits.NotchWidthDefault; width <= CollapsedLimits.NotchWidthDefault + 400 && fit == 0; width += 2)
        {
            await Show(width);
            if (alert.Children.Count == 1 && alert.Children[0] is System.Windows.Controls.StackPanel) fit = width;
        }
        if (fit == 0) throw new InvalidOperationException("No notch width shows the whole quota notice.");
        await Show(fit + 12);
        await Save("compact-alert", scale);
        Program.Call(_deck, "ClearAlert", []);
        _settings.NotchWidth = CollapsedLimits.NotchWidthDefault; _settings.NotchAutoWidth = true; Refresh();
        await Settle();
    }

    // Compact → usage page → music page → system monitor page → clipboard and file transfer → devices → compact, with the configured
    // animations. The panel's own animations run slower while they are captured; frame times are recorded on the normal time scale.
    private async Task Hero()
    {
        const double scale = 1.5;
        const int slow = 6;
        _frames.Clear(); _clock = 0;
        _settings.AnimationDuration = 360 * slow;
        _deck.SetExpanded(false, true);
        await Settle(); SetSyncRing(.3);

        await Record(1.2, 1, scale);
        _marks["expand"] = _clock;
        _deck.SetExpanded(true);
        await Record(.5, slow, scale);
        await Record(2.0, 1, scale, fps: 4);

        _marks["musicButton"] = Point(_deck.FindName("MusicPageButton"));
        _marks["switch"] = _clock;
        SelectPage(DeckPage.Music);
        await Record(.5, slow, scale);
        _marks["musicPanel"] = Bounds();
        await Record(2.2, 1, scale);

        // As in Monitor(): App.Exiting keeps the real sampler from starting. The page gets the invented reading as soon as it
        // is built and draws it without easing, as a page that opens on the latest reading does.
        Program.SetProperty(app, "Exiting", true);
        _settings.MonitorWidth = MonitorSizes.Default.Width; _settings.MonitorHeight = MonitorSizes.Default.Height;
        _marks["monitorButton"] = Point(_deck.FindName("MonitorPageButton"));
        _marks["monitorSwitch"] = _clock;
        SelectPage(DeckPage.Monitor);
        Program.Call(Program.Field(_deck, "_monitorView")!, "Update", [typeof(MonitorSnapshot), typeof(bool)], MonitorSample(), false);
        // A new reading every second of the clip, handed over as the sampler would, so the page eases to it and the list reorders.
        double opened = _clock; int fed = 0;
        _onFrame = time =>
        {
            int tick = (int)Math.Floor(time - opened);
            if (tick > fed) { fed = tick; Program.Call(_deck, "MonitorUpdated", [typeof(MonitorSnapshot)], MonitorSample(tick)); }
        };
        await Record(.5, slow, scale);
        _marks["monitorPanel"] = Bounds();
        await Record(2.4, 1, scale);
        _onFrame = null;

        // The clipboard and file transfer at its default size, then the devices at the usage page's size, where each device has
        // its ring; in the page switch's order.
        (_settings.ClipboardWidth, _settings.ClipboardHeight) = ShelfSizes.Default;
        _marks["shelfButton"] = Point(_deck.FindName("ShelfPageButton"));
        _marks["shelfSwitch"] = _clock;
        SelectPage(DeckPage.Clipboard);
        await Record(.5, slow, scale);
        _marks["shelfPanel"] = Bounds();
        await Record(2.4, 1, scale, fps: 4);

        _settings.DevicesWidth = 760; _settings.DevicesHeight = 240;
        _marks["devicesButton"] = Point(_deck.FindName("DevicesPageButton"));
        _marks["devicesSwitch"] = _clock;
        SelectPage(DeckPage.Devices);
        await Record(.5, slow, scale);
        _marks["devicesPanel"] = Bounds();
        await Record(2.0, 1, scale, fps: 4);

        _marks["collapse"] = _clock;
        _deck.SetExpanded(false);
        await Record(.6, slow, scale);
        await Record(1.0, 1, scale);
        _marks["end"] = _clock;
        SelectPage(DeckPage.Usage);
        Program.SetProperty(app, "Exiting", false);

        var folder = Path.Combine(output, "hero");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        var list = new List<object>();
        for (int i = 0; i < _frames.Count; i++)
        {
            var name = $"{i:0000}.png";
            Write(_frames[i].Image, Path.Combine(folder, name));
            list.Add(new { file = name, time = Math.Round(_frames[i].Time, 4) });
        }
        _marks["scale"] = scale;
        _marks["stage"] = new { width = _stage.ActualWidth, height = _stage.ActualHeight };
        _marks["usagePanel"] = new { width = _settings.Width, height = _settings.Height };
        File.WriteAllText(Path.Combine(folder, "frames.json"), JsonSerializer.Serialize(new { frames = list, marks = _marks }, new JsonSerializerOptions { WriteIndented = true }));
        _settings.AnimationDuration = 360;
    }

    // Collapsed notches showing the clock at a fixed invented date and time (2026-09-27 15:25): three styles in different
    // preset colors beside the quota rings, and one shown alone, centred. compose.py arranges them in a labelled grid as
    // compact-clock.png.
    private async Task ClockStyles()
    {
        const double scale = 2;
        var face = Type.GetType("BrimDeck.ClockFace, BrimDeck")!;
        var time = new DateTime(2026, 9, 27, 15, 25, 0);
        (ClockStyle Style, ClockColor Color, bool Alone)[] samples =
        [
            (ClockStyle.Minimal, ClockColor.White, false), (ClockStyle.Segment, ClockColor.Lime, false),
            (ClockStyle.Serif, ClockColor.Amber, false), (ClockStyle.Neon, ClockColor.Pink, true)
        ];
        _deck.SetExpanded(false, true);
        _settings.Style = CompactStyle.Notch; _settings.NotchClock = true; _settings.NotchMusic = false;
        var list = new List<object>();
        for (int i = 0; i < samples.Length; i++)
        {
            var (style, color, alone) = samples[i];
            // Alone means no quota rings and no music, so the clock is centred.
            _settings.ClockStyle = style; _settings.ClockColor = color; _settings.NotchSummary = !alone; Refresh();
            await Settle();
            if (Program.Field(_deck, "_compactClock") is { } clock) Program.SetProperty(clock, "Time", time);
            await Settle();
            var name = $"clock-{i}";
            Write(Capture(scale), Path.Combine(output, name + ".png"));
            string title = alone ? Loc.T("单独显示", "Shown alone") : (string)face.GetMethod("Title")!.Invoke(null, [style])!;
            list.Add(new { file = name + ".png", style = style.ToString(), alone, title });
        }
        File.WriteAllText(Path.Combine(output, "clocks.json"), JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("compact-clock");
        _settings.NotchClock = false; _settings.NotchMusic = true; _settings.NotchSummary = true;
        _settings.ClockStyle = default; _settings.ClockColor = ClockColor.White; Refresh();
        await Settle();
    }

    // The usage page in built-in panel themes that look clearly different, and in an invented custom theme. compose.py
    // arranges them in a labelled grid as themes.png. The theme folder is never read: PanelTheme is marked as loaded and its
    // custom themes are the one below, written to a folder of this run.
    private const string CustomTheme = """
        {
          "id": "warm-paper",
          "name": "暖纸",
          "nameEn": "Warm paper",
          "base": "light",
          "colors": { "surface": "#F3EBDC", "accent": "#B4532A", "clock": "#8A4B2A" }
        }
        """;

    private async Task Themes()
    {
        const double scale = 2;
        var folder = Path.Combine(Path.GetTempPath(), "BrimDeck.ReadmeThemes." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "warm-paper.json"), CustomTheme);
        string[] ids = [PanelThemes.Dark, PanelThemes.Light, PanelThemes.IslandHoliday2, PanelThemes.PixelBlocks, PanelThemes.Demo, "warm-paper"];
        // 海岛假日 2 has a day and a night look by the local time; the image shows the day one whenever it is made: the day is
        // set to begin an hour before now and to last twelve hours.
        var dawn = TimeSpan.FromHours((DateTime.Now.Hour + 23) % 24);
        _settings.ThemeDaylight = PanelThemes.FormatDaylight(dawn, TimeSpan.FromHours((dawn.Hours + 12) % 24));
        var theme = Type.GetType("BrimDeck.PanelTheme, BrimDeck")!;
        theme.GetField("_loaded", Program.Any)!.SetValue(null, true);
        theme.GetProperty("UserThemes", Program.Any)!.SetValue(null, PanelThemes.LoadFolder(folder));
        var use = theme.GetMethod("Use", Program.Any)!;
        _settings.Width = 620; _settings.Height = 200;
        // A change of theme dissolves from the old look into the new one (MainWindow.ThemeFade); without animations it is drawn at once.
        _settings.Animations = false;
        SelectPage(DeckPage.Usage);
        _deck.SetExpanded(true, true);
        var list = new List<object>();
        for (int i = 0; i < ids.Length; i++)
        {
            _settings.PanelTheme = ids[i];
            use.Invoke(null, [_settings, false, false]); Refresh();
            await Settle(); SetSyncRing(.4);
            var name = $"theme-{i}";
            await Save(name, scale);
            string title = PanelThemes.BuiltIn(ids[i])?.DisplayName ?? Loc.T("自定义主题示例：暖纸", "Custom theme example: Warm paper");
            list.Add(new { file = name + ".png", title });
        }
        File.WriteAllText(Path.Combine(output, "themes.json"), JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        Directory.Delete(folder, true);
        _settings.PanelTheme = PanelThemes.Dark; _settings.ThemeDaylight = "";
        use.Invoke(null, [_settings, false, false]);
        _settings.Width = 760; _settings.Height = 240; _settings.Animations = true;
        _deck.SetExpanded(false, true); Refresh();
        await Settle();
    }

    // The system monitor page and the readings in the notch, from one invented reading. App.Exiting keeps PresentMonitor from
    // starting the real sampler; the reading is handed to the panel as the sampler would.
    private async Task Monitor()
    {
        const double scale = 2;
        Program.SetProperty(app, "Exiting", true);
        // The page eases new readings in; without animations the first reading is drawn as it is.
        _settings.Animations = false;
        var snapshot = MonitorSample();
        void Feed() => Program.Call(_deck, "MonitorUpdated", [typeof(MonitorSnapshot)], snapshot);

        // The usage page's size, so the two images show at the same scale in the README.
        _settings.MonitorWidth = 760; _settings.MonitorHeight = 240;
        SelectPage(DeckPage.Monitor);
        _deck.SetExpanded(true, true);
        await Settle(); Feed();
        await Save("monitor", scale);
        SelectPage(DeckPage.Usage);

        _settings.NotchMonitor = true; _settings.NotchMusic = false;
        _deck.SetExpanded(false, true); Refresh();
        await Settle(); Feed();
        await Save("compact-monitor", scale);
        _settings.NotchMonitor = false; _settings.NotchMusic = true; Refresh();
        Program.SetProperty(app, "Exiting", false);
        _settings.Animations = true;
        await Settle();
    }

    // The devices page, and the notch with the devices' rings and the file transfer's count. App.Exiting keeps the panel's demand
    // from reaching the devices object.
    private async Task DevicesScene()
    {
        const double scale = 2;
        Program.SetProperty(app, "Exiting", true);
        // The page switch slides its highlight to the new page; without animations it is drawn there at once.
        _settings.Animations = false;
        // The usage page's size, so the images show at the same scale in the README.
        _settings.DevicesWidth = 760; _settings.DevicesHeight = 240;
        SelectPage(DeckPage.Devices);
        _deck.SetExpanded(true, true);
        await Settle();
        await Save("devices", scale);
        SelectPage(DeckPage.Usage);

        // The equalizer at the end of the row, after the file count and the devices.
        var order = _settings.NotchOrder;
        _settings.NotchOrder = [.. order.Where(part => part != CompactPart.Equalizer), CompactPart.Equalizer];
        _settings.NotchDevices = true; _settings.NotchTransfer = true; _settings.NotchMusic = false;
        _deck.SetExpanded(false, true); Refresh();
        await Settle();
        await Save("compact-devices", scale);
        _settings.NotchOrder = order;
        _settings.NotchDevices = false; _settings.NotchTransfer = false; _settings.NotchMusic = true; Refresh();
        Program.SetProperty(app, "Exiting", false);
        _settings.Animations = true;
        await Settle();
    }

    // The clipboard and file transfer page, at the usage page's size.
    private async Task ShelfScene()
    {
        const double scale = 2;
        _settings.ClipboardWidth = 760; _settings.ClipboardHeight = 240;
        // As in DevicesScene: the page switch's highlight is drawn on the new page at once.
        _settings.Animations = false;
        SelectPage(DeckPage.Clipboard);
        _deck.SetExpanded(true, true);
        await Settle();
        await Save("clipboard", scale);
        SelectPage(DeckPage.Usage);
        _deck.SetExpanded(false, true);
        _settings.Animations = true;
        await Settle();
    }

    // A 14-core processor (6 performance cores with two threads each, 8 efficiency cores), 32 GB of memory and a graphics card,
    // read once a second: tick counts the seconds, and tick 0 is the reading of the still image. Every figure moves around its
    // value at tick 0 from one second to the next, and the minute of history is the same figures for the 59 seconds before, so
    // the lines scroll on as the readings come in.
    private static MonitorSnapshot MonitorSample(int tick = 0)
    {
        // A slow wander between -2 and 2 and a jump between -1 and 1 that changes every second; both are 0 at tick 0.
        static double Drift(double t, int k)
        {
            static double Wave(double t, int k) => Math.Sin(t * (.4 + k % 7 * .09) + k * 1.7) * .6 + Math.Sin(t * (1.1 + k % 5 * .13) + k * .9) * .4;
            return Wave(t, k) - Wave(0, k);
        }
        static double Jump(int t, int k) => t == 0 ? 0 : new Random(t * 7919 + k * 104729).NextDouble() * 2 - 1;
        double[] performance = [.62, .48, .71, .35, .54, .27, .44, .19, .58, .31, .23, .12];
        double[] efficiency = [.22, .09, .15, .31, .06, .12, .18, .04];
        int count = performance.Length + efficiency.Length;
        double Core(int i, int t) => Math.Clamp((i < performance.Length ? performance[i] : efficiency[i - performance.Length]) + .05 * Drift(t, i) + .1 * Jump(t, i) + .06 * Drift(t, 98) + .1 * Jump(t, 99), .02, 1);
        double Cpu(int t) => Enumerable.Range(0, count).Average(i => Core(i, t));
        double Gpu(int t) => Math.Clamp(.36 + .05 * Drift(t, 40) + .08 * Jump(t, 40), .02, 1);
        double Down(int t) => Math.Max(60_000, 3_400_000 * (1 + .2 * Drift(t, 50) + .35 * Jump(t, 50)));
        double Up(int t) => Math.Max(20_000, 420_000 * (1 + .2 * Drift(t, 51) + .35 * Jump(t, 51)));

        var cores = new List<CoreSample>();
        for (int i = 0; i < count; i++)
        {
            bool fast = i < performance.Length;
            double usage = Core(i, tick);
            cores.Add(new CoreSample(i, fast ? 0 : 1, fast ? CoreTier.Performance : CoreTier.Efficiency, fast ? i + 1 : i - performance.Length + 1, usage,
                fast ? 3600 + usage * 1700 : 2800 + usage * 1500, fast ? 5400 : 4200));
        }
        var cpu = new CpuSample(cores, 4300) { Temperature = Math.Round(62 + 1.5 * Drift(tick, 70)) };
        const ulong GB = 1UL << 30, MB = 1UL << 20;
        var memory = new MemorySample(32 * GB, (ulong)((18.4 + .1 * Jump(tick, 60)) * GB), (ulong)(24.6 * GB), (ulong)(37.8 * GB), (ulong)(8.9 * GB));
        var network = new NetworkSample(Down(tick), Up(tick));
        double gpuUsage = Gpu(tick);
        var gpu = new GpuSample(gpuUsage, (ulong)(5.2 * GB))
        {
            Name = "NVIDIA GeForce RTX 4070", DedicatedTotal = 12 * GB, SharedBytes = (ulong)(0.4 * GB), Temperature = Math.Round(57 + 1.5 * Drift(tick, 71)),
            Engines = [new("3D", gpuUsage), new("Copy", Math.Max(0, .07 + .04 * Jump(tick, 41))), new("VideoDecode", Math.Max(0, .21 + .08 * Jump(tick, 42))),
                new("VideoEncode", 0), new("Compute", Math.Max(0, .03 + .02 * Jump(tick, 43)))]
        };
        // The icons are the only thing taken from this computer: each app's icon is read from its executable where it is
        // installed, as BrimDeck does. An app that is not installed shows its initial instead.
        var prepare = Type.GetType("BrimDeck.ProcessIcons, BrimDeck")!.GetMethod("Prepare", Program.Any)!;
        // Each app's processor use over five seconds, repeated. The figures change every second, and once in the clip (at tick 2)
        // Chrome overtakes Visual Studio Code, so the list reorders once. Tick 0 is the still image.
        ProcessSample App(int pid, string name, string exe, double[] cpuPercents, double memoryMb, int members = 1, params string[] places)
        {
            var path = Installed(exe, places);
            if (path is not null) prepare.Invoke(null, [path]);
            double cpuPercent = cpuPercents[tick % cpuPercents.Length];
            return new(pid, name, cpuPercent, (ulong)(memoryMb * MB), name.ToLowerInvariant(), path) { Members = Folded(name, cpuPercent, memoryMb, members) };
        }
        IReadOnlyList<ProcessMember> Folded(string name, double cpuPercent, double memoryMb, int members) =>
            members > 1 ? [.. Enumerable.Range(0, members).Select(i => new ProcessMember(name, cpuPercent / members, (ulong)(memoryMb / members * MB)))] : [];
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows), system = Environment.SystemDirectory;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        List<ProcessSample> apps =
        [
            App(4120, "Visual Studio Code", "Code.exe", [182, 170, 120, 115, 118], 1460, 9, Path.Combine(local, @"Programs\Microsoft VS Code\Code.exe")),
            App(6388, "Google Chrome", "chrome.exe", [126, 135, 168, 172, 165], 2380, 14),
            App(9012, "Cursor", "Cursor.exe", [74, 78, 80, 76, 79], 890, 6, Path.Combine(local, @"Programs\cursor\Cursor.exe")),
            App(1876, Loc.T("桌面窗口管理器", "Desktop Window Manager"), "dwm.exe", [41, 44, 39, 42, 40], 210, 1, Path.Combine(system, "dwm.exe")),
            App(7740, Loc.T("Windows 资源管理器", "Windows Explorer"), "explorer.exe", [18, 16, 19, 17, 18], 330, 1, Path.Combine(windows, "explorer.exe")),
            App(5320, Loc.T("网易云音乐", "NetEase Cloud Music"), "cloudmusic.exe", [12, 13, 12, 14, 13], 410, 4),
            App(3304, "Windows PowerShell", "powershell.exe", [9, 8, 9, 8, 9], 160, 2, Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe")),
            App(2210, "Microsoft Edge", "msedge.exe", [6, 7, 6, 7, 6], 720, 5)
        ];
        var processes = new ProcessList(ProcessSample.Folded(apps, ProcessSort.Cpu), ProcessSample.Folded(apps, ProcessSort.Memory));
        double[] History(Func<int, double> reading) => [.. Enumerable.Range(tick - 59, 60).Select(reading)];
        // Two solid-state drives, the first split into two letters; a laptop on mains, charging; and the power the processor and
        // the graphics card report, which the page calls an estimate.
        StorageDrive Drive(int number, string model, double celsius, params (string Letter, string Label, double Total, double Free)[] volumes) =>
            new(number, model, [.. volumes.Select(volume => new StorageVolume(volume.Letter, volume.Label, (ulong)(volume.Total * GB), (ulong)(volume.Free * GB)))])
            { SolidState = true, Temperature = new DiskTemperature(model, celsius) };
        double CpuWatts(int t) => 18 + 42 * Cpu(t);
        double GpuWatts(int t) => 14 + 96 * Gpu(t);
        double Watts(int t) => CpuWatts(t) + GpuWatts(t);
        var power = History(Watts);
        return new MonitorSnapshot(DateTimeOffset.Now, count, cpu, memory, network, gpu, processes)
        {
            CpuHistory = History(Cpu), GpuHistory = History(Gpu), DownHistory = History(Down), UpHistory = History(Up),
            Storage =
            [
                Drive(0, "Samsung SSD 990 PRO 1TB", 41, ("C:", Loc.T("系统", "System"), 256, 88), ("D:", Loc.T("数据", "Data"), 675, 312)),
                Drive(1, "WD_BLACK SN850X 2TB", 37, ("E:", Loc.T("素材", "Media"), 1863, 1124))
            ],
            Power = new PowerSample(Watts(tick), PowerOrigin.Estimated, PowerSource.Components,
                [new PowerPart("cpu", "CPU", CpuWatts(tick), "RAPL"), new PowerPart("gpu", "NVIDIA GeForce RTX 4070", GpuWatts(tick), "nvidia-smi")]),
            PowerHistory = power, PowerSummary = new PowerSummary(power.Max(), power.Average(), power.Length, power.Length),
            Battery = new BatterySample(82, BatteryState.Charging, 1)
            {
                Watts = 46.5, HasRate = true, TimeToFull = TimeSpan.FromMinutes(38), RemainingWh = 61.5, FullWh = 75, DesignWh = 80, Health = 75.0 / 80,
                AcOnline = true, Mode = PowerMode.Balanced
            }
        };
    }

    // Where an executable is installed: the places given, then the App Paths that installers register.
    private static string? Installed(string exe, string[] places)
    {
        foreach (var place in places) if (File.Exists(place)) return place;
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
            if (key?.GetValue(null) is string path && File.Exists(path.Trim('"'))) return path.Trim('"');
        }
        return null;
    }

    private async Task MusicSizes()
    {
        (double Width, double Height)[] sizes = [(440, 140), (440, 170), (440, 200), (520, 200), (540, 250), (600, 240), (760, 300), (900, 350), (1200, 400), (440, 400), (1200, 140)];
        SelectPage(DeckPage.Music);
        _deck.SetExpanded(true, true);
        foreach (var (width, height) in sizes)
        {
            _settings.MusicWidth = width; _settings.MusicHeight = height; Refresh();
            await Save($"music-{width}x{height}", 1.5);
        }
        // NetEase read from its log, with and without SMTC, and a song without lyrics or a timeline.
        var log = _track with { Id = "demo:log", IsNeteaseLog = true, CanShuffle = false, CanRepeat = false, CanSeek = false, CanLike = false, Liked = false };
        var variants = new (string Name, MediaTrack Track)[]
        {
            ("entry", log), ("hint", log with { CanToggle = false, CanPrevious = false, CanNext = false }),
            ("plain", _track with { Id = "demo:plain", EmbeddedLyrics = "", PositionKnown = false })
        };
        foreach (var (name, track) in variants)
        {
            Program.Call(_deck, "ApplyMediaUpdate", [typeof(MediaTrack), typeof(IReadOnlyList<MediaTrack>), typeof(BitmapSource), typeof(bool)], track, new List<MediaTrack> { track }, _cover, false);
            foreach (var (width, height) in new[] { (440d, 140d), (760d, 300d) })
            {
                _settings.MusicWidth = width; _settings.MusicHeight = height; Refresh();
                await Save($"music-{name}-{width}x{height}", 1.5);
            }
        }
        Program.Call(_deck, "ApplyMediaUpdate", [typeof(MediaTrack), typeof(IReadOnlyList<MediaTrack>), typeof(BitmapSource), typeof(bool)], _track, new List<MediaTrack> { _track, _other }, _cover, false);
        _settings.MusicWidth = 440; _settings.MusicHeight = 200; Refresh();
        SelectPage(DeckPage.Usage);
    }

    // The settings window is built without being shown: its content is taken out, laid out at the default size and
    // captured with the window's resources.
    private async Task ShelfLayouts()
    {
        const double scale = 2;
        void Build() => Program.Call(_deck, "RenderShelf", [typeof(bool)], false);
        void Front(ShelfSide side) => Program.SetField(_deck, "_shelfFront", side);
        _settings.Animations = false;
        SelectPage(DeckPage.Clipboard);
        _deck.SetExpanded(true, true);
        foreach (var (width, height) in new (double, double)[] { (440, 140), (620, 220), (860, 320) })
        {
            _settings.ClipboardWidth = width; _settings.ClipboardHeight = height; Refresh();
            _settings.ShelfArrangement = ShelfArrangement.Split; Build();
            await Save($"shelf-split-{width}x{height}", scale);
            _settings.ShelfArrangement = ShelfArrangement.Stacked;
            Front(ShelfSide.Clipboard); Build();
            await Save($"shelf-stacked-clips-{width}x{height}", scale);
            Front(ShelfSide.Files); Build();
            await Save($"shelf-stacked-files-{width}x{height}", scale);
        }
        // The cards trading places at the default size, taken at moments of the swap.
        _settings.ClipboardWidth = 620; _settings.ClipboardHeight = 220; Refresh();
        _settings.Animations = true;
        Front(ShelfSide.Clipboard); Build();
        await Settle();
        Program.Call(_deck, "BringForward", [typeof(ShelfSide)], ShelfSide.Files);
        var start = DateTime.UtcNow;
        foreach (int ms in new[] { 40, 90, 150, 230, 350, 600 })
        {
            var wait = start.AddMilliseconds(ms) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            Write(Capture(scale), Path.Combine(output, $"shelf-swap-{ms}ms.png"));
            Console.WriteLine($"shelf-swap-{ms}ms");
        }
        _settings.ShelfArrangement = ShelfArrangement.Split;
        SelectPage(DeckPage.Usage);
        _deck.SetExpanded(false, true);
        await Settle();
    }

    // Every way the clipboard and the file transfer show their entries (ClipView, FileView), at three sizes; then, at the default
    // size, entries under the pointer, several chosen, a file that is gone, one column alone, the stacked cards, an empty category
    // and an empty page; and the settings' cards. For checking the layout, not for the README.
    private async Task ShelfViews()
    {
        const double scale = 1.5;
        var data = (ShelfData)_shelf.GetType().GetProperty("Data", Program.Any)!.GetValue(_shelf)!;
        var picked = (HashSet<Guid>)Program.Field(_deck, "_picked")!;
        void Build() => Program.Call(_deck, "RenderShelf", [typeof(bool)], false);
        void Tab(ShelfSide side, Guid? category) => Program.Call(_shelf, "ShowTab", [typeof(ShelfSide), typeof(Guid?)], side, category);
        // The pointer on an entry: the entry's own handler lights it and shows its buttons.
        async Task Hover(params Guid[] ids)
        {
            await Settle();
            var rows = (System.Collections.IDictionary)Program.Field(_deck, "_shelfRows")!;
            foreach (var id in ids)
                if (rows[id] is { } row && row.GetType().GetField("Item1")!.GetValue(row) is UIElement frame)
                    frame.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
        }
        // More files than the README's: a real picture, a folder, a very long name, and a file that is gone.
        string folder = Path.GetDirectoryName(data.Files[0].Path)!;
        string picture = Path.Combine(folder, Loc.T("风景.png", "Landscape.png"));
        File.WriteAllBytes(picture, Png(Cover(640, 400)));
        string designs = Path.Combine(folder, Loc.T("设计稿", "Designs"));
        Directory.CreateDirectory(designs);
        string longName = Path.Combine(folder, Loc.T("2026 年第三季度华东区销售数据汇总（最终版）(2).xlsx", "Sales figures for the third quarter of 2026, eastern region (final) (2).xlsx"));
        File.WriteAllBytes(longName, new byte[2048]);
        string gone = Path.Combine(folder, Loc.T("已删除的文件.pdf", "Deleted file.pdf"));
        var added = new[] { picture, designs, longName, gone }.Select((path, i) => new TransferFile { Path = path, Added = _now.AddMinutes(-1 - i) }).ToList();
        data.Files.InsertRange(0, added);
        var clips = data.Clips; var files = data.Files;

        _settings.Animations = false; _settings.ShelfArrangement = ShelfArrangement.Split; _settings.ShelfShown = ShelfShown.Both;
        SelectPage(DeckPage.Clipboard);
        _deck.SetExpanded(true, true);
        async Task Shot(string name, double width, double height, ClipView clipView, FileView fileView, double share = 40, Func<Task>? then = null)
        {
            _settings.ClipboardWidth = width; _settings.ClipboardHeight = height;
            _settings.ShelfClipView = clipView; _settings.ShelfFileView = fileView; _settings.ShelfFileShare = share;
            Refresh(); Build();
            if (then is not null) await then();
            await Save(name, scale);
        }
        foreach (var (width, height) in new (double, double)[] { (440, 140), (620, 220), (860, 320) })
        {
            string size = $"{width}x{height}";
            await Shot($"views-{size}-1-single-single", width, height, ClipView.Single, FileView.Single);
            await Shot($"views-{size}-2-three-two", width, height, ClipView.Three, FileView.Two);
            await Shot($"views-{size}-3-blocks-grid", width, height, ClipView.Blocks, FileView.Grid);
            await Shot($"views-{size}-4-single-row", width, height, ClipView.Single, FileView.Row);
            await Shot($"views-{size}-5-three-column", width, height, ClipView.Three, FileView.Column, 20);
        }
        // Under the pointer.
        await Shot("state-1-hover-single-single", 620, 220, ClipView.Single, FileView.Single, then: () => Hover(clips[0].Id, files[0].Id));
        await Shot("state-1-hover-single-single-narrow", 620, 220, ClipView.Single, FileView.Single, 20, () => Hover(clips[1].Id, files[2].Id));
        await Shot("state-1-hover-three-two", 620, 220, ClipView.Three, FileView.Two, then: () => Hover(clips[1].Id, files[1].Id));
        await Shot("state-1-hover-blocks-grid", 620, 220, ClipView.Blocks, FileView.Grid, then: () => Hover(clips[2].Id, files[1].Id));
        await Shot("state-1-hover-single-row", 620, 220, ClipView.Single, FileView.Row, then: () => Hover(files[2].Id));
        await Shot("state-1-hover-single-column", 620, 220, ClipView.Single, FileView.Column, 20, () => Hover(files[0].Id));
        await Shot("state-1-hover-blocks-grid-440", 440, 140, ClipView.Blocks, FileView.Grid, then: () => Hover(clips[0].Id, files[1].Id));
        // Several chosen: three files next to each other and one apart, then the same of the clipboard.
        picked.UnionWith([files[0].Id, files[1].Id, files[2].Id, files[4].Id]);
        await Shot("state-2-picked-files-single", 620, 220, ClipView.Single, FileView.Single);
        await Shot("state-2-picked-files-two", 620, 220, ClipView.Single, FileView.Two);
        await Shot("state-2-picked-files-grid", 620, 220, ClipView.Single, FileView.Grid);
        await Shot("state-2-picked-files-row", 620, 220, ClipView.Single, FileView.Row);
        await Shot("state-2-picked-files-column", 620, 220, ClipView.Single, FileView.Column, 20);
        picked.Clear(); picked.UnionWith([clips[0].Id, clips[1].Id, clips[2].Id, clips[4].Id]);
        await Shot("state-2-picked-clips-single", 620, 220, ClipView.Single, FileView.Single);
        await Shot("state-2-picked-clips-three", 620, 220, ClipView.Three, FileView.Single);
        await Shot("state-2-picked-clips-blocks", 620, 220, ClipView.Blocks, FileView.Single);
        picked.Clear();
        // The one column dragged to its narrowest: a strip, whose header keeps the name, one "⌄" for the filters, and "清除".
        await Shot("state-6-strip-440x140", 440, 140, ClipView.Single, FileView.Column, 8);
        await Shot("state-6-strip-620x220", 620, 220, ClipView.Three, FileView.Column, 8, () => Hover(files[0].Id));
        await Shot("state-6-strip-860x320", 860, 320, ClipView.Blocks, FileView.Column, 8);
        picked.UnionWith([files[0].Id, files[1].Id]);
        await Shot("state-6-strip-picked-620x220", 620, 220, ClipView.Single, FileView.Column, 8);
        picked.Clear();
        // One column alone.
        _settings.ShelfShown = ShelfShown.Files;
        await Shot("state-3-files-only-grid", 620, 220, ClipView.Single, FileView.Grid);
        await Shot("state-3-files-only-row", 620, 220, ClipView.Single, FileView.Row);
        await Shot("state-3-files-only-two", 620, 220, ClipView.Single, FileView.Two);
        await Shot("state-3-files-only-column", 620, 220, ClipView.Single, FileView.Column);
        _settings.ShelfShown = ShelfShown.Clipboard;
        await Shot("state-3-clips-only-blocks", 620, 220, ClipView.Blocks, FileView.Single);
        await Shot("state-3-clips-only-three", 620, 220, ClipView.Three, FileView.Single);
        _settings.ShelfShown = ShelfShown.Both;
        // Stacked.
        _settings.ShelfArrangement = ShelfArrangement.Stacked;
        Program.SetField(_deck, "_shelfFront", ShelfSide.Files);
        await Shot("state-4-stacked-files-grid", 620, 220, ClipView.Blocks, FileView.Grid);
        await Shot("state-4-stacked-files-row", 620, 220, ClipView.Blocks, FileView.Row);
        Program.SetField(_deck, "_shelfFront", ShelfSide.Clipboard);
        await Shot("state-4-stacked-clips-blocks", 620, 220, ClipView.Blocks, FileView.Grid);
        await Shot("state-4-stacked-clips-three", 620, 220, ClipView.Three, FileView.Grid);
        _settings.ShelfArrangement = ShelfArrangement.Split;
        // The same under other panel themes: 像素方块 (square corners, stronger lines), 海岛假日 by day and by night, 海岛假日 2
        // by day, and 浅色. An entry under the pointer in each column, several chosen, and the empty page.
        var theme = Type.GetType("BrimDeck.PanelTheme, BrimDeck")!;
        // Marked as read, so that no theme folder of this computer is.
        theme.GetProperty("Loaded", Program.Any)!.SetValue(null, true);
        var use = theme.GetMethod("Use", Program.Any)!;
        int hour = DateTime.Now.Hour;
        string day = PanelThemes.FormatDaylight(TimeSpan.FromHours((hour + 23) % 24), TimeSpan.FromHours((hour + 11) % 24));
        string night = PanelThemes.FormatDaylight(TimeSpan.FromHours((hour + 1) % 24), TimeSpan.FromHours((hour + 13) % 24));
        foreach (var (id, daylight, tag) in new[] { (PanelThemes.PixelBlocks, "", "pixel"), ("island-holiday", day, "islandday"), ("island-holiday", night, "islandnight"), (PanelThemes.IslandHoliday2, day, "island2"), (PanelThemes.Light, "", "light") })
        {
            _settings.PanelTheme = id; _settings.ThemeDaylight = daylight;
            use.Invoke(null, [_settings, false, false]); Refresh();
            await Shot($"theme-{tag}-1-three-two-hover", 620, 220, ClipView.Three, FileView.Two, then: () => Hover(clips[1].Id, files[1].Id));
            await Shot($"theme-{tag}-2-blocks-grid-hover", 620, 220, ClipView.Blocks, FileView.Grid, then: () => Hover(clips[2].Id, files[1].Id));
            picked.UnionWith([clips[0].Id, clips[1].Id, clips[4].Id]);
            await Shot($"theme-{tag}-3-picked-single-row", 620, 220, ClipView.Single, FileView.Row);
            picked.Clear(); picked.UnionWith([clips[0].Id, clips[1].Id, clips[3].Id]);
            await Shot($"theme-{tag}-4-picked-blocks-single", 620, 220, ClipView.Blocks, FileView.Single);
            picked.Clear(); picked.UnionWith([files[0].Id, files[1].Id, files[4].Id]);
            await Shot($"theme-{tag}-5-picked-three-grid", 620, 220, ClipView.Three, FileView.Grid);
            picked.Clear();
            var holdClips = clips.ToList(); var holdFiles = files.ToList();
            clips.Clear(); files.Clear();
            await Shot($"theme-{tag}-6-empty", 620, 220, ClipView.Single, FileView.Single);
            clips.AddRange(holdClips); files.AddRange(holdFiles);
        }
        _settings.PanelTheme = PanelThemes.Dark; _settings.ThemeDaylight = "";
        use.Invoke(null, [_settings, false, false]); Refresh();
        // A category with nothing in it, "常用" emptied, and then nothing at all.
        string[] colors = ["#E5A385", "#9CB9FF"];
        var noClips = Shelf.AddCategory(data, ShelfSide.Clipboard, Loc.T("新分类", "New"), colors);
        var noFiles = Shelf.AddCategory(data, ShelfSide.Files, Loc.T("新分类", "New"), colors);
        Tab(ShelfSide.Clipboard, noClips.Id); Tab(ShelfSide.Files, noFiles.Id);
        await Shot("state-5-empty-category-620x220", 620, 220, ClipView.Single, FileView.Single);
        await Shot("state-5-empty-category-440x140", 440, 140, ClipView.Blocks, FileView.Grid);
        Tab(ShelfSide.Clipboard, null); Tab(ShelfSide.Files, null);
        var keptClips = clips.ToList(); var keptFiles = files.ToList();
        clips.Clear(); files.Clear();
        await Shot("state-5-empty-440x140", 440, 140, ClipView.Single, FileView.Single);
        await Shot("state-5-empty-620x220", 620, 220, ClipView.Three, FileView.Row);
        await Shot("state-5-empty-860x320", 860, 320, ClipView.Blocks, FileView.Grid);
        Tab(ShelfSide.Clipboard, ShelfData.Favorites);
        await Shot("state-5-empty-favorites-620x220", 620, 220, ClipView.Single, FileView.Single);
        await Shot("state-5-empty-favorites-440x140", 440, 140, ClipView.Single, FileView.Single);
        Tab(ShelfSide.Clipboard, null);
        clips.AddRange(keptClips); files.AddRange(keptFiles);
        foreach (var file in added) files.Remove(file);
        Shelf.DeleteCategory(data, ShelfSide.Clipboard, noClips.Id); Shelf.DeleteCategory(data, ShelfSide.Files, noFiles.Id);
        // The settings' cards, with a choice made in each row.
        _settings.ShelfClipView = ClipView.Three; _settings.ShelfFileView = FileView.Grid; _settings.ShelfFileShare = 40;
        await SettingsPage("views-settings", 17, 1000);
        _settings.ShelfClipView = ClipView.Single; _settings.ShelfFileView = FileView.Two;
        SelectPage(DeckPage.Usage);
        _deck.SetExpanded(false, true);
    }

    // What a picture does not show, measured with many entries and written to measures.txt: that the pointer on a file of the one
    // row moves no other file, at the row's start, scrolled and at its end; and how long a list takes to lay out a frame of
    // scrolling, in every way of showing entries (the rows that come into view are built in that time).
    private async Task ShelfMeasures()
    {
        var report = new System.Text.StringBuilder();
        var data = (ShelfData)_shelf.GetType().GetProperty("Data", Program.Any)!.GetValue(_shelf)!;
        string folder = Path.GetDirectoryName(data.Files[0].Path)!;
        _settings.Animations = false;
        SelectPage(DeckPage.Clipboard);
        _deck.SetExpanded(true, true);
        var files = data.Files; var clips = data.Clips;
        var moreFiles = Enumerable.Range(1, 60).Select(i =>
        {
            string path = Path.Combine(folder, $"measure {i:00}.{(i % 3 == 0 ? "png" : i % 3 == 1 ? "txt" : "docx")}");
            File.WriteAllBytes(path, i % 3 == 0 ? Png(Cover(320, 200)) : new byte[512]);
            return new TransferFile { Path = path, Added = _now.AddMinutes(-30 - i) };
        }).ToList();
        var moreClips = Enumerable.Range(1, 200).Select(i => new ClipEntry
        {
            Kind = ClipKind.Text, Text = string.Join("\n", Enumerable.Range(0, 1 + i % 4).Select(line => $"measure {i} line {line} lorem ipsum dolor sit amet")), Copied = _now.AddMinutes(-60 - i), Source = "notepad"
        }).ToList();
        files.AddRange(moreFiles); clips.AddRange(moreClips);
        _settings.ShelfArrangement = ShelfArrangement.Split; _settings.ShelfShown = ShelfShown.Both;
        _settings.ClipboardWidth = 620; _settings.ClipboardHeight = 220; _settings.ShelfFileShare = 40;
        var rows = (System.Collections.IDictionary)Program.Field(_deck, "_shelfRows")!;
        List<System.Windows.Controls.ScrollViewer> Viewers()
        {
            var found = new List<System.Windows.Controls.ScrollViewer>();
            void Walk(DependencyObject at)
            {
                if (at is System.Windows.Controls.ScrollViewer { Tag: ShelfSide } viewer) found.Add(viewer);
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(at); i++) Walk(VisualTreeHelper.GetChild(at, i));
            }
            Walk(_deck);
            return found;
        }

        // The one row: where every file in view stands before and after the pointer comes onto one of them.
        _settings.ShelfClipView = ClipView.Single; _settings.ShelfFileView = FileView.Row;
        Refresh(); Program.Call(_deck, "RenderShelf", [typeof(bool)], false);
        await Settle();
        var across = Viewers().First(viewer => (ShelfSide)viewer.Tag == ShelfSide.Files);
        Dictionary<Guid, double> Places() => files.Where(file => rows.Contains(file.Id)).Select(file => (file.Id, Frame: rows[file.Id]!.GetType().GetField("Item1")!.GetValue(rows[file.Id]) as FrameworkElement))
            .Where(entry => entry.Frame is { IsVisible: true } frame && across.IsAncestorOf(frame))
            .ToDictionary(entry => entry.Id, entry => entry.Frame!.TranslatePoint(new Point(0, 0), across).X);
        foreach (double offset in new[] { 0d, 37, 300, 1500, 1e6 })
        {
            across.ScrollToHorizontalOffset(offset);
            await Settle();
            var before = Places(); double was = across.HorizontalOffset;
            var under = before.Where(place => place.Value > 20 && place.Value < across.ViewportWidth - 80).Select(place => place.Key).First();
            if (rows[under]!.GetType().GetField("Item1")!.GetValue(rows[under]) is UIElement onto)
                onto.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
            await Settle();
            var after = Places();
            double moved = before.Keys.Where(after.ContainsKey).Max(id => Math.Abs(after[id] - before[id]));
            report.AppendLine($"row at {was:0.#}: the pointer on a file moves the others by {moved:0.##} DIP (offset then {across.HorizontalOffset:0.#}, extent {across.ExtentWidth:0.#})");
            if (rows[under]!.GetType().GetField("Item1")!.GetValue(rows[under]) is UIElement frame)
                frame.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
        }

        // The pointer on an entry changes that entry alone: the page is drawn before, with the pointer on each entry in view in
        // turn, and after it has left; what differs outside the entry, and what is left changed once it has gone, is reported.
        string[] names = ["微信.lnk", "网易云音乐.lnk", "Microsoft Edge.lnk", "BrimDeck-硬盘温度监控.zip", "Visual Studio Code.lnk", "QQ.lnk", "QQ音乐.lnk", "Google Chrome.lnk"];
        var named = names.Select((name, i) => { string path = Path.Combine(folder, name); File.WriteAllBytes(path, new byte[64]); return new TransferFile { Path = path, Added = _now.AddMinutes(-i) }; }).ToList();
        files.InsertRange(0, named);
        byte[] Pixels(out int width)
        {
            var image = Capture(1);
            width = image.PixelWidth;
            var bits = new byte[image.PixelWidth * image.PixelHeight * 4];
            image.CopyPixels(bits, image.PixelWidth * 4, 0);
            return bits;
        }
        // The pixels that differ, but for those inside `but`; and the box they lie in.
        static (int Count, Int32Rect Box) Differ(byte[] one, byte[] other, int width, Rect but)
        {
            int count = 0, left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            for (int i = 0; i < one.Length; i += 4)
            {
                if (Math.Abs(one[i] - other[i]) <= 6 && Math.Abs(one[i + 1] - other[i + 1]) <= 6 && Math.Abs(one[i + 2] - other[i + 2]) <= 6) continue;
                int x = i / 4 % width, y = i / 4 / width;
                if (but.Contains(x + .5, y + .5)) continue;
                count++; left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
            return (count, count == 0 ? default : new Int32Rect(left, top, right - left + 1, bottom - top + 1));
        }
        async Task Quick() { for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
        void Pointer(UIElement on, bool enters) => on.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
            { RoutedEvent = enters ? System.Windows.Input.Mouse.MouseEnterEvent : System.Windows.Input.Mouse.MouseLeaveEvent });
        foreach (var (width, height) in new (double, double)[] { (440, 140), (620, 220), (860, 320) })
            foreach (var (clipView, fileView) in new[] { (ClipView.Single, FileView.Single), (ClipView.Three, FileView.Two), (ClipView.Blocks, FileView.Grid), (ClipView.Single, FileView.Row), (ClipView.Three, FileView.Column) })
            {
                _settings.ClipboardWidth = width; _settings.ClipboardHeight = height; _settings.ShelfClipView = clipView; _settings.ShelfFileView = fileView;
                Refresh(); Program.Call(_deck, "RenderShelf", [typeof(bool)], false);
                await Settle();
                var rest = Pixels(out int stride);
                var stage = new Rect(0, 0, _stage.ActualWidth, _stage.ActualHeight);
                var shown = clips.Select(clip => (clip.Id, Side: "clipboard " + clipView, Name: clip.Text.Split('\n')[0])).Concat(files.Select(file => (file.Id, Side: "files " + fileView, file.Name)))
                    .Where(entry => rows.Contains(entry.Id)).Select(entry => (entry.Side, entry.Name, Frame: (FrameworkElement)rows[entry.Id]!.GetType().GetField("Item1")!.GetValue(rows[entry.Id])!))
                    .Where(entry => entry.Frame.IsVisible && _stage.IsAncestorOf(entry.Frame)).Select(entry => (entry.Side, entry.Name, entry.Frame, Box: entry.Frame.TransformToAncestor(_stage).TransformBounds(new Rect(entry.Frame.RenderSize))))
                    .Where(entry => stage.Contains(entry.Box)).ToList();
                int wrong = 0;
                foreach (var entry in shown)
                {
                    Pointer(entry.Frame, true);
                    await Quick();
                    var lit = Differ(rest, Pixels(out _), stride, Rect.Inflate(entry.Box, 1, 1));
                    if (lit.Count > 0 && wrong < 2) { Write(Capture(2), Path.Combine(output, $"wrong-{width}-{fileView}-{wrong}-lit.png")); Pointer(entry.Frame, false); await Quick(); Write(Capture(2), Path.Combine(output, $"wrong-{width}-{fileView}-{wrong}-rest.png")); }
                    Pointer(entry.Frame, false);
                    await Quick();
                    var left = Differ(rest, Pixels(out _), stride, Rect.Empty);
                    if (lit.Count == 0 && left.Count == 0) continue;
                    wrong++;
                    report.AppendLine($"{width}x{height} {entry.Side} \"{entry.Name}\" at {entry.Box.X:0},{entry.Box.Y:0} {entry.Box.Width:0}x{entry.Box.Height:0}: under the pointer {lit.Count} pixels differ outside it ({lit.Box}); after it left {left.Count} differ ({left.Box})");
                    if (left.Count > 0) rest = Pixels(out _);
                }
                report.AppendLine($"{width}x{height} {clipView} + {fileView}: {shown.Count} entries tried, {wrong} change something else");
            }
        foreach (var file in named) files.Remove(file);

        // A frame of scrolling: the list moved by 12 DIP and laid out, over its whole length or 150 frames.
        foreach (var (clipView, fileView) in new[] { (ClipView.Single, FileView.Single), (ClipView.Three, FileView.Two), (ClipView.Blocks, FileView.Grid), (ClipView.Single, FileView.Row), (ClipView.Three, FileView.Column) })
        {
            _settings.ShelfClipView = clipView; _settings.ShelfFileView = fileView;
            Refresh(); Program.Call(_deck, "RenderShelf", [typeof(bool)], false);
            await Settle();
            foreach (var viewer in Viewers())
            {
                var side = (ShelfSide)viewer.Tag; bool sideways = viewer.ScrollableWidth > 1;
                double room = sideways ? viewer.ScrollableWidth : viewer.ScrollableHeight;
                var times = new List<double>();
                var watch = new Stopwatch();
                for (double at = 12; at < room && times.Count < 150; at += 12)
                {
                    watch.Restart();
                    if (sideways) viewer.ScrollToHorizontalOffset(at); else viewer.ScrollToVerticalOffset(at);
                    viewer.UpdateLayout();
                    times.Add(watch.Elapsed.TotalMilliseconds);
                }
                if (times.Count == 0) { report.AppendLine($"{side} {(side == ShelfSide.Clipboard ? clipView : fileView)}: nothing to scroll"); continue; }
                times.Sort();
                report.AppendLine($"{side} {(side == ShelfSide.Clipboard ? clipView : fileView)}: {times.Count} frames, mean {times.Average():0.00} ms, median {times[times.Count / 2]:0.00} ms, worst {times[^1]:0.00} ms, over 8 ms {times.Count(time => time > 8)}");
            }
        }
        foreach (var file in moreFiles) files.Remove(file);
        foreach (var clip in moreClips) clips.Remove(clip);
        File.WriteAllText(Path.Combine(output, "measures.txt"), report.ToString());
        Console.Write(report);
        _settings.ShelfClipView = ClipView.Single; _settings.ShelfFileView = FileView.Two;
        SelectPage(DeckPage.Usage);
        _deck.SetExpanded(false, true);
    }

    private async Task SettingsPage(string name, int page, double height = 640)
    {
        const double scale = 1.5, width = 960;
        _settings.Theme = SettingsTheme.Dark;
        var window = new SettingsWindow(app);
        window.ShowPage(page);
        var root = window.RootVisual;
        var resources = window.Resources;
        window.Content = null;
        root.Resources = resources;
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height));
        await Settle();
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root); bitmap.Freeze();
        Write(bitmap, Path.Combine(output, name + ".png"));
        Console.WriteLine(name);
        window.Close();
    }

    private object Point(object? element)
    {
        var target = (FrameworkElement)element!;
        var point = target.TranslatePoint(new Point(target.ActualWidth / 2, target.ActualHeight / 2), _stage);
        return new { x = point.X, y = point.Y };
    }

    private object Bounds()
    {
        var island = (FrameworkElement)_deck.FindName("Island")!;
        var point = island.TranslatePoint(new Point(0, 0), _stage);
        return new { x = point.X, y = point.Y, width = island.ActualWidth, height = island.ActualHeight };
    }

    // Captures for the given length of normal time; slow is how much slower the panel's animations run meanwhile.
    private async Task Record(double seconds, int slow, double scale, double fps = 30)
    {
        var watch = Stopwatch.StartNew();
        double start = _clock, last = double.NegativeInfinity;
        while (true)
        {
            double time = watch.Elapsed.TotalSeconds / slow;
            if (time > seconds) break;
            if (time - last >= 1 / fps / 3)
            {
                Equalizer(start + time);
                _onFrame?.Invoke(start + time);
                _frames.Add(new Frame(Capture(scale), start + time)); last = time;
            }
            await Task.Delay(8);
        }
        _clock = start + seconds;
    }

    private void Refresh() { _deck.ApplySettings(); Program.Call(_deck, "RenderCompact", []); }
    private void SelectPage(DeckPage page) => Program.Call(_deck, "SelectPage", [typeof(DeckPage), typeof(bool)], page, false);

    private void SetSyncRing(double fraction)
    {
        // The window never loads here, so ApplySettings stops the timer; the ring is shown only while it runs.
        ((DispatcherTimer)Program.Field(_deck, "_refresh")!).Start();
        Program.SetField(_deck, "_syncCycleStart", DateTime.UtcNow.AddSeconds(-60 * fraction));
        Program.Call(_deck, "RenderSyncButton", []);
    }

    // Equalizer bars follow an invented signal that flows from left to right, 75 ms per bar, like the real ones.
    private void Equalizer(double time)
    {
        static double Signal(double t) => Math.Clamp(.52 + .22 * Math.Sin(t * 7.1) + .16 * Math.Sin(t * 12.7 + 1.3) + .12 * Math.Sin(t * 3.3 + .4)
            + .25 * Math.Pow(Math.Max(0, Math.Sin(t * Math.PI * 2 * 1.05)), 12), .08, 1);
        foreach (var name in new[] { "_musicEqualizer", "_compactEqualizer" })
        {
            if (Program.Field(_deck, name) is not { } equalizer) continue;
            var levels = (double[])Program.Field(equalizer, "_levels")!;
            for (int i = 0; i < levels.Length; i++) levels[i] = Signal(time - i * .075);
            Program.SetProperty(equalizer, "Playing", true);
            Program.Call(equalizer, "Redraw", [typeof(bool)], true);
        }
    }

    private BitmapSource Capture(double scale)
    {
        _stage.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(_stage.ActualWidth * scale), (int)Math.Ceiling(_stage.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(_stage); bitmap.Freeze();
        return bitmap;
    }

    // The equalizer is set just before the capture; the panel's own tick would otherwise bring the bars to rest.
    private async Task Save(string name, double scale, double equalizer = 1.3)
    {
        await Settle();
        Equalizer(equalizer);
        Write(Capture(scale), Path.Combine(output, name + ".png"));
        Console.WriteLine(name);
    }

    private static void Write(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0)));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // Lets layout, bindings and the first render pass finish.
    private static async Task Settle()
    {
        for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(120);
        // The files' icons and the clipboard's thumbnails are read on other threads; the picture waits for them.
        for (int i = 0; i < 400 && (int)Pending.GetValue(null)! + (int)Reading.GetValue(null)! > 0; i++) await Task.Delay(15);
        for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static readonly PropertyInfo Pending = Type.GetType("BrimDeck.ShelfFiles, BrimDeck")!.GetProperty("Pending", Program.Any)!,
        Reading = Type.GetType("BrimDeck.ShelfService, BrimDeck")!.GetProperty("Reading", Program.Any)!;
}
