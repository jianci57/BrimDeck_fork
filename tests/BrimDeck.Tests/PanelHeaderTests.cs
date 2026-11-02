using BrimDeck.Core;

internal static class PanelHeaderTests
{
    internal static void Run(Action<string, bool> check)
    {
        // Music page at the three presets and the largest panel: row Toolbar + Top, switch at Side, gear's ink at Side.
        var expected = new (double Density, double Row, double Side, double Switch, double Top, double Right, double Settings)[]
        { (0, 32, 16, 24, 4, 10, 24), (1, 44, 20, 28, 8, 14, 24), (2, 50, 24, 28, 11, 18, 24), (3, 52, 28, 28, 12, 22, 24) };
        foreach (var e in expected)
        {
            var c = MusicMetrics.At(e.Density);
            var h = PanelHeader.Music(c);
            check($"Music header at density {e.Density}: {e.Row} row, switch {e.Switch} at ({e.Side}, {e.Top}), right margin {e.Right}",
                h.Row == e.Row && h.Row == c.Toolbar + c.Top && h.Height == e.Row && h.Side == e.Side && h.Side == c.Side
                && h.SwitchHeight == e.Switch && h.ButtonHeight == e.Switch - 4 && h.SwitchTop == e.Top && h.RightMargin == e.Right
                && h.SettingsHeight == e.Settings && h.RightMargin + PanelHeader.GearInset == c.Side);
        }
        check("The music body keeps its height: the header row replaces the toolbar and the top margin",
            MusicLayout.Calculate(440, 200, true, true).BodyHeight == 200 - PanelHeader.Music(MusicMetrics.At(1)).Row - MusicMetrics.At(1).Bottom);

        // Usage tiers: toolbar 40/36/36/32/28, content EdgeInset 6 + ColumnPad 20/14/10/10/8.
        var usage = new (double Row, double Side, double Header, double Switch, double Top, double Right, double Settings)[]
        { (40, 26, 26, 28, 6, 20, 24), (36, 20, 20, 28, 4, 14, 24), (36, 16, 16, 28, 4, 10, 24), (32, 16, 16, 24, 4, 10, 24), (28, 14, 16, 24, 2, 10, 20) };
        foreach (var e in usage)
        {
            var h = PanelHeader.For(e.Row, e.Side);
            check($"Usage header for a {e.Row} toolbar and {e.Side} content inset: switch {e.Switch} at ({e.Header}, {e.Top})",
                h.Height == e.Row && h.Side == e.Header && h.SwitchHeight == e.Switch && h.SwitchTop == e.Top && h.RightMargin == e.Right && h.SettingsHeight == e.Settings);
        }

        check("An odd row centres the controls in its even part", PanelHeader.For(37, 18) is { Row: 37, Height: 36, SwitchTop: 4 });
        check("Every header value is a whole DIP for any density", Enumerable.Range(0, 301).Select(i => PanelHeader.Music(MusicMetrics.At(i / 100d)))
            .All(h => new[] { h.Row, h.Height, h.Side, h.SwitchHeight, h.ButtonHeight, h.SwitchTop, h.RightMargin, h.SettingsHeight }.All(v => v == Math.Round(v)) && h.Height % 2 == 0));
        check("Bad input gives an empty row", PanelHeader.For(double.NaN, double.PositiveInfinity) is { Row: 0, Height: 0, SwitchTop: 0 });
        check("The inner buttons are concentric with the switch", PanelHeader.ButtonRadius == PanelHeader.SwitchRadius - PanelHeader.SwitchPadding);

        // A rounded top corner (R24, whenever the panel does not start at the monitor's top) never comes closer to the switch,
        // the gear or the settings button's hover background than the top edge does, and never clips them.
        var headers = Enumerable.Range(0, 301).Select(i => PanelHeader.Music(MusicMetrics.At(i / 100d)))
            .Concat(usage.Select(e => PanelHeader.For(e.Row, e.Side))).Concat([PanelHeader.For(28, 20), PanelHeader.For(36, 14)]).ToList();
        bool Clear(PanelHeader h)
        {
            double R = PanelHeader.PanelRadius;
            double switchGap = PanelHeader.CornerGap(R, h.Side, h.SwitchTop, PanelHeader.SwitchRadius);
            double gearTop = (h.Height - PanelHeader.GearSize) / 2, gearGap = PanelHeader.CornerGap(R, h.Side, gearTop, PanelHeader.GearSize / 2);
            double hoverGap = PanelHeader.CornerGap(R, h.RightMargin, (h.Height - h.SettingsHeight) / 2, PanelHeader.SettingsRadius);
            return switchGap >= h.SwitchTop && gearGap >= Math.Min(gearTop, 4) && hoverGap >= 1;
        }
        check("Under a rounded top corner the header keeps clear of the curve at every size", headers.All(Clear));
        // A panel floating below a lowered capsule adds CollapsedLimits.FloatingHeaderExtra above the same header row, so the
        // controls keep their size tier and only move further from the curve; the switch then sits 8 DIPs or more below the edge.
        bool ClearFloating(PanelHeader h)
        {
            double R = PanelHeader.PanelRadius, extra = CollapsedLimits.FloatingHeaderExtra, switchTop = h.SwitchTop + extra;
            double gearTop = (h.Height - PanelHeader.GearSize) / 2 + extra;
            return PanelHeader.CornerGap(R, h.Side, switchTop, PanelHeader.SwitchRadius) >= Math.Min(switchTop, h.Side)
                && PanelHeader.CornerGap(R, h.Side, gearTop, PanelHeader.GearSize / 2) >= 4
                && PanelHeader.CornerGap(R, h.RightMargin, (h.Height - h.SettingsHeight) / 2 + extra, PanelHeader.SettingsRadius) >= 1 && switchTop >= 8;
        }
        check("A floating panel's header keeps clear of the curve at every size, with the switch at least 8 DIPs below the edge", headers.All(ClearFloating));
        check("The previous header (switch at (10, 4)) was 2.6 DIPs from the curve, which the check rejects",
            Math.Abs(PanelHeader.CornerGap(24, 10, 4, 8) - 2.58) < .01 && PanelHeader.CornerGap(24, 10, 0, 8) < 0);

        // The usage page keeps its own header (78d0a03: settings 28x24 with a 6 DIP corner; ba20473: switch 28, or 24 in a row below
        // 36). The music page takes the usage page's controls, so they keep their distance from the panel's top edge when the page
        // changes; it keeps its own row (where its content starts), side inset and right margin.
        var usageHeaders = usage.Select(e => PanelHeader.For(e.Row, e.Side)).ToList();
        var musicHeaders = Enumerable.Range(0, 301).Select(i => PanelHeader.Music(MusicMetrics.At(i / 100d))).ToList();
        static (double, double, double, double, double) Controls(PanelHeader h) => (h.Height, h.SwitchHeight, h.ButtonHeight, h.SwitchTop, h.SettingsHeight);
        check("With the usage page's controls, every music header puts the switch, the clock and the right group where the usage page does",
            usageHeaders.All(u => musicHeaders.All(m => Controls(m.WithControlsOf(u)) == Controls(u))));
        check("The music page keeps its row, side and right margin with the usage page's controls",
            usageHeaders.All(u => musicHeaders.All(m => m.WithControlsOf(u) is var w && w.Row == m.Row && w.Side == m.Side && w.RightMargin == m.RightMargin)));
        var user = PanelHeader.For(32, 16);
        var userMusic = PanelHeader.Music(MusicMetrics.At(1)).WithControlsOf(user);
        check("Floating, both pages' switches sit 10 DIPs below the edge (4 hanging)",
            userMusic.SwitchTop + CollapsedLimits.FloatingHeaderExtra == 10 && user.SwitchTop + CollapsedLimits.FloatingHeaderExtra == 10 && userMusic.SwitchTop == 4);
        var mixed = usageHeaders.SelectMany(u => musicHeaders.Select(m => m.WithControlsOf(u))).ToList();
        check("Under a rounded top corner the music header with the usage page's controls keeps clear of the curve, hanging and floating",
            mixed.All(Clear) && mixed.All(ClearFloating));
    }
}