using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The devices page, set out by DevicesLayout: a ring per device whose arc is the charge, with the device's icon inside, and under it
// the figure, the name and a line (the earbuds' parts, charging, low, no battery reported, or where the reading comes from); one
// device alone, its ring with the figure, name and kind beside it; many, a line each in columns that scroll. The ring is in the
// text color, in the collapsed readings' warning or critical color when getting low or low, and green (Charging) with a bolt at
// its lower right while charging; the figure and the words say the same, so color is never the only sign.
// As the monitor page does, it draws itself in one element: a new reading changes what it draws, never the layout. Within a
// layout step a device that arrives fades in where it belongs while the others glide aside, one that leaves fades while they
// close up, and a ring eases to each reading (a first reading grows from empty); figures change in place, never cross-faded. The
// empty page and the search before the first listing fade in and out. The page is built again (and crossfades,
// MonitorCrossfade) only when its layout step changes.
internal sealed class DevicesView : FrameworkElement
{
    private readonly DevicesLayout _layout;
    private readonly DeviceInks _inks;
    private sealed class Card
    {
        public required string Key;
        public required DeviceBattery Device;
        public Eased Presence, X, Y, Fill;
        public double Width, Height;
        public bool Placed, Leaving;
        // The texts as last laid out, kept while their text, width and color stay the same.
        public (string Text, double Width, string Color, double Size) NameKey, DetailKey;
        public FormattedText? Name, Detail;
        // Whether either text was cut to its width: the tooltip shows the whole of the one cut, and only then.
        public bool NameCut, DetailCut;
        public string FullName = "", FullDetail = "";
        public bool Cut => NameCut || DetailCut;
        public string Whole => NameCut && DetailCut ? FullName + "\n" + FullDetail : NameCut ? FullName : FullDetail;
    }
    private readonly List<Card> _cards = [];
    private bool _settled, _updated;
    private Eased _empty, _looking;
    private string? _hovered;
    // The list's scroll offset, from its top.
    private double _scroll;
    // One tooltip, opened by the page itself under the device the pointer rests on, a moment after it comes there: the page is one
    // element, so the tooltip service, which opens a tooltip as the pointer enters an element, would not follow it from device to
    // device. It names the device in full (a long name is cut on the page) and follows its readings while it is open.
    private readonly System.Windows.Controls.ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
    private readonly System.Windows.Threading.DispatcherTimer _tipDelay = new() { Interval = TimeSpan.FromMilliseconds(450) };

    public DevicesView(DevicesLayout layout, DeviceInks inks)
    {
        _layout = layout; _inks = inks;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        MouseMove += (_, e) => Hover(e.GetPosition(this));
        MouseLeave += (_, _) => Hover(null);
        _tip.PlacementTarget = this;
        _tipDelay.Tick += (_, _) => { _tipDelay.Stop(); OpenToolTip(); };
        Unloaded += (_, _) => { _tipDelay.Stop(); _tip.IsOpen = false; };
        SizeChanged += (_, _) => Place(motion: false);
    }

    private IEnumerable<Card> Shown => _cards.Where(card => !card.Leaving);
    private int ShownCount => _cards.Count(card => !card.Leaving);

    // The devices to show; settled: the sources have listed what is connected, so an empty list means there are none. The first
    // call (the page being built) shows them in place; later ones move.
    public void Update(IReadOnlyList<DeviceBattery> devices, bool settled, bool motion)
    {
        var current = Shown.ToList();
        // Asked again with what it shows (a resize within the step, a settings change elsewhere): nothing to do.
        if (_updated && settled == _settled && current.Count == devices.Count && devices.Select((device, i) => current[i].Device.Same(device)).All(same => same))
            return;
        bool animate = motion && _updated;
        _updated = true; _settled = settled;
        double now = Eased.Now;
        var keys = devices.Select(device => device.Key).ToHashSet();
        foreach (var card in _cards) if (!card.Leaving && !keys.Contains(card.Key)) { card.Leaving = true; card.Presence.Set(0, animate, now); }
        foreach (var device in devices) Take(device, now, animate);
        foreach (var card in Shown) card.Fill.Set((card.Device.Lowest ?? 0) / 100.0, animate, now);
        // The search and the empty page never show together: the search goes at once when the list settles, and the empty page
        // fades in (OnRender holds it back while the last card fades out).
        _looking.Set(!settled && devices.Count == 0 ? 1 : 0, animate && !settled, now);
        _empty.Set(settled && devices.Count == 0 ? 1 : 0, animate, now);
        // Without motion a leaving card is gone at once.
        if (!animate) _cards.RemoveAll(card => card.Leaving);
        // Leaving cards first, so they are painted under the cards gliding into their places; the others in the list's order.
        var order = devices.Select((device, i) => (device.Key, i)).ToDictionary(entry => entry.Key, entry => entry.i);
        _cards.Sort((a, b) => a.Leaving != b.Leaving ? b.Leaving.CompareTo(a.Leaving) : (order.GetValueOrDefault(a.Key, -1)).CompareTo(order.GetValueOrDefault(b.Key, -1)));
        Place(animate);
        if (_hovered is not null) RefreshToolTip();
    }

