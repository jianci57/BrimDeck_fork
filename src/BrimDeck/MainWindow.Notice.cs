using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// A system notification shown on the island itself (MainWindow.xaml's NotificationOverlay): foreground sharing the
// island's background opacity, replacing the collapsed row or open panel for five seconds, then giving the surface back exactly
// as it was. No new window, no desktop toast, no focus, no launch: the card draws over the island's own content and
// its close button or the Escape hotkey ends it early.
//
// The card owns the surface while it shows. SetExpanded is intercepted (its first line) and calls LayoutSystemNotice
// instead, so the hover timers, a screen-context change and ApplySettings re-lay the card and never fight its layout;
// _expanded, the host's fronting, the user's page and the Escape hotkey state are left exactly as they were. While the
// card shows, the compact row and open page retain their state and animations in the hidden IslandBaseContent wrapper.
// The overlay draws foreground only; IslandBorder remains the sole background painter.
//
// External symbols this file reads from Core (implemented with the settings side; see the Planner's call notes):
//   enum NotificationContentMode { Brief, Compact, Full }  — BrimDeck.Core
//   DeckSettings.NotificationsEnabled (bool)               — the switch; ApplyNoticeSettings clears on it
//   DeckSettings.NotificationContentMode                   — the card's content mode
public partial class MainWindow
{
    // The notice on show, or null. A replacement arrives through ShowSystemNotification and re-lays the card.
    private NotificationItem? _notice;
    // The card's surface in the edge's frame, like Collapsed.Surface (X along the edge, Y across it); set by
    // ApplyNoticeGeometry, read by HoverZoneOnStage so pointer sensing follows what is drawn.
    private DipRect _noticeSurface;
    private double _noticeCardW, _noticeCardH;
    // What the card was built for; a new item, mode, shape, theme, scale or orientation rebuilds it. Geometry is
    // reapplied on every interception; the content is rebuilt only when this key changes.
    private (NotificationItem Item, NotificationContentMode Mode, CollapsedShape Shape, double Dpi, int Theme, double Scale, bool Across)? _noticeRendered;
    // Five seconds, as the quota alert's timer; a replacement restarts it.
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _noticeWired;
    // A keyboard expand or collapse pressed while the card shows is remembered and applied when it clears, so the
    // five seconds never swallow the hotkeys' intent; pointer-driven calls only re-lay the card.
    private bool? _noticeDeferredExpand;

