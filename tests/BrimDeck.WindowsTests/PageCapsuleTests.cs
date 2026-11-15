using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// Soft reads only this property. No real App startup, accounts or user settings are involved.
public sealed class App : Application
{
    public DeckSettings Settings { get; } = new();
}

public partial class MainWindow
{
    private DeckSettings Settings { get; } = new();
    // As the real panel: its own profile decides whether it animates (Soft.For, Soft.At).
    internal DeckSettings PanelSettings => Settings;
    private bool Animates => Soft.For(Settings);
    private DeckPage CurrentPage { get; set; }
    private readonly Button AIUsageButton = new() { Content = "Usage", Width = 60, Height = 24 };
    private readonly Button MusicPageButton = new() { Content = "Music", Width = 60, Height = 24 };
    private readonly Button MonitorPageButton = new() { Content = "Monitor", Width = 60, Height = 24 };
    private readonly Button ShelfPageButton = new() { Content = "Clipboard", Width = 60, Height = 24 };
    private readonly Button DevicesPageButton = new() { Content = "Devices", Width = 60, Height = 24 };
    private readonly Grid PageSwitch = new();
    private readonly Border PageCapsule = new() { Width = 60, Height = 24 };
    private readonly TranslateTransform PageCapsuleShift = new();
    private readonly ScaleTransform PageCapsuleStretch = new();
    private bool _expanded = true, _preview = false;

    // Links the actual production method, including its retry branch and button reorder, rather than testing the helper alone.
    internal static void CheckPageCapsule(Action<string, bool> check)
    {
        var window = new MainWindow { Width = 260, Height = 100, Left = -20000, Top = -20000,
            ShowActivated = false, WindowStyle = WindowStyle.None };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(window.AIUsageButton); buttons.Children.Add(window.MonitorPageButton); buttons.Children.Add(window.MusicPageButton); buttons.Children.Add(window.ShelfPageButton);
        window.PageCapsule.RenderTransform = new TransformGroup { Children = { window.PageCapsuleStretch, window.PageCapsuleShift } };
        window.PageSwitch.Children.Add(window.PageCapsule); window.PageSwitch.Children.Add(buttons);
        window.Content = window.PageSwitch;
        try
        {
            window.Show();
            PumpCapsule(() => window.PlacePageCapsule(), check);
            check("page capsule starts at the measured usage button", window.PageCapsule.Width == window.AIUsageButton.ActualWidth);

            // Usage details hide the whole switch; it retains the old ActualWidth after a button invalidates its measure.
            window.PageSwitch.Visibility = Visibility.Collapsed;
            window.AIUsageButton.Width = 80;
            PumpCapsule(() => { for (int i = 0; i < 20; i++) window.PlacePageCapsule(); }, check);
            check("production placement waits once while the switch is collapsed", window._capsulePending);

            // A page changed while the callback was waiting: it must use the latest page and its latest order, not the old button.
            window.CurrentPage = DeckPage.Monitor;
            window.Settings.PageOrder = [DeckPage.Music, DeckPage.Usage, DeckPage.Monitor];
            window.OrderPageButtons();
            window.MonitorPageButton.Width = 90;
            window.PageSwitch.Visibility = Visibility.Visible;
            PumpCapsule(() => window.PlacePageCapsule(), check);
            var at = window.MonitorPageButton.TranslatePoint(new Point(), window.PageSwitch).X;
            check("pending production placement uses the current page after reorder", !window._capsulePending &&
                (double)window.PageCapsule.GetAnimationBaseValue(WidthProperty) == window.MonitorPageButton.ActualWidth && Math.Abs((double)window.PageCapsuleShift.GetAnimationBaseValue(TranslateTransform.XProperty) - at) < .5);

            // An invisible/retired panel must yield too, even though its controls keep their previous size.
            window.Hide(); window.MonitorPageButton.Width = 95;
            PumpCapsule(() => window.PlacePageCapsule(), check);
            check("hidden production window does not spin while waiting for layout", window._capsulePending);
        }
        finally { window.Close(); }
    }

