using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;
using BrimDeck.Core;
using BrimDeck.Native;

internal static class Program
{
    // Exercises the native adapters without starting the app, reading account
    // credentials, changing playback, or creating application settings.
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            // "--only power": just the power and battery checks, for a quick look at what this machine reports.
            if (args is ["--only", "power"]) { PowerChecks.Run(Check); return Finish(); }
            if (args is ["--only", "gpu"]) { CheckGpus(); return Finish(); }
            if (args is ["--only", "capture"]) { CaptureChecks.Run(Check); return Finish(); }
            if (args is ["--only", "frames"]) { FrameRateChecks.Run(Check); return Finish(); }
            if (args is ["--only", "ringletter", ..]) { RingLetterChecks.Run(Check, args.Length > 2 ? args[2] : null); return Finish(); }
            if (args is ["--only", "drawn"]) { DrawnAutomationTests.Run(Check); return Finish(); }
            if (args is ["--only", "settings"]) { SettingsFocusTests.Run(Check); BrimDeck.MainWindow.CheckPageCapsuleFit(Check); return Finish(); }
            // "--hosted": only the checks that hold on a CI runner (a virtual machine with a session but no display output, GPU,
            // battery or devices, and no promise about speed). Everything left out reads this machine's hardware, so its outcome
            // says something about the machine, not the code. Without the flag every check runs.
            bool hosted = args is ["--hosted"];
            Console.WriteLine($"Windows native smoke tests{(hosted ? " (hosted subset)" : "")}: {Environment.OSVersion.Version}");
            CheckWindowChrome();
            if (hosted) Console.WriteLine("EXCLUDED on a hosted runner: window capture (not yet known to work on a virtual display).");
            else CaptureChecks.Run(Check);
            CheckSettledClip();
            CheckPageToolTips();
            LayoutRetryTests.Run(Check);
            DrawnAutomationTests.Run(Check);
            RingLetterChecks.Run(Check);
            SettingsFocusTests.Run(Check);
            BrimDeck.MainWindow.CheckPageCapsule(Check);
            BrimDeck.MainWindow.CheckPageCapsuleFit(Check);
            BrimDeck.MainWindow.CheckShadowLifetime();
            if (hosted) Console.WriteLine("EXCLUDED on a hosted runner: GPU, display, frame rate, temperature and power readings, and the application identity the shell resolves (they read this machine or its shell).");
            else
            {
                CheckGpus();
                var (bounds, work, scale) = WindowsHost.PrimaryScreen();
                Check("primary display has usable bounds and DPI", bounds.Width > 0 && bounds.Height > 0
                    && work.Width > 0 && work.Height > 0 && scale > 0);
                AppIdentityChecks.Run(Check);
                FrameRateChecks.Run(Check);
                TemperatureChecks.Run(Check);
                PowerChecks.Run(Check);
            }
            SensorGuardChecks.Run(Check);

            return Finish();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void CheckWindowChrome()
    {
        var window = new Window { Width = 400, Height = 300, ShowInTaskbar = false };
        try
        {
            SettingsChrome.Apply(window);
            // Theme changes must also be safe before a native handle exists.
            SettingsChrome.SetTheme(window, true);
            Check("settings theme does not create a native handle", new WindowInteropHelper(window).Handle == IntPtr.Zero);
            var handle = new WindowInteropHelper(window).EnsureHandle();
            Check("settings window creates a native handle", handle != IntPtr.Zero);
            SettingsChrome.SetTheme(window, true);
            SettingsChrome.SetTheme(window, false);
            var chrome = WindowChrome.GetWindowChrome(window);
            Check("settings window remains resizable", window.ResizeMode == ResizeMode.CanResize && chrome.ResizeBorderThickness.Left > 0);
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                Check("Windows 10 settings use a square outer frame", chrome.CornerRadius == new CornerRadius(0));
            else
                Check("Windows 11 settings retain rounded corners", chrome.CornerRadius == new CornerRadius(12));
        }
        finally { window.Close(); }
    }

