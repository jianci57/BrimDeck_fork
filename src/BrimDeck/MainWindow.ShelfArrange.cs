using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;
using Path = System.Windows.Shapes.Path;

namespace BrimDeck;

// How the clipboard and file transfer page shows its two columns (DeckSettings.ShelfArrangement). Side by side, the divider between
// them can be dragged to give either column more of the width; the file transfer's share is saved (DeckSettings.ShelfFileShare),
// and a double-click on the divider gives back the default. Stacked, the two columns are cards of one width, and the one not in
// front shows as a side strip (its icon, its name written downward, its count): the clipboard's at the left, the file transfer's
// at the right. Switching, the card in front narrows to its strip at its own side while the other widens over it (ShelfDeck). A click on the strip, on the title of the card in front or on the page's own button brings the other card
// forward, as do the ways chosen in the settings; files dragged over the panel bring the file transfer forward, and editing a
// text brings the clipboard.
public partial class MainWindow
{
    // The card in front when stacked, kept while the panel is open and closed, and the one the page was last built with, so a
    // change between them is drawn as the cards trading places.
    private ShelfSide _shelfFront = ShelfSide.Clipboard;
    private ShelfSide? _shelfBuiltFront;
    // While the divider is dragged: the file transfer's share it gives, and how far from the divider's middle it was taken.
    private double? _shareDrag;
    private double _shareGrab;
    private bool _shareWatching;
    // The divider last built and how it is drawn lit or at rest, for the end of a drag.
    private (Border Grip, Action<bool> Show)? _divider;

    private ShelfLayout ShelfLayoutFor(double width, double height) =>
        ShelfLayout.For(width, height, _shareDrag ?? Settings.ShelfFileShare, Settings.ShelfStacked(), Settings.ShelfShown, Settings.ShelfClipView, Settings.ShelfFileView);

    private ShelfSide ShelfFront => _shelfEdit is not null ? ShelfSide.Clipboard : _dropping ? ShelfSide.Files : _shelfFront;

    // Opened from the file transfer's count on the island, the panel opens on this page with the file transfer in front.
    private bool OpensOnTransfer()
    {
        if (!PointerOnTransfer()) return false;
        _shelfFront = ShelfSide.Files;
        return true;
    }