    // motion-review F8: moving the capsule to a button of another width sets the width at once and draws the change by stretching
    // the capsule from its left edge (the real PlacePageCapsule), so the glide lays nothing out again. Skipped where animations are
    // off (Windows' effects off, high contrast), since nothing glides then.
    internal static void CheckPageCapsuleStretch(Action<string, bool> check)
    {
        var window = new MainWindow { Width = 400, Height = 100, Left = -20000, Top = -20000, ShowActivated = false, WindowStyle = WindowStyle.None };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(window.AIUsageButton); buttons.Children.Add(window.MonitorPageButton); buttons.Children.Add(window.MusicPageButton); buttons.Children.Add(window.ShelfPageButton);
        window.PageCapsule.RenderTransform = new TransformGroup { Children = { window.PageCapsuleStretch, window.PageCapsuleShift } };
        window.PageSwitch.Children.Add(window.PageCapsule); window.PageSwitch.Children.Add(buttons);
        window.Content = window.PageSwitch;
        try
        {
            window.Show();
            window.MonitorPageButton.Width = 90;
            window.CurrentPage = DeckPage.Usage; window.UpdateLayout(); window.PlacePageCapsule();
            window.CurrentPage = DeckPage.Monitor; window.PlacePageCapsule();
            check("the capsule's width is the new button's at once", window.PageCapsule.Width == window.MonitorPageButton.ActualWidth && window.MonitorPageButton.ActualWidth == 90);
            if (!BrimDeck.Soft.Enabled) { check("animations are off here: the stretch check is skipped", true); return; }
            check("the capsule's width is not what glides", !window.PageCapsule.HasAnimatedProperties);
            check("the change of width is drawn by stretching the capsule from the left", window.PageCapsuleStretch.HasAnimatedProperties);
            window.CurrentPage = DeckPage.Monitor; window.PlacePageCapsule();
            check("a placement without a change of width starts no stretch", !window.PageCapsuleStretch.HasAnimatedProperties && window.PageCapsuleStretch.ScaleX == 1);
        }
        finally { window.Close(); }
    }

    // The header changes size tier: the capsule follows the buttons' new drawn height at once (ApplyHeader), not at the next refresh.
    internal static void CheckPageCapsuleFit(Action<string, bool> check)
    {
        var window = new MainWindow { Width = 260, Height = 100, Left = -20000, Top = -20000, ShowActivated = false, WindowStyle = WindowStyle.None };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(window.AIUsageButton); buttons.Children.Add(window.MonitorPageButton); buttons.Children.Add(window.MusicPageButton); buttons.Children.Add(window.ShelfPageButton);
        window.PageCapsule.RenderTransform = new TransformGroup { Children = { window.PageCapsuleStretch, window.PageCapsuleShift } };
        window.PageSwitch.Children.Add(window.PageCapsule); window.PageSwitch.Children.Add(buttons);
        window.Content = window.PageSwitch;
        try
        {
            window.Show();
            var tall = PanelHeader.For(36, 20); var short_ = PanelHeader.For(30, 20);
            window.FitPageButtons(tall); window.UpdateLayout(); window.PlacePageCapsule();
            check("page capsule is as tall as the buttons draw in the tall header", window.PageCapsule.Height == tall.ButtonHeight && tall.ButtonHeight == 24);

            // The header moves to the shorter tier: the capsule follows the buttons at once, and a placement before the next layout
            // pass (RefreshStatus runs PlacePageCapsule before ApplyHeader) keeps it there, though ActualHeight is still the old tier's.
            window.FitPageButtons(short_);
            check("the capsule takes the new tier's height as the buttons change", window.PageCapsule.Height == short_.ButtonHeight && short_.ButtonHeight != tall.ButtonHeight);
            check("a button's ActualHeight before the next layout pass is still the old tier's", window.AIUsageButton.ActualHeight == tall.ButtonHeight);
            window.PlacePageCapsule();
            check("a placement before the next layout pass keeps the new tier's height", window.PageCapsule.Height == short_.ButtonHeight);
            window.UpdateLayout(); window.PlacePageCapsule();
            check("after layout the capsule and the buttons agree", window.PageCapsule.Height == window.AIUsageButton.ActualHeight);
        }
        finally { window.Close(); }
    }

    private static void PumpCapsule(Action arrange, Action<string, bool> check)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        int loadedPosts = 0;
        bool inputRan = false, backgroundRan = false, stopped = false, timedOut = false;
        DispatcherHookEventHandler count = (_, e) =>
        {
            if (e.Operation.Priority != DispatcherPriority.Loaded || ++loadedPosts <= 32 || stopped) return;
            stopped = true;
            dispatcher.BeginInvoke(DispatcherPriority.Send, () => frame.Continue = false);
        };
        dispatcher.Hooks.OperationPosted += count;
        // Runs outside the UI thread. An old Loaded-priority retry can starve DispatcherTimer, Input and ApplicationIdle.
        using var watchdog = new System.Threading.Timer(_ => dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        { timedOut = true; frame.Continue = false; }), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        try
        {
            arrange();
            dispatcher.BeginInvoke(DispatcherPriority.Input, () => inputRan = true);
            dispatcher.BeginInvoke(DispatcherPriority.Background, () => backgroundRan = true);
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
        finally { dispatcher.Hooks.OperationPosted -= count; }
        check("production page placement has a bounded Loaded queue", !stopped && loadedPosts <= 32);
        check("production page placement yields to input and settings/save-priority work", !timedOut && inputRan && backgroundRan);
    }
}