    // The card for a device: the one shown (or still leaving, which comes back), or a new one that fades in.
    private void Take(DeviceBattery device, double now, bool animate)
    {
        var card = _cards.FirstOrDefault(card => card.Key == device.Key);
        if (card is null)
        {
            card = new Card { Key = device.Key, Device = device, Presence = new Eased(animate ? 0 : 1, Eased.Seconds * .8), Fill = new Eased(0) };
            _cards.Add(card);
        }
        card.Device = device;
        card.Leaving = false;
        card.Presence.Set(1, animate, now);
    }

    // The shown cards' places; a card that arrives takes its place at once (it fades in there), the others glide to theirs. A
    // card leaving keeps its place while it fades.
    private void Place(bool motion)
    {
        double w = ActualWidth, h = ActualHeight, now = Eased.Now;
        if (w > 0 && h > 0)
        {
            var shown = Shown.ToList();
            for (int i = 0; i < shown.Count; i++)
            {
                var card = shown[i];
                var (x, y, width, height) = _layout.Place(i, shown.Count, LinesWidth(w), h);
                card.Width = width; card.Height = height;
                if (!card.Placed) { card.X = new Eased(x, Eased.Seconds); card.Y = new Eased(y, Eased.Seconds); card.Placed = true; }
                else { card.X.Set(x, motion, now); card.Y.Set(y, motion, now); }
            }
            _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        }
        if (Moving(now)) Motion.Start(this, Step);
        InvalidateVisual();
    }