    private FrameworkElement ShelfColumns(ShelfLayout layout)
    {
        if (layout.Stacked) return StackedColumns(layout);
        _deck = null; _shelfBuiltFront = null;
        // One column only (DeckSettings.ShelfShown): it takes the whole page, with no divider.
        if (Settings.ShelfShown == ShelfShown.Clipboard) return ClipColumn(layout);
        if (Settings.ShelfShown == ShelfShown.Files) return FileColumn(layout);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.Gap) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.FileColumn) });
        var clips = ClipColumn(layout);
        var files = FileColumn(layout);
        Grid.SetColumn(files, 2);
        var divider = ShelfDivider();
        Grid.SetColumn(divider, 1);
        grid.Children.Add(clips); grid.Children.Add(files); grid.Children.Add(divider);
        return grid;
    }

    // ---- Side by side: the divider ----
    // The line stays 1 DIP of the hairline at rest; under the pointer it widens to 2 DIP and brightens, and while dragged it takes
    // the accent. The part that takes the pointer is 12 DIP wide, centered in the gap.
    private const double DividerGrip = 12;
    private Border ShelfDivider()
    {
        var line = new Border { Width = 1, Background = UI.Brush(Look.Hairline), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 4), CornerRadius = new CornerRadius(1), IsHitTestVisible = false };
        var grip = new Border { Width = DividerGrip, Background = Brushes.Transparent, Cursor = Cursors.SizeWE, HorizontalAlignment = HorizontalAlignment.Center, Child = line };
        AutomationProperties.SetName(grip, Loc.T("两栏宽度", "Column widths"));
        void Show(bool lit) { line.Width = lit ? 2 : 1; line.Background = UI.Brush(_shareDrag is not null ? Look.Hex("accent") : lit ? Look.Hex("track") : Look.Hairline); }
        _divider = (grip, Show);
        grip.MouseEnter += (_, _) => Show(true);
        grip.MouseLeave += (_, _) => { if (_shareDrag is null) Show(false); };
        grip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ClickCount == 2) { ResetShare(); return; }
            WatchShareDrag();
            _shareGrab = e.GetPosition(grip).X - grip.ActualWidth / 2;
            _shareDrag = Settings.ShelfFileShare;
            Show(true);
            // The page's own grid takes the pointer, not the divider: the page is built again where its sizes step during the drag,
            // and the new divider would not have it.
            ShelfContent.CaptureMouse();
        };
        return grip;
    }
    private void WatchShareDrag()
    {
        if (_shareWatching) return;
        _shareWatching = true;
        ShelfContent.MouseMove += (_, e) =>
        {
            if (_shareDrag is null || !ShelfContent.IsMouseCaptured) return;
            e.Handled = true;
            if (ShelfContent.Children.Count != 1 || ShelfContent.Children[0] is not Grid { ColumnDefinitions.Count: 3 } grid) return;
            double gap = grid.ColumnDefinitions[1].ActualWidth, inner = grid.ActualWidth - gap;
            double middle = e.GetPosition(grid).X - _shareGrab;
            var view = Settings.ShelfFileView;
            double files = ShelfLayout.FileWidth(inner, ShelfLayout.ClampShare((grid.ActualWidth - middle - gap / 2) / Math.Max(1, inner) * 100, view), view);
            double share = Math.Round(files / Math.Max(1, inner) * 100, 1);
            if (Math.Abs(share - _shareDrag.Value) < .05) return;
            _shareDrag = share;
            StretchShelf();
        };
        ShelfContent.MouseLeftButtonUp += (_, e) =>
        {
            if (_shareDrag is null) return;
            e.Handled = true;
            ShelfContent.ReleaseMouseCapture();
        };
        // Released, or the capture taken away (another window, Esc in a drag): the share is saved where it stands.
        ShelfContent.LostMouseCapture += (_, _) =>
        {
            if (_shareDrag is not { } share) return;
            _shareDrag = null;
            if (Math.Abs(share - Settings.ShelfFileShare) >= .05) SaveShare(share);
            else
            {
                // Back where it started: the columns go back to the saved share, and the page is not built again unless a step was
                // crossed meanwhile, so a double-click finds the same divider for its second press.
                StretchShelf();
                if (_divider is { } divider) divider.Show(divider.Grip.IsMouseOver);
            }
            if (_expanded && !_pointerInside && !ShelfHolds) PointerChanged(false);
        };
    }
    // A step in what the rows show, passed while the widths are dragged: the page is built for it once the drag has rested for a
    // moment. If the drag has ended meanwhile, the page was built as the share was saved.
    private DispatcherTimer? _shareSettle;
    private void SettleShare()
    {
        if (_shareSettle is null)
        {
            _shareSettle = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(160) };
            _shareSettle.Tick += (_, _) =>
            {
                _shareSettle.Stop();
                if (_shareDrag is null || !IsShelfPage || _shelfDetail is not null) return;
                double width = ContentCanvas.Width, height = ContentCanvas.Height;
                if (!double.IsFinite(width) || !double.IsFinite(height)) return;
                if (ShelfLayoutFor(width, height) with { FileColumn = 0, ClipColumn = 0 } != _shelfLayout with { FileColumn = 0, ClipColumn = 0 }) RenderShelf();
            };
        }
        _shareSettle.Stop(); _shareSettle.Start();
    }
    // The widths' slider in the settings is dragged: the columns follow it as they follow the divider, without anything being
    // saved; null when the drag has ended (the settings save the share themselves).
    internal void PreviewShelfShare(double? share)
    {
        _shareDrag = share is { } value && Settings.ShelfShown == ShelfShown.Both && Settings.ShelfArrangement == ShelfArrangement.Split ? ShelfLayout.ClampShare(Math.Round(value, 1), Settings.ShelfFileView) : null;
        if (IsShelfPage && (_expanded || _preview)) StretchShelf();
    }
    // The double-click: the default share, at once.
    private void ResetShare() { _shareDrag = null; SaveShare(ShelfLayout.DefaultShare); }
    private void SaveShare(double share)
    {
        share = ShelfLayout.ClampShare(Math.Round(share, 1), Settings.ShelfFileView);
        if (Math.Abs(share - Settings.ShelfFileShare) < .05) { RenderShelf(); return; }
        _app.UpdateSettingsFor(Profile, SettingsWith(s => s.ShelfFileShare = share));
        _app.ShelfShareChanged();
    }

    // ---- Stacked: two cards ----
    // The two cards are the same thing, mirrored: the clipboard's keeps to the left edge, the file transfer's to the right. A card
    // in front is as wide as the page less the other's strip and shows its column; a card behind is narrowed to its strip (and
    // Under more, which lies beneath the card in front), a little shorter, in a darker fill, and shows its strip's parts instead.
    // Switching, both go from the one to the other together (SlideDeck), as in the design: the card coming forward lies on top at
    // once, so only its inner edge is seen moving across the page.
    private sealed class DeckCard
    {
        public required ShelfSide Side;
        // The card itself: its width and its top and bottom margins change. Body holds the column at the width of a card in front,
        // Edge the strip's parts at the card's outer side.
        public required Border Root, Body;
        public required FrameworkElement Edge, Icon, Count;
        public required SolidColorBrush Fill, Rim, Ink, Faint;
        // What the strip's parts need along the strip.
        public required double Padding, Gap, IconSize, NameLength, CountSize;
        public required bool Counted;
    }
    private sealed class Deck
    {
        public required Grid Host;
        public required DeckCard Clips, Files;
        public required Border Hit;
        public required Color CardFill, CardRim, Rest, RestRim, Lit, Down, Ink, InkLit, Faint, FaintLit;
        // The card in front as drawn, and the change under way (counted, so one that was overtaken does not end the next).
        public ShelfSide Front;
        public int Slide;
        public bool Moving;
        public DeckCard Behind => Front == ShelfSide.Clipboard ? Files : Clips;
    }
    private Deck? _deck;
    private DispatcherTimer? _deckHoverTimer;
    private long _deckSwapCooldownUntil, _shelfShiftWheelCooldownUntil;
    private readonly HorizontalSwipe _shelfSwipe = new();
    private int _shelfShiftWheelRest;
    private long _shelfShiftWheelAt;
    // The curves of the design: cubic-bezier(.22, 1, .36, 1) for the cards' size, and "ease" for what fades or changes color.
    private static readonly IEasingFunction DeckEase = FrozenEase(.22, 1, .36, 1), DeckFade = FrozenEase(.25, .1, .25, 1);
    private static IEasingFunction FrozenEase(double x1, double y1, double x2, double y2) { var ease = new BezierEase(x1, y1, x2, y2); ease.Freeze(); return ease; }

    private static readonly Geometry ClipboardPageIcon = FrozenGeometry(
        "M 8.5,4.5 H 6.5 A 2,2 0 0 0 4.5,6.5 V 19.5 A 2,2 0 0 0 6.5,21.5 H 17.5 A 2,2 0 0 0 19.5,19.5 V 6.5 A 2,2 0 0 0 17.5,4.5 H 15.5 M 9.5,2.5 H 14.5 A 1,1 0 0 1 15.5,3.5 V 5.5 A 1,1 0 0 1 14.5,6.5 H 9.5 A 1,1 0 0 1 8.5,5.5 V 3.5 A 1,1 0 0 1 9.5,2.5 Z M 8.5,11.5 H 15.5 M 8.5,15.5 H 13");
    private static readonly Geometry TrayPageIcon = FrozenGeometry(
        "M 3,13 L 6,5 H 18 L 21,13 V 18 A 1.5,1.5 0 0 1 19.5,19.5 H 4.5 A 1.5,1.5 0 0 1 3,18 Z M 3,13 H 8.5 L 10,15 H 14 L 15.5,13 H 21");
    private static Geometry FrozenGeometry(string data) { var g = Geometry.Parse(data); g.Freeze(); return g; }

    internal void UpdateShelfPageButton()
    {
        // The search covers the columns shown.
        AutomationProperties.SetName(ShelfSearchButton, ShelfSearchName);
        if (IsShelfPage && Settings.ShelfStacked() && !ShelfHidesSwitch)
        {
            bool filesFront = ShelfFront == ShelfSide.Files;
            ShelfPageIcon.Data = filesFront ? TrayPageIcon : ClipboardPageIcon;
            string label = filesFront ? Loc.T("切换到剪贴板", "Switch to clipboard") : Loc.T("切换到文件中转", "Switch to file transfer");
            AutomationProperties.SetName(ShelfPageButton, label);
            ShelfPageButton.ToolTip = label;
            return;
        }
        // Showing the file transfer alone, the page's button is its tray.
        ShelfPageIcon.Data = Settings.ShelfShown == ShelfShown.Files ? TrayPageIcon : ClipboardPageIcon;
        AutomationProperties.SetName(ShelfPageButton, Settings.ShelfShown switch
        {
            ShelfShown.Clipboard => Loc.T("剪贴板", "Clipboard"),
            ShelfShown.Files => Loc.T("文件中转", "File transfer"),
            _ => Loc.T("剪贴板与文件中转", "Clipboard and file transfer")
        });
        ShelfPageButton.ToolTip = null;
    }

    private string ShelfSearchName => Settings.ShelfShown switch
    {
        ShelfShown.Clipboard => Loc.T("搜索剪贴板", "Search the clipboard"),
        ShelfShown.Files => Loc.T("搜索文件中转", "Search the file transfer"),
        _ => Loc.T("搜索剪贴板与文件中转", "Search the clipboard and file transfer")
    };

    private void StopDeckHover() => _deckHoverTimer?.Stop();

    private bool CanQuickSwitchShelf => _expanded && !_preview && IsShelfPage && Settings.ShelfStacked()
        && _shelfDetail is null && _shelfEdit is null && !_dropping && _shelfMenu?.IsOpen != true;

    private static bool IsLightColor(Color color) => (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) > 140;
    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount), (byte)Math.Round(from.G + (to.G - from.G) * amount), (byte)Math.Round(from.B + (to.B - from.B) * amount));

    private FrameworkElement StackedColumns(ShelfLayout layout)
    {
        var front = ShelfFront;
        // Built as they stood last, so a change of the card in front (files dragged in, a text to edit) is drawn as the change.
        var shown = _shelfBuiltFront is { } was && _deck is not null && _expanded && !_preview && Animates ? was : front;
        // The cards are opaque, in the theme's surface made a little lighter on a dark theme: more in front than behind. On a light
        // theme the card in front goes toward white and the one behind a little darker. A card behind under the pointer comes
        // closer to the fill of the one in front without reaching it.
        var surface = Look.ColorOf("surface"); surface.A = 255;
        bool light = IsLightColor(surface);
        Color Toward(double white, double black) => light ? Blend(surface, Colors.Black, black) : Blend(surface, Colors.White, white);
        var cardFill = light ? Blend(surface, Colors.White, .55) : Blend(surface, Colors.White, .075);
        Color rest = Toward(.043, .045), lit = Toward(.06, .065), down = Toward(.05, .08);
        var edge = light ? Colors.Black : Colors.White;
        Color cardRim = Color.FromArgb(26, edge.R, edge.G, edge.B), restRim = Color.FromArgb(18, edge.R, edge.G, edge.B);
        Color ink = Look.ColorOf("textSecondary"), inkLit = Look.ColorOf("textPrimary"), faint = Look.ColorOf("textTertiary");

        var host = new Grid();
        var clips = DeckCardFor(ShelfSide.Clipboard, ClipColumn(layout), layout, ink, faint);
        var files = DeckCardFor(ShelfSide.Files, FileColumn(layout), layout, ink, faint);
        host.Children.Add(clips.Root); host.Children.Add(files.Root);
        // What takes the click that brings the card behind forward: its strip and the page's margin beside it, out to the panel's
        // edge and over the whole height of the page.
        var hit = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Width = layout.Peek + layout.CardSide };
        var root = new Grid();
        root.Children.Add(host); root.Children.Add(hit);
        var deck = _deck = new Deck
        {
            Host = host, Clips = clips, Files = files, Hit = hit, CardFill = cardFill, CardRim = cardRim, Rest = rest, RestRim = restRim,
            Lit = lit, Down = down, Ink = ink, InkLit = inkLit, Faint = faint, FaintLit = ink, Front = shown
        };
        _shelfBuiltFront = front;
        WireDeckHit(deck);
        host.SizeChanged += (_, _) => { if (ReferenceEquals(_deck, deck)) PlaceDeck(); };
        PlaceDeck();
        if (shown != front) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (ReferenceEquals(_deck, deck)) SlideDeck(deck, ShelfFront, Animates); });
        return root;
    }

    // A card: its column, and for when it is behind its strip's parts: the column's icon, its name written downward (Chinese
    // upright, one character under another; English turned a quarter clockwise, where the file transfer is "Files") and the
    // number of its entries at the bottom, none when it is empty.
    private DeckCard DeckCardFor(ShelfSide side, FrameworkElement column, ShelfLayout layout, Color ink, Color faint)
    {
        bool left = side == ShelfSide.Clipboard, compact = layout.Tier == ShelfTier.Compact;
        int count = left ? VisibleClips().Count : VisibleFiles().Count;
        string name = left ? Loc.T("剪贴板", "Clipboard") : Loc.T("文件中转", "Files");
        string full = left ? Loc.T("剪贴板", "Clipboard") : Loc.T("文件中转", "File transfer");
        double iconSize = compact ? 15 : 16, nameSize = layout.Tier switch { ShelfTier.Compact => 11.5, ShelfTier.Standard => 12, _ => 12.5 };
        double countSize = layout.Aux, gap = compact ? 5 : 7, top = compact ? 6 : 9, bottom = compact ? 5 : 8;
        var inkBrush = new SolidColorBrush(ink); var faintBrush = new SolidColorBrush(faint);
        // The icon in a fixed box, centered by its box.
        var icon = new Viewbox
        {
            Width = iconSize, Height = iconSize, HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Canvas { Width = 24, Height = 24, Children = { new Path { Data = left ? ClipboardPageIcon : TrayPageIcon, Stroke = inkBrush, StrokeThickness = 1.7,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round } } }
        };
        FrameworkElement label; double length;
        if (name.Any(letter => letter >= 0x2E80))
        {
            double step = nameSize + (compact ? 1 : 2);
            var letters = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            foreach (char letter in name)
                letters.Children.Add(new TextBlock { Text = letter.ToString(), FontFamily = UI.PanelFont, FontSize = nameSize, FontWeight = FontWeights.SemiBold, Foreground = inkBrush,
                    TextAlignment = TextAlignment.Center, Height = step, LineHeight = step, LineStackingStrategy = LineStackingStrategy.BlockLineHeight });
            label = letters; length = step * name.Length;
        }
        else
        {
            label = new TextBlock { Text = name, FontFamily = UI.PanelFont, FontSize = nameSize, FontWeight = FontWeights.SemiBold, Foreground = inkBrush,
                HorizontalAlignment = HorizontalAlignment.Center, LayoutTransform = new RotateTransform(90) };
            length = Math.Ceiling(TextWidth(name, nameSize, FontWeights.SemiBold));
        }
        label.Margin = new Thickness(0, gap, 0, 0); label.VerticalAlignment = VerticalAlignment.Top;
        var number = new TextBlock { Text = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture), FontFamily = UI.PanelFont, FontSize = countSize, Foreground = faintBrush,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, gap, 0, 0) };
        Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
        var parts = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(icon, System.Windows.Controls.Dock.Top); DockPanel.SetDock(number, System.Windows.Controls.Dock.Bottom);
        parts.Children.Add(icon); parts.Children.Add(number); parts.Children.Add(label);
        // The parts stand in the middle of what shows of the card behind: Peek at its outer side, less its own edge.
        var strip = new Border { Width = Math.Max(0, layout.Peek - 1), Padding = new Thickness(0, top, 0, bottom), Child = parts, IsHitTestVisible = false,
            HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right };
        // The column keeps the width it has in front and stands against the card's left edge; a narrower card cuts it off.
        var body = new Border { Padding = new Thickness(layout.CardPad, 2, layout.CardPad, 4), Child = column, HorizontalAlignment = HorizontalAlignment.Left };
        // The card clips what it holds, and text under a clip is drawn in grayscale in this layered window (MainWindow.xaml:
        // IslandContent). The card's fill beneath is opaque, so ClearType is safe: the text looks as it does side by side.
        RenderOptions.SetClearTypeHint(body, ClearTypeHint.Enabled); RenderOptions.SetClearTypeHint(strip, ClearTypeHint.Enabled);
        var fill = new SolidColorBrush(); var rim = new SolidColorBrush();
        var card = new Border
        {
            CornerRadius = new CornerRadius(Look.Corner(8)), Background = fill, BorderBrush = rim, BorderThickness = new Thickness(1),
            HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Child = new Grid { ClipToBounds = true, Children = { body, strip } }
        };
        AutomationProperties.SetName(strip, Loc.T($"切换到{full}", $"Switch to {full}"));
        return new DeckCard
        {
            Side = side, Root = card, Body = body, Edge = strip, Icon = icon, Count = number, Fill = fill, Rim = rim, Ink = inkBrush, Faint = faintBrush,
            Padding = top + bottom + 2, Gap = gap, IconSize = iconSize, NameLength = length, CountSize = Math.Ceiling(countSize * 1.35), Counted = count > 0
        };
    }

    // The card behind under the pointer is a little lighter and its icon and name go to the primary text color; pressed, its fill
    // goes back down.
    private void ShowStrip(Deck deck, DeckCard card, bool lit, bool down = false)
    {
        Tone(card.Fill, down ? deck.Down : lit ? deck.Lit : deck.Rest, 120);
        Tone(card.Ink, lit ? deck.InkLit : deck.Ink, 120);
        Tone(card.Faint, lit ? deck.FaintLit : deck.Faint, 120);
    }
    private void Tone(SolidColorBrush brush, Color to, double milliseconds)
    {
        if (Animates) brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = DeckFade });
        else { brush.BeginAnimation(SolidColorBrush.ColorProperty, null); brush.Color = to; }
    }

    private void WireDeckHit(Deck deck)
    {
        var hit = deck.Hit;
        bool pressed = false;
        bool Live() => ReferenceEquals(_deck, deck) && !_dropping && _shelfEdit is null;
        ShelfSide Other() => deck.Front == ShelfSide.Clipboard ? ShelfSide.Files : ShelfSide.Clipboard;
        hit.MouseEnter += (_, _) =>
        {
            if (!Live()) return;
            ShowStrip(deck, deck.Behind, true);
            if (!Settings.ShelfHoverSwitch || Environment.TickCount64 < _deckSwapCooldownUntil) return;
            StopDeckHover();
            var timer = _deckHoverTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(160) };
            EventHandler? tick = null;
            tick = (_, _) =>
            {
                timer.Tick -= tick; timer.Stop();
                if (ReferenceEquals(_deckHoverTimer, timer) && Live() && hit.IsMouseOver && Settings.ShelfHoverSwitch && _expanded && !_preview) BringForward(Other());
            };
            timer.Tick += tick; timer.Start();
        };
        hit.MouseLeave += (_, _) => { pressed = false; StopDeckHover(); if (ReferenceEquals(_deck, deck)) ShowStrip(deck, deck.Behind, false); };
        hit.MouseLeftButtonDown += (_, e) =>
        {
            if (!Live()) return;
            e.Handled = true; pressed = true; StopDeckHover();
            ShowStrip(deck, deck.Behind, true, down: true);
        };
        hit.MouseLeftButtonUp += (_, e) =>
        {
            if (!pressed) return;
            e.Handled = true; pressed = false;
            if (Live()) BringForward(Other());
        };
        hit.MouseRightButtonUp += (_, e) => e.Handled = true;
    }

    // The title of the card in front, stacked: a click on it brings the other card forward. Nothing about it is drawn differently.
    private void SwitchTitle(TextBlock title, ShelfSide side)
    {
        string other = side == ShelfSide.Clipboard ? Loc.T("文件中转", "File transfer") : Loc.T("剪贴板", "Clipboard");
        title.Cursor = Cursors.Hand;
        AutomationProperties.SetName(title, Loc.T($"切换到{other}", $"Switch to {other}"));
        title.MouseLeftButtonDown += (_, e) => e.Handled = true;
        title.MouseLeftButtonUp += (_, e) => { e.Handled = true; BringForward(side == ShelfSide.Clipboard ? ShelfSide.Files : ShelfSide.Clipboard); };
    }

    private void BringForward(ShelfSide side)
    {
        StopDeckHover();
        if (ShelfFront == side) return;
        _deckSwapCooldownUntil = Environment.TickCount64 + 300;
        CloseMenu();
        _shelfFront = side; _picked.Clear();
        UpdateShelfPageButton();
        if (_deck is { } deck && _expanded && !_preview) { _shelfBuiltFront = side; SlideDeck(deck, side, Animates); }
        else RenderShelf();
    }

    // ---- Quick-switching input gestures (middle-click, horizontal touchpad swipe, Shift + wheel) ----
    private bool IsOverShelfTextInput(DependencyObject? source)
    {
        for (var at = source; at is not null; at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
            if (at is TextBoxBase) return true;
        return false;
    }

    private void ShelfPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !CanQuickSwitchShelf || !Settings.ShelfMiddleSwitch) return;
        if (e.OriginalSource is DependencyObject source && (IsOverShelfTextInput(source) || Toolbar.IsAncestorOf(source))) return;
        e.Handled = true;
        BringForward(ShelfFront == ShelfSide.Clipboard ? ShelfSide.Files : ShelfSide.Clipboard);
    }

    private void ShelfPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !CanQuickSwitchShelf || !Settings.ShelfMiddleSwitch) return;
        if (e.OriginalSource is DependencyObject source && (IsOverShelfTextInput(source) || Toolbar.IsAncestorOf(source))) return;
        e.Handled = true;
    }

    private bool ShelfWheelSwipe(int delta)
    {
        if (!CanQuickSwitchShelf || !Settings.ShelfSwipeSwitch || !ScreenBounds(Island).Contains(WindowsHost.Cursor())) return false;
        int step = _shelfSwipe.Feed(delta, DateTimeOffset.UtcNow);
        if (step > 0) BringForward(ShelfSide.Files);
        else if (step < 0) BringForward(ShelfSide.Clipboard);
        return true;
    }

    private void ShelfPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 || PasteInput.KeyDown(0x10);
        if (!shift || !CanQuickSwitchShelf || !Settings.ShelfSwipeSwitch) return;
        e.Handled = true;
        long now = Environment.TickCount64;
        if (now - _shelfShiftWheelAt > 300 || Math.Sign(e.Delta) != Math.Sign(_shelfShiftWheelRest)) _shelfShiftWheelRest = 0;
        _shelfShiftWheelAt = now;
        _shelfShiftWheelRest += e.Delta;
        if (Math.Abs(_shelfShiftWheelRest) < 60) return;
        int dir = Math.Sign(_shelfShiftWheelRest);
        _shelfShiftWheelRest = 0;
        if (now < _shelfShiftWheelCooldownUntil) return;
        _shelfShiftWheelCooldownUntil = now + 200;
        BringForward(dir < 0 ? ShelfSide.Files : ShelfSide.Clipboard);
    }

    // Puts the clickable area where the card in front calls for, gives the columns their size, shows on each strip what its height
    // has room for and, unless a change is under way, draws both cards as they stand with that card in front.
    private void PlaceDeck()
    {
        if (_deck is not { } deck) return;
        var layout = _shelfLayout;
        double full = deck.Host.ActualWidth > 0 ? deck.Host.ActualWidth : Math.Max(0, ContentCanvas.Width - 2 * layout.CardSide), height = deck.Host.ActualHeight;
        var (clipWidth, fileWidth) = ShelfDeck.Widths(deck.Front, full, layout.Peek);
        double wide = Math.Max(clipWidth, fileWidth);
        foreach (var card in new[] { deck.Clips, deck.Files })
        {
            bool front = card.Side == deck.Front;
            // The column keeps one size whatever its card's, so it is not laid out again while the card changes.
            card.Body.Width = Math.Max(0, wide - 2);
            card.Body.Height = height > 0 ? Math.Max(0, height - 2) : double.NaN;
            card.Body.VerticalAlignment = height > 0 ? VerticalAlignment.Top : VerticalAlignment.Stretch;
            card.Body.IsHitTestVisible = front;
            Panel.SetZIndex(card.Root, front ? 1 : 0);
            if (height > 0)
            {
                var (icon, count) = ShelfDeck.StripParts(height - 2 * ShelfDeck.Inset, card.Padding, card.Gap, card.IconSize, card.NameLength, card.CountSize);
                card.Icon.Visibility = icon ? Visibility.Visible : Visibility.Collapsed;
                card.Count.Visibility = count && card.Counted ? Visibility.Visible : Visibility.Collapsed;
            }
            if (deck.Moving) continue;
            card.Root.BeginAnimation(FrameworkElement.WidthProperty, null); card.Root.BeginAnimation(FrameworkElement.MarginProperty, null);
            card.Fill.BeginAnimation(SolidColorBrush.ColorProperty, null); card.Rim.BeginAnimation(SolidColorBrush.ColorProperty, null);
            card.Body.BeginAnimation(UIElement.OpacityProperty, null); card.Edge.BeginAnimation(UIElement.OpacityProperty, null);
            card.Root.Width = card.Side == ShelfSide.Clipboard ? clipWidth : fileWidth;
            card.Root.Margin = new Thickness(0, front ? 0 : ShelfDeck.Inset, 0, front ? 0 : ShelfDeck.Inset);
            card.Fill.Color = front ? deck.CardFill : deck.Rest; card.Rim.Color = front ? deck.CardRim : deck.RestRim;
            card.Body.Opacity = front ? 1 : 0; card.Edge.Opacity = front ? 0 : 1;
        }
        bool right = deck.Front == ShelfSide.Clipboard;
        deck.Hit.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        deck.Hit.Margin = new Thickness(right ? 0 : -layout.CardSide, -layout.CardTop, right ? -layout.CardSide : 0, -layout.CardBottom);
        AutomationProperties.SetName(deck.Hit, AutomationProperties.GetName(deck.Behind.Edge));
    }

    // Brings `front` forward, from however the cards stand (a change under way turns back from there), or at once. As in the
    // design: the cards' widths and margins change in SwapSeconds, their fills and edges in ToneSeconds, and in each card the
    // column and the strip's parts trade places in FadeSeconds, the one fading out as the other fades in.
    private void SlideDeck(Deck deck, ShelfSide front, bool animate)
    {
        deck.Front = front;
        int slide = ++deck.Slide;
        if (!animate || !_expanded || _preview) { deck.Moving = false; PlaceDeck(); return; }
        deck.Moving = true;
        PlaceDeck();
        double full = deck.Host.ActualWidth > 0 ? deck.Host.ActualWidth : Math.Max(0, ContentCanvas.Width - 2 * _shelfLayout.CardSide);
        var (clipWidth, fileWidth) = ShelfDeck.Widths(front, full, _shelfLayout.Peek);
        TimeSpan swap = TimeSpan.FromSeconds(ShelfDeck.SwapSeconds), fade = TimeSpan.FromSeconds(ShelfDeck.FadeSeconds);
        foreach (var card in new[] { deck.Clips, deck.Files })
        {
            bool ahead = card.Side == front;
            var width = new DoubleAnimation(card.Side == ShelfSide.Clipboard ? clipWidth : fileWidth, swap) { EasingFunction = DeckEase };
            if (ahead) width.Completed += (_, _) =>
            {
                if (!ReferenceEquals(_deck, deck) || deck.Slide != slide) return;
                deck.Moving = false; PlaceDeck();
                if (deck.Hit.IsMouseOver) ShowStrip(deck, deck.Behind, true);
            };
            card.Root.BeginAnimation(FrameworkElement.WidthProperty, width);
            card.Root.BeginAnimation(FrameworkElement.MarginProperty, new ThicknessAnimation(new Thickness(0, ahead ? 0 : ShelfDeck.Inset, 0, ahead ? 0 : ShelfDeck.Inset), swap) { EasingFunction = DeckEase });
            Tone(card.Fill, ahead ? deck.CardFill : deck.Rest, ShelfDeck.ToneSeconds * 1000); Tone(card.Rim, ahead ? deck.CardRim : deck.RestRim, ShelfDeck.ToneSeconds * 1000);
            Tone(card.Ink, deck.Ink, 120); Tone(card.Faint, deck.Faint, 120);
            card.Body.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(ahead ? 1 : 0, fade) { EasingFunction = DeckFade });
            card.Edge.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(ahead ? 0 : 1, fade) { EasingFunction = DeckFade });
        }
    }
}