    private void EnsureNoticeWired()
    {
        if (_noticeWired) return;
        _noticeWired = true;
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); ClearSystemNotice(); };
    }

    // A real notification: App broadcasts one call per deck after its own switch check, so the switch is not read
    // here — the settings page previews a test card through the same path with the background service off.
    internal void ShowSystemNotification(NotificationItem item) => ShowNotice(item);
    // The settings page's test card. Same display, same five seconds; IsSuppressed and PowerOff still hold it back.
    internal void ShowSystemNotificationPreview(NotificationItem item) => ShowNotice(item);

    private void ShowNotice(NotificationItem item)
    {
        if (_poweringOff || _retiring) return;
        // A screen-context rule or a user-hidden panel cannot show the card; a hidden window (an edge move, the
        // tray) drops it outright — nothing is kept to pop up later, and an older card is cleared too.
        if (IsSuppressed || !IsVisible) { StopSystemNotification(); return; }
        _notice = item;
        _noticeRendered = null;
        EnsureNoticeWired();
        _noticeTimer.Stop(); _noticeTimer.Start();
        LayoutSystemNotice();
    }

    // Stops the notice without laying anything out. PowerOff calls it before its own collapse, ApplyNoticeSettings
    // when the switch goes off or the panel is suppressed — whoever continues then lays the surface out itself.
    internal void StopSystemNotification()
    {
        _noticeTimer.Stop();
        _notice = null; _noticeRendered = null; _noticeDeferredExpand = null;
        _noticeSurface = default; _noticeCardW = 0; _noticeCardH = 0;
        NotificationOverlay.Visibility = Visibility.Collapsed;
        NotificationOverlay.Children.Clear();
        IslandBaseContent.Visibility = Visibility.Visible;
        // The Escape hotkey belonged to the card while it showed; give it back to the panel's own state at once, so
        // a stop without a following layout (the switch going off) does not leave Esc captured.
        _escapeDismissal?.SetEnabled(_expanded && !_preview || _moveArmed);
    }

    // Ends the notice and gives the surface back exactly as it was: SetExpanded re-lays the collapsed shape or the
    // open panel from its own state, which restores the settled clip, the shoulders, the corner radius, the surface
    // tone, the material and shadow opacities, the hover strip's hit testing and the Escape hotkey. A keyboard expand
    // or collapse pressed meanwhile is applied here. A pointer that left while the panel was open is handled by the
    // ordinary poll (TrackPointer sees it outside, PointerChanged starts the leave timer, CheckPointerExit closes),
    // so the restore itself never second-guesses the state it is giving back.
    internal void ClearSystemNotification() => ClearSystemNotice();

    internal void ClearSystemNotice()
    {
        if (_notice is null) return;
        // A keyboard expand or collapse pressed while the card showed is read before anything clears it.
        bool expand = _noticeDeferredExpand ?? _expanded;
        StopSystemNotification();
        _noticeDeferredExpand = null;
        if (PresentationSource.FromVisual(this) is null || _poweringOff || _retiring) return;
        // What the interception stood down is given back here in SetExpanded's own values for the state that returns
        // (a collapsed surface must not be hit-testable: its strip would catch clicks for the layered window).
        ExpandedContent.IsHitTestVisible = _expanded;
        HoverExtension.IsHitTestVisible = _expanded;
        HoverExtension.Visibility = _expanded ? Visibility.Visible : Visibility.Hidden;
        // The restore is laid out in place (instant): every size, margin and corner is set directly from the state,
        // so no frame of the notice's SetImmediate geometry can leak into an animated start value.
        SetExpanded(expand, instant: true, keyboard: expand != _expanded);
    }

    // Called by ApplySettings: the switch, the mode, a theme or a shape change all pass through here. Off, suppressed
    // or powering off clears the card at once (no restore — ApplySettings lays the panel out next); showing re-lays it.
    private void ApplyNoticeSettings()
    {
        if (_notice is null) return;
        if (!Settings.NotificationsEnabled || IsSuppressed || _poweringOff || _retiring) { StopSystemNotification(); return; }
        LayoutSystemNotice();
    }

    // The notice owns the surface while it shows: SetExpanded is intercepted and calls this, so every hover tick,
    // screen-context change and ApplySettings lands here instead of fighting the card. The geometry is reapplied on
    // every call; the content is rebuilt only when its key changes. The boot's jumping dot and a surface drag keep
    // the card pending (not cleared): they lay the surface out themselves and the interception keeps them safe.
    private void LayoutSystemNotice()
    {
        if (_notice is not { } notice) return;
        if (_booting || _bootDot || _bootWaiting || _moveOverlay is not null) return;
        if (_poweringOff || _retiring || IsSuppressed || PresentationSource.FromVisual(Stage) is null) { StopSystemNotification(); return; }
        if (!IsVisible) return;
        Collapsed = ShapeFor(EffectiveStyle);
        var shape = Collapsed;
        double scale = CompactMusicLayout.SafeScale(shape.Scale);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var mode = Settings.NotificationContentMode;
        // The layered window is already as large as anything the panel can show, so the card clamps into it and the
        // window is never resized mid-notice (Windows would show the old picture at the new size for a frame). Along
        // the edge the resize grips' room is kept; across it the 24 DIP that always stay under the surface for the shadow.
        double maxAlong = Math.Max(60, FrameWidth - 2 * ResizeGrips.Room);
        double maxAcross = Math.Max(40, FrameHeight - shape.Surface.Y - 24);
        // The card lays out in IslandContent's upright space. On the top and bottom edges its width runs along the
        // edge; on a side edge the upright transform turns the column upright, so the card's width runs across. The
        // limits follow the same mapping, and so does the island sizing in ApplyNoticeGeometry.
        double widthLimit = _frame.Across ? maxAcross : maxAlong;
        double heightLimit = _frame.Across ? maxAlong : maxAcross;
        var key = (notice, mode, shape, dpi, PanelTheme.Generation, scale, _frame.Across);
        if (_noticeRendered is not { } done || !done.Equals(key))
        {
            // The card keeps clear of the surface's own curves with its padding: the notch's shoulders and bottom
            // corners, a capsule's rounding, the column's pads.
            double padH = Math.Max(Math.Max(shape.Shoulder, CollapsedLimits.ColumnPad), 4) + 12;
            double padTop = shape.TopRadius > 0 ? Math.Min(shape.TopRadius * .6 + 2, 14) : 4;
            double padBottom = shape.BottomRadius > 0 ? Math.Max(6, shape.BottomRadius * .6 + 2) : 6;
            var card = BuildNoticeCard(notice, mode, scale, padH, padTop, padBottom, () => ClearSystemNotice(), widthLimit, out double wanted, out var body);
            double minW = Math.Min(220, widthLimit), capW = Math.Min(460, widthLimit);
            double cardW = Math.Clamp(Math.Max(wanted, minW), minW, capW);
            card.Width = cardW;
            card.Measure(new Size(cardW, double.PositiveInfinity));
            double cardH = card.DesiredSize.Height;
            // The body gives way first when the room is short (a line-style surface, a short monitor): one or two
            // lines come off before anything else is cut.
            for (int shrink = 0; cardH > heightLimit && body is not null && shrink < 2; shrink++)
            {
                if (!double.IsFinite(body.MaxHeight) || body.MaxHeight <= 16 * scale) break;
                body.MaxHeight = Math.Max(16 * scale, body.MaxHeight - (cardH - heightLimit) - 16 * scale * .5);
                card.Measure(new Size(cardW, double.PositiveInfinity));
                cardH = card.DesiredSize.Height;
            }
            cardH = Math.Min(cardH, heightLimit);
            card.CornerRadius = new CornerRadius(shape.TopRadius, shape.TopRadius, shape.BottomRadius, shape.BottomRadius);
            NotificationOverlay.Children.Clear();
            NotificationOverlay.Children.Add(card);
            _noticeRendered = key; _noticeCardW = cardW; _noticeCardH = cardH;
        }
        ApplyNoticeGeometry(shape);
    }

    // Sizes and places the island for the card, from the shape as it is now. The island's Width runs along the edge
    // and its Height across it on every edge (SetExpanded does the same for the collapsed shape); on a side edge the
    // upright transform of IslandContent turns the card, so its upright width is the island's cross-section there.
    private void ApplyNoticeGeometry(CollapsedShape shape)
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double cardW = _noticeCardW, cardH = _noticeCardH;
        double islandAlong = _frame.Across ? cardH : cardW;
        double islandAcross = _frame.Across ? cardW : cardH;
        islandAlong = CollapsedGeometry.SnapToDevicePixels(Math.Min(islandAlong, Math.Max(40, MonitorBoundsDip.Width)), dpi);
        islandAcross = CollapsedGeometry.SnapToDevicePixels(islandAcross, dpi);
        double centre = shape.Surface.X + shape.Surface.Width / 2;
        double x = Math.Clamp(centre - islandAlong / 2, 0, Math.Max(0, MonitorBoundsDip.Width - islandAlong));
        // The card also stays within the window's frame, which is centred on the user's surface.
        x = Math.Clamp(x, _windowOffset, Math.Max(_windowOffset, _windowOffset + FrameWidth - islandAlong));
        x = CollapsedGeometry.SnapToDevicePixels(x, dpi);
        double y = shape.Surface.Y;
        _noticeSurface = new DipRect(x, y, islandAlong, islandAcross);
        IslandBaseContent.Visibility = Visibility.Hidden;
        NotificationOverlay.Visibility = Visibility.Visible;
        SetImmediate(HoverZone, MarginProperty, new Thickness(x - _windowOffset, 0, 0, 0));
        SetImmediate(Island, MarginProperty, new Thickness(0, y, 0, 0));
        SetImmediate(Island, WidthProperty, islandAlong);
        SetImmediate(Island, HeightProperty, islandAcross);
        SetImmediate(Island, Border.CornerRadiusProperty, new CornerRadius(shape.TopRadius, shape.TopRadius, shape.BottomRadius, shape.BottomRadius));
        Island.ShoulderRadius = shape.Shoulder;
        Island.MaterialOpacity = 1;
        // The card keeps clear of the corners with its own padding; the settled rectangle clip comes back with the restore.
        Island.SettledClip = false;
        SetImmediate(_surface, SolidColorBrush.ColorProperty, PanelTheme.ColorOf("surface"));
        SetImmediate(ShadowHost, OpacityProperty, 1d);
        // The hover strip and the open page stand down; SetExpanded restores both when the notice clears.
        HoverExtension.IsHitTestVisible = false;
        HoverExtension.Visibility = Visibility.Hidden;
        ExpandedContent.IsHitTestVisible = false;
        // The Escape hotkey covers the card while it shows; the restore re-arms the panel's own state (SetExpanded).
        _escapeDismissal?.SetEnabled(true);
        UpdatePointerTracking();
    }

    // Builds the card in IslandContent's upright space, following the reference toast's three content modes
    // (NotchPeninsula Renderer.Toast.cs): Brief shows the title and the body; Compact adds the right-hand column
    // with "now" over the app's name beside them; Full puts the app's name and "now" on the top row above the title
    // and a longer body. The app's name falls back to "Unknown app" (the reader's AppInfo often cannot give one),
    // the title to the app's name, and the body is capped to a few lines with an ellipsis. Colors follow the panel
    // theme; the bell is a local vector, not a real app icon.
    internal static Border BuildNoticeCard(NotificationItem item, NotificationContentMode mode, double scale,
        double padH, double padTop, double padBottom, Action close, double maxWidth, out double wanted, out TextBlock? body)
    {
        body = null;
        double Whole(double v) => CompactMusicLayout.Whole(v);
        double metaSize = Whole(Math.Max(9, 10.5 * scale)), metaLine = Whole(Math.Max(12, 14 * scale));
        double titleSize = Whole(Math.Max(10, 12.5 * scale)), titleLine = Whole(Math.Max(14, 17 * scale));
        double bodySize = Whole(Math.Max(9.5, 11.5 * scale)), bodyLine = Whole(Math.Max(12, 16 * scale));
        double gap = Whole(Math.Max(3, 4 * scale));
        double button = Whole(Math.Max(16, 20 * scale));
        bool full = mode == NotificationContentMode.Full, brief = mode == NotificationContentMode.Brief;
        string app = string.IsNullOrWhiteSpace(item.AppName) ? Loc.T("未知应用", "Unknown app") : item.AppName.Trim();
        string title = string.IsNullOrWhiteSpace(item.Title) ? app : item.Title.Trim();
        string text = item.Body ?? "";
        bool hasBody = text.Length > 0;
        int bodyLines = full ? 4 : 2;

        var titleBlock = UI.Fixed(title, titleSize, titleLine, UI.Primary, FontWeights.SemiBold);
        var bell = Bell(scale);
        var nowBlock = UI.Fixed(Loc.T("现在", "Now"), metaSize, metaLine, UI.Tertiary);
        var appBlock = UI.Fixed(app, metaSize, metaLine, full ? UI.Secondary : UI.Tertiary);

        double Natural(FrameworkElement element)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Ceiling(element.DesiredSize.Width);
        }
        double textWidth = Natural(titleBlock);
        double rightWidth = 0;
        if (full)
        {
            // The app's name and "now" share the top row and count once toward the wanted width.
            textWidth = Math.Max(textWidth, Natural(appBlock) + gap + Natural(nowBlock));
        }
        else if (!brief)
        {
            // "Now" over the app's name beside the text; the column is capped so a long name never squeezes the
            // title and the body away — the name trims with an ellipsis instead.
            rightWidth = Math.Min(100 * scale, Math.Max(Natural(nowBlock), Natural(appBlock)));
        }

        if (hasBody)
        {
            body = UI.Text(text, bodySize, UI.Secondary);
            body.TextWrapping = TextWrapping.Wrap;
            body.LineHeight = bodyLine; body.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            body.MaxHeight = bodyLine * bodyLines;
            body.TextTrimming = TextTrimming.CharacterEllipsis;
            // A short title with a long body still gets a wide card: the body counts toward the wanted width,
            // capped so it wraps into lines rather than stretching the card.
            body.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            textWidth = Math.Max(textWidth, Math.Min(Math.Ceiling(body.DesiredSize.Width), 380 * scale));
        }
        wanted = Math.Min(maxWidth, padH * 2 + bell.Width + gap + textWidth + (rightWidth > 0 ? gap + rightWidth : 0) + gap + button);

        var closeButton = new Button
        {
            Width = button, Height = button, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), Focusable = false, Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            Content = new TextBlock
            {
                Text = "\uE711", FontFamily = Application.Current?.TryFindResource("FontIconMdl2") as FontFamily ?? new FontFamily("Segoe MDL2 Assets"),
                FontSize = Math.Max(8, 9 * scale), Foreground = UI.Brush(UI.Tertiary),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        if (closeButton.Content is TextBlock glyph)
        {
            closeButton.MouseEnter += (_, _) => glyph.Foreground = UI.Brush(UI.Primary);
            closeButton.MouseLeave += (_, _) => glyph.Foreground = UI.Brush(UI.Tertiary);
        }
        closeButton.Click += (_, _) => close();

        var grid = new Grid();
        void Add(FrameworkElement element, int column, int row, int columnSpan = 1, int rowSpan = 1)
        {
            if (columnSpan > 1) Grid.SetColumnSpan(element, columnSpan);
            if (rowSpan > 1) Grid.SetRowSpan(element, rowSpan);
            Grid.SetColumn(element, column); Grid.SetRow(element, row);
            grid.Children.Add(element);
        }
        // Columns: bell, text, (the compact right column), close. Rows follow the mode.
        int closeColumn = brief ? 2 : full ? 2 : 3, rows = full ? 3 : 2;
        for (int c = 0; c <= closeColumn; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 1 ? new GridLength(1, GridUnitType.Star) : new GridLength(1, GridUnitType.Auto) });
        for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

        Add(bell, 0, 0, 1, rows);
        Add(closeButton, closeColumn, 0, 1, rows);
        if (full)
        {
            // The app's name and "now" share the top row; the title and the longer body follow.
            var topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            Grid.SetColumn(appBlock, 0); topRow.Children.Add(appBlock);
            Grid.SetColumn(nowBlock, 1); nowBlock.HorizontalAlignment = HorizontalAlignment.Right; topRow.Children.Add(nowBlock);
            Add(topRow, 1, 0);
            Add(titleBlock, 1, 1);
            if (body is not null) Add(body, 1, 2);
        }
        else if (!brief)
        {
            // "Now" over the app's name in a right-hand column, beside the title and the body.
            var right = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
            double rightW = rightWidth;
            nowBlock.Width = rightW; appBlock.Width = rightW;
            nowBlock.TextAlignment = TextAlignment.Right; appBlock.TextAlignment = TextAlignment.Right;
            nowBlock.HorizontalAlignment = HorizontalAlignment.Right; appBlock.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(nowBlock); right.Children.Add(appBlock);
            Add(right, 2, 0, 1, rows);
            Add(titleBlock, 1, 0);
            if (body is not null) Add(body, 1, 1);
        }
        else
        {
            Add(titleBlock, 1, 0);
            if (body is not null) Add(body, 1, 1);
        }

        return new Border { Background = Brushes.Transparent, Padding = new Thickness(padH, padTop, padH, padBottom), Child = grid, SnapsToDevicePixels = true };
    }

    // The local bell: the toolbar icons' stroke style, no real app icon (the reader's AppInfo cannot always give one).
    private static FrameworkElement Bell(double scale)
    {
        double size = Math.Max(12, 15 * scale);
        return new Viewbox
        {
            Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0),
            Child = new Canvas
            {
                Width = 24, Height = 24,
                Children =
                {
                    new Path
                    {
                        Data = Geometry.Parse("M 12,3.2 A 4.8,4.8 0 0 1 16.8,8 C 16.8,10.9 17.8,12.5 18.9,14 L 5.1,14 C 6.2,12.5 7.2,10.9 7.2,8 A 4.8,4.8 0 0 1 12,3.2 Z M 9.7,17.2 A 2.3,2.3 0 0 0 14.3,17.2"),
                        Stroke = UI.Brush(UI.Secondary), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
                    }
                }
            }
        };
    }
}