    private double MaxScroll => _layout.Form == DevicesForm.List ? Math.Max(0, _layout.ListHeight(ShownCount, LinesWidth(ActualWidth)) - ActualHeight) : 0;
    // The list keeps room at its right for its scroll bar.
    private double LinesWidth(double width) => _layout.Form == DevicesForm.List ? Math.Max(0, width - 12) : width;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (MaxScroll <= 0) return;
        _scroll = Math.Clamp(_scroll - e.Delta / 120.0 * DevicesLayout.ListRow, 0, MaxScroll);
        e.Handled = true;
        Hover(e.GetPosition(this));
        InvalidateVisual();
    }

    private bool Moving(double now) => _empty.Moving(now) || _looking.Moving(now)
        || _cards.Any(card => card.Presence.Moving(now) || card.X.Moving(now) || card.Y.Moving(now) || card.Fill.Moving(now));

    private bool Step(double now)
    {
        InvalidateVisual();
        if (Moving(now)) return true;
        _cards.RemoveAll(card => card.Leaving);
        return false;
    }

    // The card drawn under the pointer, where it is drawn now (cards glide for a moment after a device comes or goes).
    private void Hover(Point? at)
    {
        double now = Eased.Now, scroll = _layout.Form == DevicesForm.List ? _scroll : 0;
        string? found = null;
        if (at is { } point)
            for (int i = _cards.Count - 1; i >= 0; i--)
            {
                var card = _cards[i];
                if (card.Leaving || !card.Placed) continue;
                if (new Rect(card.X.At(now), card.Y.At(now) - scroll, card.Width, card.Height).Contains(point)) { found = card.Key; break; }
            }
        if (found == _hovered) return;
        _hovered = found;
        _tip.IsOpen = false;
        _tipDelay.Stop();
        if (found is not null && _cards.Any(card => card.Key == found && card.Cut)) _tipDelay.Start();
    }
    private void OpenToolTip()
    {
        var card = _cards.FirstOrDefault(card => card.Key == _hovered && !card.Leaving);
        if (card is null || !card.Cut || !IsMouseOver) return;
        double now = Eased.Now, scroll = _layout.Form == DevicesForm.List ? _scroll : 0;
        _tip.PlacementRectangle = new Rect(card.X.At(now), card.Y.At(now) - scroll, card.Width, card.Height);
        _tip.Content = card.Whole;
        _tip.IsOpen = true;
    }
    // An open tooltip follows its device's readings.
    private void RefreshToolTip()
    {
        if (!_tip.IsOpen) return;
        var card = _cards.FirstOrDefault(card => card.Key == _hovered && !card.Leaving);
        if (card is null || !card.Cut) { _tip.IsOpen = false; return; }
        string text = card.Whole;
        if (!Equals(_tip.Content, text)) _tip.Content = text;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, h = ActualHeight, now = Eased.Now, ppd = MonitorDraw.PixelsPerDip(this);
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        double cards = _cards.Count == 0 ? 0 : _cards.Max(card => Math.Clamp(card.Presence.At(now), 0, 1));
        Empty(drawing, w, h, ppd, Math.Clamp(_empty.At(now), 0, 1) * (1 - cards));
        Looking(drawing, w, h, ppd, Math.Clamp(_looking.At(now), 0, 1));
        bool list = _layout.Form == DevicesForm.List;
        double scroll = list ? _scroll : 0, max = MaxScroll;
        // A list that runs on fades out at the bottom, until it is scrolled to its end.
        bool fade = list && scroll < max - .5;
        if (fade)
        {
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0)); mask.GradientStops.Add(new GradientStop(Colors.Black, Math.Max(0, 1 - 28 / Math.Max(1, h))));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            drawing.PushOpacityMask(mask);
        }
        foreach (var card in _cards)
        {
            double presence = Math.Clamp(card.Presence.At(now), 0, 1);
            if (presence <= .001 || !card.Placed) continue;
            double x = Math.Round(card.X.At(now)), y = Math.Round(card.Y.At(now) - scroll + (1 - presence) * 6);
            if (y + card.Height < 0 || y > h) continue;
            bool partial = presence < .999;
            if (partial) drawing.PushOpacity(presence * presence);
            var r = new Rect(x, y, card.Width, card.Height);
            switch (_layout.Form)
            {
                case DevicesForm.Single: DrawSingle(drawing, card, r, now, ppd); break;
                case DevicesForm.List: DrawLine(drawing, card, r, now, ppd, card.Y.Target >= DevicesLayout.ListRow * .5); break;
                default: DrawTile(drawing, card, r, now, ppd); break;
            }
            if (partial) drawing.Pop();
        }
        if (fade) drawing.Pop();
        // The list's position as a thin bar at its right edge.
        if (list && max > 0)
        {
            double content = _layout.ListHeight(ShownCount, LinesWidth(w)), length = Math.Max(16, h * h / content), top = (h - length) * scroll / max;
            drawing.DrawRoundedRectangle(UI.Brush(PanelTheme.Hex("scrollThumb")), null, new Rect(w - 3, top, 3, length), 1.5, 1.5);
        }
    }

    // The empty page: a ring with no charge around a faint device, the words under it.
    private ((string, double, double, string) Key, FormattedText Text)? _hint;
    private void Empty(DrawingContext drawing, double w, double h, double ppd, double opacity)
    {
        if (opacity <= .001) return;
        var heading = MonitorDraw.Text(Loc.T("没有已连接的无线设备", "No wireless devices connected"), 13, UI.Secondary, ppd);
        string hint = Loc.T("连接蓝牙耳机、键鼠、手柄，或插上罗技等接收器后会显示在这里", "Bluetooth headphones, keyboards, mice and controllers, and receivers such as Logitech's, show here once connected");
        // Laid out once per text, width and scale: it is drawn on every frame of a fade.
        var key = (hint, Math.Round(Math.Max(40, Math.Min(w - 24, 300))), ppd, UI.Tertiary);
        if (_hint is not { } known || known.Key != key)
            _hint = (key, new FormattedText(hint, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(UI.PanelFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11, UI.Brush(UI.Tertiary), ppd)
            { MaxTextWidth = key.Item2, MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center });
        var lines = _hint.Value.Text;
        bool ring = h >= 120;
        double ringSize = ring ? 48 : 0, total = ringSize + (ring ? 12 : 0) + heading.Height + 3 + lines.Height, top = Math.Round((h - total) / 2);
        if (opacity < .999) drawing.PushOpacity(opacity);
        if (ring)
        {
            var center = new Point(Math.Round(w / 2), top + ringSize / 2);
            Ring(drawing, center, ringSize, 4, null, UI.Primary);
            drawing.PushOpacity(.35);
            DeviceGlyphs.Draw(drawing, DeviceKind.Other, new Rect(center.X - 11, center.Y - 11, 22, 22), UI.Primary);
            drawing.Pop();
        }
        double y = top + ringSize + (ring ? 12 : 0);
        drawing.DrawText(heading, new Point(Math.Round((w - heading.WidthIncludingTrailingWhitespace) / 2), y));
        drawing.DrawText(lines, new Point(Math.Round((w - lines.MaxTextWidth) / 2), y + heading.Height + 3));
        if (opacity < .999) drawing.Pop();
    }

    // Before the first listing: three empty rings, each with a short arc, and the words under them.
    private void Looking(DrawingContext drawing, double w, double h, double ppd, double opacity)
    {
        if (opacity <= .001) return;
        var words = MonitorDraw.Text(Loc.T("正在查找设备…", "Looking for devices…"), 11, UI.Tertiary, ppd);
        double size = h >= 120 ? 48 : 32, gap = size * .6, total = size + 12 + words.Height, top = Math.Round((h - total) / 2);
        if (opacity < .999) drawing.PushOpacity(opacity);
        for (int i = 0; i < 3; i++)
        {
            var center = new Point(Math.Round(w / 2 + (i - 1) * (size + gap)), top + size / 2);
            Ring(drawing, center, size, size / 12, null, UI.Primary);
            Arc(drawing, center, (size - size / 12) / 2, size / 12, i / 3.0, .14, MonitorDraw.Faint(UI.Primary, .35));
        }
        drawing.DrawText(words, new Point(Math.Round((w - words.WidthIncludingTrailingWhitespace) / 2), top + size + 12));
        if (opacity < .999) drawing.Pop();
    }

    // A tile: the ring with the icon in it, then the figure, the name and the line under it, each centred.
    private void DrawTile(DrawingContext drawing, Card card, Rect r, double now, double ppd)
    {
        var t = _layout.Tile;
        var device = card.Device;
        string ring = _inks.Ring(device), accent = _inks.Figure(device);
        double cx = r.X + r.Width / 2;
        var center = new Point(cx, r.Y + t.Ring / 2);
        Ring(drawing, center, t.Ring, t.Stroke, device.Lowest is null ? null : Math.Clamp(card.Fill.At(now), 0, 1), ring);
        DeviceGlyphs.Draw(drawing, device.Kind, new Rect(cx - t.Icon / 2, center.Y - t.Icon / 2, t.Icon, t.Icon), device.Lowest is null ? UI.Tertiary : UI.Primary);
        if (device.Charging && device.Lowest is not null) Badge(drawing, center, (t.Ring - t.Stroke) / 2, Math.Round(t.Ring * .28), ring);
        double top = r.Y + t.Ring + t.Gap;
        Figure(drawing, device, accent, cx, top, t.Figure, t.FigureLine, t.Unit, ppd, centered: true);
        top += t.FigureLine + DeviceTileStep.NameGap;
        var (name, detail) = Texts(card, device.Name, t.DetailLine > 0 ? Detail(device, _inks.Level(device)) : null,
            r.Width - 8, t.Name, t.Detail, UI.Primary, _inks.Detail(device), ppd);
        drawing.DrawText(name, new Point(Math.Round(cx - name.WidthIncludingTrailingWhitespace / 2), Math.Round(top + (t.NameLine - name.Height) / 2)));
        if (detail is not null)
            drawing.DrawText(detail, new Point(Math.Round(cx - detail.WidthIncludingTrailingWhitespace / 2), Math.Round(top + t.NameLine + (t.DetailLine - detail.Height) / 2)));
    }

    // One device alone: its ring at the left, the figure, the name and what it is beside it.
    private void DrawSingle(DrawingContext drawing, Card card, Rect r, double now, double ppd)
    {
        var s = _layout.Single;
        var device = card.Device;
        string ring = _inks.Ring(device), accent = _inks.Figure(device);
        var center = new Point(r.X + s.Ring / 2, r.Y + s.Ring / 2);
        Ring(drawing, center, s.Ring, s.Stroke, device.Lowest is null ? null : Math.Clamp(card.Fill.At(now), 0, 1), ring);
        DeviceGlyphs.Draw(drawing, device.Kind, new Rect(center.X - s.Icon / 2, center.Y - s.Icon / 2, s.Icon, s.Icon), device.Lowest is null ? UI.Tertiary : UI.Primary);
        if (device.Charging && device.Lowest is not null) Badge(drawing, center, (s.Ring - s.Stroke) / 2, Math.Round(s.Ring * .22), ring);
        double x = r.X + s.Ring + s.Gap, width = Math.Max(10, r.Right - x);
        string what = string.Join(" · ", new[] { Devices.KindName(device.Kind), Detail(device, _inks.Level(device)) }.Distinct());
        var (name, detail) = Texts(card, device.Name, what, width, s.Name, s.Detail, UI.Primary, _inks.Detail(device), ppd);
        double block = s.FigureLine + 2 + s.NameLine + s.DetailLine, top = Math.Round(center.Y - block / 2);
        Figure(drawing, device, accent, x, top, s.Figure, s.FigureLine, s.Unit, ppd, centered: false);
        top += s.FigureLine + 2;
        drawing.DrawText(name, new Point(x, Math.Round(top + (s.NameLine - name.Height) / 2)));
        drawing.DrawText(detail!, new Point(x, Math.Round(top + s.NameLine + (s.DetailLine - detail!.Height) / 2)));
    }

    // A line of the list: the icon, the name and the line under it, then a small ring (with a bolt in it while charging) and the
    // figure in a column of its own, so the rings line up from line to line. divider: a hairline above it.
    private void DrawLine(DrawingContext drawing, Card card, Rect r, double now, double ppd, bool divider)
    {
        var device = card.Device;
        string ring = _inks.Ring(device), accent = _inks.Figure(device);
        if (divider) drawing.DrawRectangle(UI.Brush(PanelTheme.Hex("rowDivider")), null, new Rect(r.X, r.Y, r.Width, 1 / ppd));
        double middle = r.Y + r.Height / 2;
        DeviceGlyphs.Draw(drawing, device.Kind, new Rect(r.X, Math.Round(middle - 9), 18, 18), device.Lowest is null ? UI.Tertiary : UI.Primary);
        const double figureColumn = 38, ringSize = 18;
        double figureRight = r.Right, ringX = figureRight - figureColumn - 6 - ringSize;
        var center = new Point(ringX + ringSize / 2, middle);
        Ring(drawing, center, ringSize, 2.5, device.Lowest is null ? null : Math.Clamp(card.Fill.At(now), 0, 1), ring);
        if (device.Charging && device.Lowest is not null) DeviceGlyphs.Bolt(drawing, new Rect(center.X - 5, center.Y - 5, 10, 10), ring);
        // The figure, right-aligned in its column.
        var number = MonitorDraw.Text(device.Lowest is { } p ? Devices.Clamp(p).ToString(System.Globalization.CultureInfo.InvariantCulture) : "—", 15,
            device.Lowest is null ? UI.Tertiary : accent, ppd, display: true);
        var unit = MonitorDraw.Text("%", 10, UI.Secondary, ppd);
        double baseline = Math.Round(middle + 15 * .36), unitX = figureRight - (device.Lowest is null ? 0 : unit.WidthIncludingTrailingWhitespace);
        if (device.Lowest is not null) MonitorDraw.Baseline(drawing, unit, unitX, baseline);
        MonitorDraw.Baseline(drawing, number, unitX - (device.Lowest is null ? 0 : 1) - number.WidthIncludingTrailingWhitespace, baseline);
        double textX = r.X + 18 + 10, textWidth = Math.Max(10, ringX - 10 - textX);
        var (name, detail) = Texts(card, device.Name, Detail(device, _inks.Level(device)), textWidth, 12, 10.5, UI.Primary, _inks.Detail(device), ppd);
        double block = 15 + 13, top = Math.Round(middle - block / 2);
        drawing.DrawText(name, new Point(textX, Math.Round(top + (15 - name.Height) / 2)));
        drawing.DrawText(detail!, new Point(textX, Math.Round(top + 15 + (13 - detail!.Height) / 2)));
    }

    // The figure in the display face with "%" small after it, on one baseline; "—" for a device that reports no battery.
    private static void Figure(DrawingContext drawing, DeviceBattery device, string accent, double x, double top, double size, double line, double unitSize, double ppd, bool centered)
    {
        var number = MonitorDraw.Text(device.Lowest is { } p ? Devices.Clamp(p).ToString(System.Globalization.CultureInfo.InvariantCulture) : "—", size,
            device.Lowest is null ? UI.Tertiary : accent, ppd, display: true);
        var unit = device.Lowest is null ? null : MonitorDraw.Text("%", unitSize, UI.Secondary, ppd);
        double width = number.WidthIncludingTrailingWhitespace + (unit is null ? 0 : 1 + unit.WidthIncludingTrailingWhitespace);
        double left = Math.Round(centered ? x - width / 2 : x), y = Math.Round(top + (line - number.Height) / 2);
        drawing.DrawText(number, new Point(left, y));
        if (unit is not null) MonitorDraw.Baseline(drawing, unit, left + number.WidthIncludingTrailingWhitespace + 1, y + number.Baseline);
    }

    // The card's name and detail lines, cut to the width, laid out again only when their text, width or color changes.
    private static (FormattedText Name, FormattedText? Detail) Texts(Card card, string name, string? detail, double width, double nameSize, double detailSize,
        string nameColor, string detailColor, double ppd)
    {
        var nameKey = (name, Math.Round(width), nameColor, nameSize * ppd);
        if (card.Name is null || card.NameKey != nameKey)
        {
            card.Name = MonitorDraw.Trimmed(name, nameSize, nameColor, width, ppd); card.NameKey = nameKey;
            card.NameCut = Cut(name, nameSize, nameColor, width, ppd); card.FullName = name;
        }
        if (detail is null) { card.DetailCut = false; return (card.Name, null); }
        var detailKey = (detail, Math.Round(width), detailColor, detailSize * ppd);
        if (card.Detail is null || card.DetailKey != detailKey)
        {
            card.Detail = MonitorDraw.Trimmed(detail, detailSize, detailColor, width, ppd); card.DetailKey = detailKey;
            card.DetailCut = Cut(detail, detailSize, detailColor, width, ppd); card.FullDetail = detail;
        }
        return (card.Name, card.Detail);
    }

    private static bool Cut(string text, double size, string color, double width, double ppd)
        => MonitorDraw.Text(text, size, color, ppd).WidthIncludingTrailingWhitespace > Math.Max(1, width) + 0.5;

    // The line under the name: the earbuds' parts, then charging, low or getting low, an estimate, or that the device reports no
    // battery; otherwise where the reading comes from.
    private static string Detail(DeviceBattery device, MemoryLevel level)
    {
        var parts = new List<string>();
        if (device.Parts.Count > 0) parts.Add(string.Join("  ", device.Parts.Select(part => Devices.PartName(part.Part) + " " + Devices.Figure(part.Percent))));
        if (device.Charging) parts.Add(Loc.T("充电中", "Charging"));
        else if (device.Lowest is null) parts.Add(Loc.T("未报告电量", "No battery reported"));
        else if (level == MemoryLevel.Critical) parts.Add(Loc.T("电量低", "Battery low"));
        else if (level == MemoryLevel.Elevated) parts.Add(Loc.T("电量偏低", "Getting low"));
        else if (device.Coarse) parts.Add(Loc.T("估算", "Estimated"));
        if (parts.Count == 0) parts.Add(Devices.SourceName(device.Source));
        return string.Join(" · ", parts);
    }

    // The charging green, made to stand out from the theme's surface as a graphic (a lighter theme gets a darker green).
    internal static string Charging => PanelTheme.Readable("#6BD68A");

    // A ring: the theme's ring track all round, and the share as an arc from the top, clockwise, with round ends. share null: the
    // track alone. track: the track's color, the panel theme's ring track when not given.
    internal static void Ring(DrawingContext drawing, Point center, double size, double stroke, double? share, string color, string? track = null)
    {
        double radius = (size - stroke) / 2;
        drawing.DrawEllipse(null, DeviceGlyphs.Stroke(track ?? PanelTheme.Hex("ringTrack"), stroke), center, radius, radius);
        if (share is { } s && s > .002) Arc(drawing, center, radius, stroke, 0, s, color);
    }

    // An arc of the circle from start to start + length (turns, from the top, clockwise).
    private static void Arc(DrawingContext drawing, Point center, double radius, double stroke, double start, double length, string color)
    {
        var pen = DeviceGlyphs.Stroke(color, stroke);
        if (length >= .999) { drawing.DrawEllipse(null, pen, center, radius, radius); return; }
        Point at(double turn) => new(center.X + radius * Math.Sin(turn * 2 * Math.PI), center.Y - radius * Math.Cos(turn * 2 * Math.PI));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(at(start), false, false);
            context.ArcTo(at(start + length), new Size(radius, radius), 0, length > .5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        drawing.DrawGeometry(null, pen, geometry);
    }

    // The charging mark: a disc in the ring's (charging) color on the ring at its lower right, set off from it by the page's own color, with
    // a bolt in that color inside.
    internal static void Badge(DrawingContext drawing, Point center, double radius, double size, string color)
    {
        var at = new Point(center.X + radius * .7071, center.Y + radius * .7071);
        double cut = Math.Max(1.5, size * .18);
        drawing.DrawEllipse(UI.Brush(PanelTheme.Hex("surface")), null, at, size / 2 + cut, size / 2 + cut);
        drawing.DrawEllipse(UI.Brush(color), null, at, size / 2, size / 2);
        double bolt = size * .66;
        DeviceGlyphs.Bolt(drawing, new Rect(at.X - bolt / 2, at.Y - bolt / 2, bolt, bolt), PanelTheme.Hex("surface"));
    }
}

// Each device's colors: its own (DeviceChoice), else the defaults: the ring in the text color while normal, green while charging,
// the collapsed readings' warning and critical colors when getting low and low. A color of the user's is moved to stand out from the
// theme's surface; as text (the figure, the line under the name) it is moved to read at 4.5:1. While normal the figure keeps the
// text color; getting low, the line under the name does too (the warning amber reads poorly as small text on light themes). In
// high contrast the system's highlight and text colors.
internal sealed class DeviceInks(MonitorColors colors, IReadOnlyList<DeviceChoice> choices, int warning, int low)
{
    // What the colors were made from, so a page built with them is built again when they change.
    public string Key => string.Join("|", warning, low, colors.Warning, colors.Critical, PanelTheme.Generation, SystemParameters.HighContrast,
        string.Join(",", choices.Select(choice => $"{choice.Key}:{choice.Color}:{choice.ChargeColor}:{choice.WarningColor}:{choice.CriticalColor}")));
    public MemoryLevel Level(DeviceBattery device) => Devices.Level(device.Lowest, device.Charging, warning, low);
    private static bool Charging(DeviceBattery device) => device.Charging && device.Lowest is not null;

    public string Ring(DeviceBattery device)
    {
        if (SystemParameters.HighContrast) return SystemColors.HighlightColor.ToString();
        var own = DeviceLists.Find(choices, device.Key);
        if (Charging(device)) return Graphic(own?.ChargeColor) ?? DevicesView.Charging;
        return Level(device) switch
        {
            MemoryLevel.Critical => Graphic(own?.CriticalColor) ?? colors.Critical,
            MemoryLevel.Elevated => Graphic(own?.WarningColor) ?? colors.Warning,
            _ => Graphic(own?.Color) ?? UI.Primary
        };
    }
    public string Figure(DeviceBattery device)
    {
        if (SystemParameters.HighContrast) return SystemColors.WindowTextColor.ToString();
        return Charging(device) || Level(device) == MemoryLevel.Normal ? UI.Primary : Text(Ring(device));
    }
    public string Detail(DeviceBattery device)
    {
        if (SystemParameters.HighContrast) return SystemColors.WindowTextColor.ToString();
        return Charging(device) || Level(device) == MemoryLevel.Critical ? Text(Ring(device)) : UI.Tertiary;
    }

    private static string? Graphic(string? color) => color is null ? null : PanelTheme.Readable(color);
    // By theme (a color readable on a light theme is not on a dark one).
    private static readonly Dictionary<(int Theme, string Color), string> TextColors = [];
    public static string Text(string color)
    {
        var key = (PanelTheme.Generation, color);
        if (TextColors.TryGetValue(key, out var readable)) return readable;
        if (TextColors.Count > 32) TextColors.Clear();
        return TextColors[key] = PanelTheme.Readable(color, 4.6);
    }
}

// The devices' icons, filled: Material Symbols Rounded (Apache-2.0) on its 960 unit grid, the keyboard from Fluent UI System Icons
// and the earbuds from Tabler's AirPods outline, filled (both MIT), on 24 unit grids.
internal static class DeviceGlyphs
{
    private static readonly Rect Material = new(0, -960, 960, 960), Small = new(0, 0, 24, 24);
    private static readonly Dictionary<DeviceKind, (Geometry Shape, Rect Grid)> Icons = new()
    {
        [DeviceKind.Mouse] = (Frozen("M480-80q-117 0-198.5-81.5T200-360v-200h560v200q0 117-81.5 198.5T480-80ZM200-620q0-108 71.5-187.5T450-898v278H200Zm310 0v-278q107 11 178.5 90.5T760-620H510Z"), Material),
        [DeviceKind.Keyboard] = (Frozen("M19.75 5C20.99 5 22 6 22 7.25v9.5C22 18 20.99 19 19.75 19H4.25C3.01 19 2 18 2 16.75v-9.5C2 6.01 3 5 4.25 5zm-2.5 9.5H6.65a.75.75 0 0 0 0 1.5h10.7a.75.75 0 0 0 0-1.5zM16.5 11a1 1 0 1 0 0 2 1 1 0 0 0 0-2m-3 0a1 1 0 1 0 0 2 1 1 0 0 0 0-2m-3 0a1 1 0 1 0 0 2 1 1 0 0 0 0-2m-3 0a1 1 0 1 0 0 2 1 1 0 0 0 0-2M6 8a1 1 0 1 0 0 2 1 1 0 0 0 0-2m3 0a1 1 0 1 0 0 2 1 1 0 0 0 0-2m3 0a1 1 0 1 0 0 2 1 1 0 0 0 0-2m3 0a1 1 0 1 0 0 2 1 1 0 0 0 0-2m3 0a1 1 0 1 0 0 2 1 1 0 0 0 0-2"), Small),
        [DeviceKind.Headphones] = (Frozen("M268-120h-88q-24 0-42-18t-18-42v-300q0-75 28.5-140.5t77-114q48.5-48.5 114-77T480-840q75 0 140.5 28.5t114 77q48.5 48.5 77 114T840-480v300q0 24-18 42t-42 18h-88q-25 0-42.5-17.5T632-180v-176q0-25 17.5-42.5T692-416h88v-64q0-125-87.5-212.5T480-780q-125 0-212.5 87.5T180-480v64h88q25 0 42.5 17.5T328-356v176q0 25-17.5 42.5T268-120Z"), Material),
        [DeviceKind.Earbuds] = (Frozen("M6 4a4 4 0 0 1 4 3.8v10.7a1.5 1.5 0 0 1 -3 0v-6.5h-1a4 4 0 0 1 -4 -3.8v-.2a4 4 0 0 1 4 -4z M18 4a4 4 0 0 0 -4 3.8v10.7a1.5 1.5 0 0 0 3 0v-6.5h1a4 4 0 0 0 4 -3.8v-.2a4 4 0 0 0 -4 -4z"), Small),
        [DeviceKind.Controller] = (Frozen("M189-160q-60 0-102.5-43T42-307q0-9 1-18t3-18l84-336q14-54 57-87.5t98-33.5h390q55 0 98 33.5t57 87.5l84 336q2 9 3.5 18.5T919-306q0 61-43.5 103.5T771-160q-42 0-78-22t-54-60l-28-58q-5-10-15-15t-21-5H385q-11 0-21 5t-15 15l-28 58q-18 38-54 60t-78 22Zm372.5-378.68q8.5-8.67 8.5-21.5 0-12.82-8.68-21.32-8.67-8.5-21.5-8.5-12.82 0-21.32 8.68-8.5 8.67-8.5 21.5 0 12.82 8.68 21.32 8.67 8.5 21.5 8.5 12.82 0 21.32-8.68Zm80-80q8.5-8.67 8.5-21.5 0-12.82-8.68-21.32-8.67-8.5-21.5-8.5-12.82 0-21.32 8.68-8.5 8.67-8.5 21.5 0 12.82 8.68 21.32 8.67 8.5 21.5 8.5 12.82 0 21.32-8.68Zm0 160q8.5-8.67 8.5-21.5 0-12.82-8.68-21.32-8.67-8.5-21.5-8.5-12.82 0-21.32 8.68-8.5 8.67-8.5 21.5 0 12.82 8.68 21.32 8.67 8.5 21.5 8.5 12.82 0 21.32-8.68Zm80-80q8.5-8.67 8.5-21.5 0-12.82-8.68-21.32-8.67-8.5-21.5-8.5-12.82 0-21.32 8.68-8.5 8.67-8.5 21.5 0 12.82 8.68 21.32 8.67 8.5 21.5 8.5 12.82 0 21.32-8.68ZM358-472.08q7-7.09 7-17.92v-45h45q10.83 0 17.92-7.12 7.08-7.11 7.08-18 0-10.88-7.08-17.88-7.09-7-17.92-7h-45v-45q0-10.83-7.12-17.92-7.11-7.08-18-7.08-10.88 0-17.88 7.08-7 7.09-7 17.92v45h-45q-10.83 0-17.92 7.12-7.08 7.11-7.08 18 0 10.88 7.08 17.88 7.09 7 17.92 7h45v45q0 10.83 7.12 17.92 7.11 7.08 18 7.08 10.88 0 17.88-7.08Z"), Material),
        [DeviceKind.Pen] = (Frozen("M167-120q-21 5-36.5-10.5T120-167l36-169 180 180-169 36Zm221-70L190-388l443-443q17-17 42-17t42 17l114 114q17 17 17 42t-17 42L388-190Z"), Material),
        [DeviceKind.Speaker] = (Frozen("M700-80H260q-25 0-42.5-17.5T200-140v-680q0-25 17.5-42.5T260-880h440q25 0 42.5 17.5T760-820v680q0 25-17.5 42.5T700-80ZM535-626q22-22 22-54t-22-54q-22-22-54-22t-54 22q-22 22-22 54t22 54q22 22 54 22t54-22Zm61.5 382.5Q644-291 644-360t-47.5-116.5Q549-524 480-524t-116.5 47.5Q316-429 316-360t47.5 116.5Q411-196 480-196t116.5-47.5Zm-190-43Q376-317 376-360t30.5-73.5Q437-464 480-464t73.5 30.5Q584-403 584-360t-30.5 73.5Q523-256 480-256t-73.5-30.5Z"), Material),
        [DeviceKind.Other] = (Frozen("M140-160q-24 0-42-18t-18-42v-520q0-24 18-42t42-18h680q25 0 42.5 17.5T880-740v11q-11-5-22-8t-23-3H606q-42 0-73.5 31.5T501-635v115H336q-10 0-18 6t-11 16l-20 72q-21 23-33.5 52.5T241-310q0 34 12.5 63.5T287-194l9 34H140Zm466 0q-18 0-31.5-13.5T561-205v-430q0-18 13.5-31.5T606-680h229q18 0 31.5 13.5T880-635v430q0 18-13.5 31.5T835-160H606Zm-254-22-12-44q-18-15-28.5-36.5T301-310q0-26 10.5-47.5T340-394l12-44q3-10 11-16t18-6h60q10 0 18 6t11 16l12 44q18 15 28.5 36.5T521-310q0 26-10.5 47.5T482-226l-12 44q-3 10-11 16t-18 6h-60q-10 0-18-6t-11-16Zm59-58q31 0 50.5-20.5T481-310q0-29-21-49.5T411-380q-28 0-49 20t-21 50q0 30 20 50t50 20Zm310-280q12 0 21-9t9-21q0-12-9-21t-21-9q-12 0-21 9t-9 21q0 12 9 21t21 9Z"), Material),
    };
    private static readonly Geometry BoltShape = Frozen("M360-360H217q-18 0-26.5-16t2.5-31l338-488q8-11 20-15t24 1q12 5 19 16t5 24l-39 309h176q19 0 27 17t-4 32L388-66q-8 10-20.5 13T344-55q-11-5-17.5-16T322-95l38-265Z");
    private static Geometry Frozen(string data) { var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }

    // Round-capped pens by color and thickness, made once: a page redraws on every frame of a move.
    private static readonly Dictionary<(string Color, double Thickness), Pen> Pens = [];
    public static Pen Stroke(string color, double thickness)
    {
        if (Pens.TryGetValue((color, thickness), out var pen)) return pen;
        pen = new Pen(UI.Brush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        if (Pens.Count > 64) Pens.Clear();
        return Pens[(color, thickness)] = pen;
    }

    // Transforms that place a glyph, by their matrix, made once.
    private static readonly Dictionary<Matrix, Transform> Places = [];
    private static Transform Placed(Rect grid, Rect at)
    {
        double scale = at.Width / grid.Width;
        var matrix = new Matrix(scale, 0, 0, scale, at.X - grid.X * scale, at.Y - grid.Y * scale);
        if (Places.TryGetValue(matrix, out var known)) return known;
        var transform = new MatrixTransform(matrix); transform.Freeze();
        if (Places.Count > 128) Places.Clear();
        return Places[matrix] = transform;
    }

    public static void Draw(DrawingContext drawing, DeviceKind kind, Rect at, string color)
    {
        var (shape, grid) = Icons.TryGetValue(kind, out var icon) ? icon : Icons[DeviceKind.Other];
        drawing.PushTransform(Placed(grid, at));
        drawing.DrawGeometry(UI.Brush(color), null, shape);
        drawing.Pop();
    }

    // A bolt filling the area.
    public static void Bolt(DrawingContext drawing, Rect at, string color)
    {
        drawing.PushTransform(Placed(Material, at));
        drawing.DrawGeometry(UI.Brush(color), null, BoltShape);
        drawing.Pop();
    }
}