    // Reads every graphics adapter as the monitor does and lists them. A machine may have none with counters (a virtual machine
    // with the software rasterizer only), which is not a failure.
    private static void CheckGpus()
    {
        using var pdh = new PdhQuery();
        var provider = new GpuProvider(pdh);
        provider.Want(true);
        pdh.Collect();
        Thread.Sleep(1000);
        var reading = provider.Sample(pdh.Collect());
        var gpus = reading.OrDefault ?? [];
        if (!reading.HasValue) Console.WriteLine("SKIP: no GPU readings: " + reading.Reason);
        foreach (var gpu in gpus)
            Console.WriteLine($"GPU: {gpu.Name ?? "(unnamed)"} [{gpu.Kind}] usage {gpu.Usage:P0}, dedicated {Gigabytes(gpu.DedicatedBytes)} of {(gpu.DedicatedTotal is { } total ? $"{total / 1e9:0.0} GB" : "?")}, "
                + $"shared {Gigabytes(gpu.SharedBytes)}, key {gpu.Key}, {GpuReadings.LuidText(gpu.Luid)}, engines {string.Join(" ", gpu.Engines.Select(e => $"{e.Kind} {e.Usage:P0}"))}");
        var (adapters, described, _) = GpuAdapters.Read();
        foreach (var adapter in adapters.Values)
            Console.WriteLine($"DXGI: {adapter.Name} [{adapter.Kind}{(adapter.Software ? ", software" : "")}] {adapter.DedicatedTotal / 1e9:0.0} GB, at {adapter.Location ?? "?"}, key {adapter.Key}, {GpuReadings.LuidText(adapter.Luid)}");
        Check("GPU readings keep only the hardware adapters DXGI describes", gpus.All(gpu => described ? adapters.TryGetValue(gpu.Luid, out var adapter) && !adapter.Software : gpu.Name is null));
        Check("GPU readings give each adapter its own key", gpus.Select(gpu => gpu.Key).Distinct().Count() == gpus.Count);
        if (gpus.Any(gpu => gpu.Kind == GpuKind.Discrete))
            Check("GPU auto shows a discrete adapter", GpuChoice.Auto(gpus, 0)?.Kind == GpuKind.Discrete);
        // The drivers' own usage: asked twice, as Intel's counters need two readings; an adapter Windows saw idle is not asked.
        var drivers = new GpuDrivers(() => { });
        drivers.Read(gpus, 0, true);
        Thread.Sleep(1000);
        foreach (var gpu in drivers.Read(gpus, 1, true))
            Console.WriteLine($"GPU driver: {gpu.Name ?? "(unnamed)"} usage {gpu.Usage:P0} from {gpu.UsageSource ?? "Windows engines"}, engines {gpu.EngineUsage:P0}"
                + (gpu.DriverWatts is { } watts ? $", {watts:0.0} W" : "") + (gpu.DriverTemperature is { } heat ? $", {heat:0} °C" : ""));
        Check("GPU driver usage is a share, and only for an adapter Windows saw working", drivers.Read(gpus, 2, true).All(gpu => gpu.Usage is >= 0 and <= 1
            && (gpu.UsageSource is null || gpu.EngineUsage > 0)));
    }
    private static string Gigabytes(Reading<ulong> bytes) => bytes.TryGet(out var value) ? $"{value / 1e9:0.00} GB" : "?";

    // The collapsed island at rest clips to a rectangle (IslandBorder.SettledClip): nothing of it, its rim included, may then reach
    // past the rounded outline it shows with the rounded clip, at the notch's shoulders or the capsule's ends.
    private static void CheckSettledClip()
    {
        foreach (var (name, corners, shoulder) in new[] { ("notch", new CornerRadius(0, 0, 12, 12), 16d), ("capsule", new CornerRadius(18), 0d) })
            foreach (double scale in new[] { 1d, 1.5 })
            {
                byte[] Draw(bool settled)
                {
                    var island = new BrimDeck.IslandBorder { Width = 300, Height = 36, Background = System.Windows.Media.Brushes.Black, CornerRadius = corners,
                        ShoulderRadius = shoulder, MaterialOpacity = 1, RimWidth = 1, RimBrush = System.Windows.Media.Brushes.White, ClipToBounds = true,
                        SettledClip = settled };
                    island.Measure(new Size(300, 36)); island.Arrange(new Rect(0, 0, 300, 36)); island.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(300 * scale), (int)(36 * scale), 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(island);
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                    return pixels;
                }
                byte[] rounded = Draw(false), rest = Draw(true);
                // Without the clip the outline's own antialiased edge may tint a few pixels faintly; the rim's outer half was
                // whole pixels at full strength (99 of them on the notch before the fix).
                int outside = 0, strongest = 0;
                for (int i = 3; i < rounded.Length; i += 4) if (rounded[i] == 0 && rest[i] > 8) { outside++; strongest = Math.Max(strongest, rest[i]); }
                Check($"the {name} at rest draws nothing past its rounded outline at {scale * 100:0}% ({outside} faint edge pixels, strongest alpha {strongest})",
                    strongest <= 64 && outside <= 16);
            }
    }

    // A page left without the pointer moving closes the tooltips opened over it, and only those (BrimDeck.PageToolTips).
    private static void CheckPageToolTips()
    {
        var left = new Border { Width = 40, Height = 20 }; var kept = new Border { Width = 40, Height = 20 };
        var page = new StackPanel(); page.Children.Add(left);
        var other = new StackPanel(); other.Children.Add(kept);
        var root = new StackPanel(); root.Children.Add(page); root.Children.Add(other);
        // Off screen and never activated: nothing shows where the tests run.
        var window = new Window { Content = root, Left = -20000, Top = -20000, Width = 120, Height = 80, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        try
        {
            window.Show();
            var leftTip = new ToolTip { Content = "page", PlacementTarget = left, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            var keptTip = new ToolTip { Content = "other", PlacementTarget = kept, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            left.ToolTip = leftTip; kept.ToolTip = keptTip;
            leftTip.IsOpen = true; keptTip.IsOpen = true;
            Check("tooltips open for the page check", leftTip.IsOpen && keptTip.IsOpen);
            int closed = BrimDeck.PageToolTips.CloseWithin(page);
            Check("leaving a page closes the tooltip opened over it", closed == 1 && !leftTip.IsOpen);
            Check("leaving a page keeps a tooltip opened elsewhere", keptTip.IsOpen);
            keptTip.IsOpen = false;
            Check("nothing open over the page closes nothing", BrimDeck.PageToolTips.CloseWithin(page) == 0);
        }
        finally { window.Close(); }
    }

    // Every check runs, so one failure does not hide the rest; the run fails if any did.
    private static int Finish()
    {
        Console.WriteLine(_failed.Count == 0 ? $"{_passed} checks passed." : $"{_passed} checks passed, {_failed.Count} failed:" + string.Concat(_failed.Select(name => "\n  " + name)));
        return _failed.Count == 0 ? 0 : 1;
    }
    private static int _passed;
    private static readonly List<string> _failed = [];
    private static void Check(string name, bool condition)
    {
        if (condition) { _passed++; Console.WriteLine("PASS: " + name); }
        else { _failed.Add(name); Console.WriteLine("FAIL: " + name); }
    }
}
