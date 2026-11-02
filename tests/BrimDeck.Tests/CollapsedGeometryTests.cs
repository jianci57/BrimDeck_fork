using BrimDeck.Core;

internal static class CollapsedGeometryTests
{
    internal static void Run(string root, Action<string, bool> check)
    {
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
        var monitor = new DipRect(0, 0, 1920, 1080);
        var work = new DipRect(0, 0, 1920, 1032);
        var defaults = new DeckSettings();

        var notch = CollapsedGeometry.Resolve(defaults, CompactStyle.Notch, monitor, work);
        check("Default notch is 238 x 32, centred, against the screen edge",
            notch.Surface == new DipRect(841, 0, 238, 32) && Near(notch.Scale, 1) && Near(notch.Inset, 30) && Near(notch.ContentWidth, 178)
            && Near(notch.BottomRadius, 12) && Near(notch.TopRadius, 0) && Near(notch.Shoulder, 16));
        var capsule = CollapsedGeometry.Resolve(defaults, CompactStyle.Capsule, monitor, work);
        check("Default capsule is 196 x 32, five DIPs below the edge",
            capsule.Surface == new DipRect(862, 5, 196, 32) && Near(capsule.Inset, 16) && Near(capsule.ContentWidth, 164) && Near(capsule.BottomRadius, 16) && Near(capsule.Shoulder, 0));
        var line = CollapsedGeometry.Resolve(defaults, CompactStyle.Line, monitor, work);
        check("Default line is 56 x 4, four DIPs below the edge", line.Surface == new DipRect(932, 4, 56, 4) && Near(line.BottomRadius, 2));
        var bigNotch = CollapsedGeometry.Resolve(defaults, CompactStyle.Notch, monitor, work, panelScale: 1.5);
        var bigCapsule = CollapsedGeometry.Resolve(defaults, CompactStyle.Capsule, monitor, work, panelScale: 1.5);
        var bigLine = CollapsedGeometry.Resolve(defaults, CompactStyle.Line, monitor, work, panelScale: 1.5);
        check("At 150 % the sizes, the content and the notch's corners grow; the places stay",
            bigNotch.Surface == new DipRect(781.5, 0, 357, 48) && Near(bigNotch.Scale, 1.5) && Near(bigNotch.BottomRadius, 18) && Near(bigNotch.Shoulder, 24)
            && bigCapsule.Surface == new DipRect(813, 5, 294, 48) && Near(bigCapsule.Scale, 1.5)
            && bigLine.Surface == new DipRect(918, 4, 84, 6)
            && CollapsedGeometry.WidthRange(CompactStyle.Notch, monitor, work, 1.5) == (108, 1920)
            && Near(CollapsedGeometry.SurfaceHeight(CompactStyle.Capsule, 32, monitor, work, 1.5), 48));

        var settings = new DeckSettings
        {
            NotchWidth = 9000, NotchHeight = 1, NotchOffsetX = double.NaN, CapsuleWidth = 10, CapsuleHeight = 100, CapsuleTop = -3,
            CapsuleOffsetX = 9999, LineLength = 1000, LineThickness = 3.3, LineTop = 50, LineOffsetX = double.NegativeInfinity
        };
        settings.Normalize();
        check("Normalize clamps every collapsed value to its range",
            settings.NotchWidth == 8000 && settings.NotchHeight == 24 && settings.NotchOffsetX == 0 && settings.CapsuleWidth == 48
            && settings.CapsuleHeight == 64 && settings.CapsuleTop == 0 && settings.CapsuleOffsetX == 4000 && settings.LineLength == 600
            && settings.LineThickness == 3.5 && settings.LineTop == 16 && settings.LineOffsetX == 0);
        var halves = new DeckSettings { LineThickness = 2.24 }; halves.Normalize();
        var halfUp = new DeckSettings { LineThickness = 2.25 }; halfUp.Normalize();
        var thin = new DeckSettings { LineThickness = 0.4, CapsuleTop = double.NaN, NotchWidth = double.PositiveInfinity }; thin.Normalize();
        check("Line thickness rounds to half DIPs within 2 to 8; a value that is not a number becomes the default",
            halves.LineThickness == 2 && halfUp.LineThickness == 2.5 && thin.LineThickness == 2 && thin.CapsuleTop == 5 && thin.NotchWidth == 238);

        var moved = new DeckSettings { NotchOffsetX = 3000, CapsuleOffsetX = -3000, LineOffsetX = 100, NotchWidth = 300 };
        var movedNotch = CollapsedGeometry.Resolve(moved, CompactStyle.Notch, monitor, work);
        var movedCapsule = CollapsedGeometry.Resolve(moved, CompactStyle.Capsule, monitor, work);
        var movedLine = CollapsedGeometry.Resolve(moved, CompactStyle.Line, monitor, work);
        check("Offsets stop at the monitor edge and the saved values stay as they were",
            Near(movedNotch.Surface.X, 1620) && Near(movedCapsule.Surface.X, 0) && Near(movedLine.Surface.X, 1032)
            && moved.NotchOffsetX == 3000 && moved.CapsuleOffsetX == -3000);
        check("The notch stays at the top of the monitor even over a top taskbar",
            CollapsedGeometry.Resolve(moved, CompactStyle.Notch, monitor, new DipRect(0, 48, 1920, 1032)).Surface.Y == 0);
        var leftBar = new DipRect(60, 40, 1860, 1040);
        var capsuleInWork = CollapsedGeometry.Resolve(new DeckSettings { CapsuleOffsetX = -3000 }, CompactStyle.Capsule, monitor, leftBar);
        var lineInWork = CollapsedGeometry.Resolve(defaults, CompactStyle.Line, monitor, leftBar);
        check("The capsule and the line stay within the work area", Near(capsuleInWork.Surface.X, 60) && Near(capsuleInWork.Surface.Y, 45)
            && Near(lineInWork.Surface.Y, 44) && Near(lineInWork.Surface.X, 60 + (1860 - 56) / 2.0));

        var low = CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 200, CapsuleHeight = 64 }, CompactStyle.Capsule, new DipRect(0, 0, 800, 240), new DipRect(0, 0, 800, 220));
        check("A capsule too low for the monitor stops at its bottom, over the taskbar", Near(low.Surface.Y, 240 - 64) && Near(low.Surface.Bottom, 240));
        check("Capsule top range reaches the monitor's bottom", CollapsedGeometry.CapsuleTopRange(32, monitor, work) == (0, 1048)
            && CollapsedGeometry.CapsuleTopRange(64, new DipRect(0, 0, 800, 240), new DipRect(0, 0, 800, 220)) == (0, 176)
            && CollapsedGeometry.CapsuleTopRange(64, new DipRect(0, 0, 800, 10), new DipRect(0, 0, 800, 10)) == (0, 0)
            && CollapsedGeometry.CapsuleTopRange(32, monitor, new DipRect(double.NaN, 0, 0, 0)) == (0, 1048));

        var deep = new DeckSettings { CapsuleTop = 9000 }; deep.Normalize();
        var bottom = CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 3000 }, CompactStyle.Capsule, monitor, work);
        var tinyBottom = CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 4000, CapsuleHeight = 64 }, CompactStyle.Capsule, new DipRect(0, 0, 400, 300), new DipRect(0, 0, 400, 276));
        var unchanged = new DeckSettings { CapsuleTop = 3000 }; CollapsedGeometry.Resolve(unchanged, CompactStyle.Capsule, monitor, work);
        check("The capsule goes down to the bottom of the monitor; saved values stop at 4000 and are not rewritten per monitor",
            deep.CapsuleTop == 4000 && Near(bottom.Surface.Y, 1048) && Near(bottom.Surface.Bottom, 1080) && Near(tinyBottom.Surface.Bottom, 300)
            && unchanged.CapsuleTop == 3000 && CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 600 }, CompactStyle.Capsule, monitor, work).Surface.Y == 600
            && CollapsedGeometry.CapsuleTopRange(48, monitor, new DipRect(0, 40, 1920, 1000)) == (0, 992));

        var topBar = new DipRect(0, 48, 1920, 1032);
        var near = CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 20 }, CompactStyle.Capsule, monitor, topBar);
        var lowered = CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 40 }, CompactStyle.Capsule, monitor, work);
        var nearBottom = CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 4000 }, CompactStyle.Capsule, monitor, work);
        check("The open panel hangs from the screen edge unless the capsule is lowered beyond the bridge",
            CollapsedGeometry.PanelTop(notch, work, 200) == 0 && CollapsedGeometry.PanelTop(line, work, 200) == 0 && CollapsedGeometry.PanelTop(capsule, work, 200) == 0
            && CollapsedGeometry.PanelTop(near, topBar, 200) == 48 && CollapsedGeometry.PanelTop(lowered, work, 200) == 34);
        var topBarNear = CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 5 }, CompactStyle.Capsule, monitor, topBar);
        var topBarLowered = CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 52 }, CompactStyle.Capsule, monitor, topBar);
        check("Top taskbar: a capsule at y 53 opens at the work-area top (48); one at y 100 floats 6 DIPs above it (94)",
            topBarNear.Surface.Y == 53 && CollapsedGeometry.PanelTop(topBarNear, topBar, 200) == 48
            && topBarLowered.Surface.Y == 100 && CollapsedGeometry.PanelTop(topBarLowered, topBar, 200) == 94);
        var tallLowered = CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 100, CapsuleHeight = 48 }, CompactStyle.Capsule, monitor, work);
        var tallerLowered = CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 100, CapsuleHeight = 64 }, CompactStyle.Capsule, monitor, work);
        var shortLowered = CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 100, CapsuleHeight = 24 }, CompactStyle.Capsule, monitor, work);
        check("The lift above a lowered capsule is max(0, 22 - H/2)", CollapsedGeometry.PanelTop(tallLowered, work, 200) == 100
            && CollapsedGeometry.PanelTop(tallerLowered, work, 200) == 100 && CollapsedGeometry.PanelTop(shortLowered, work, 200) == 90
            && CollapsedGeometry.CapsulePanelLift(32) == 6 && CollapsedGeometry.CapsulePanelLift(40) == 2 && CollapsedGeometry.CapsulePanelLift(48) == 0
            && CollapsedGeometry.CapsulePanelLift(double.NaN) == 22);
        // A 32 DIP header row, 6 DIPs below the top edge of the floating panel, is centred on the capsule's centreline.
        check("A floating panel's 32 DIP header is centred on the capsule",
            new[] { lowered, topBarLowered, shortLowered }.All(raised => CollapsedGeometry.Panel(raised, raised == topBarLowered ? topBar : work, 200) is var placed
                && placed.HeaderExtra == CollapsedLimits.FloatingHeaderExtra && placed.Top + placed.HeaderExtra + 16 == raised.Surface.Y + raised.Surface.Height / 2));
        // The user's case: capsule 32 high at 384 on a 1392 DIP work area, a 220 DIP page: 226 tall from 378 to 604.
        var userWork = new DipRect(0, 0, 2560, 1392);
        var user = CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 384 }, CompactStyle.Capsule, new DipRect(0, 0, 2560, 1440), userWork);
        check("A floating panel grows upward by 6: its bottom edge stays where the page alone would end below the capsule's top",
            CollapsedGeometry.Panel(user, userWork, 220, 1392) == new PanelPlacement(378, 6) && 378 + 6 + 220 == 604 && 604 == user.Surface.Y + 220
            && CollapsedGeometry.Panel(user, userWork, 200, 1392).Top == 378);
        check("Hanging panels add nothing above the header", CollapsedGeometry.Panel(capsule, work, 200) == new PanelPlacement(0, 0)
            && CollapsedGeometry.Panel(notch, work, 200) == new PanelPlacement(0, 0) && CollapsedGeometry.Panel(line, work, 200) == new PanelPlacement(0, 0)
            && CollapsedGeometry.Panel(near, topBar, 200) == new PanelPlacement(48, 0) && CollapsedGeometry.Panel(topBarNear, topBar, 200) == new PanelPlacement(48, 0));
        // The lift (at most 10) is smaller than the bridge (20), so just beyond the bridge the panel still floats below the work area's top.
        check("Just beyond the bridge the panel floats, its top below the work area's top",
            CollapsedGeometry.Panel(CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 21 }, CompactStyle.Capsule, monitor, work), work, 200) == new PanelPlacement(15, 6)
            && CollapsedGeometry.Panel(CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 21, CapsuleHeight = 24 }, CompactStyle.Capsule, monitor, work), work, 200) == new PanelPlacement(11, 6)
            && CollapsedGeometry.Panel(CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 21 }, CompactStyle.Capsule, monitor, topBar), topBar, 200) == new PanelPlacement(63, 6));
        // Settings cannot produce this (the lift is at most 10, below the 20 DIP bridge), so the shape is built by hand.
        var thinLowered = new CollapsedShape(CompactStyle.Capsule, new DipRect(800, 48 + 21, 196, 2), 0.0625, 1, 194, 1, 1, 0);
        check("A lift that would rise above the work area hangs the panel from its top without the extra space",
            CollapsedGeometry.Panel(thinLowered, topBar, 200) == new PanelPlacement(48, 0));
        var noTopBar = CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 20 }, CompactStyle.Capsule, monitor, work);
        check("Without a top taskbar a capsule near the edge still opens from the screen edge; notch and line are unchanged",
            CollapsedGeometry.PanelTop(noTopBar, work, 200) == 0 && CollapsedGeometry.PanelTop(capsule, work, 200) == 0
            && CollapsedGeometry.PanelTop(CollapsedGeometry.Resolve(defaults, CompactStyle.Notch, monitor, topBar), topBar, 200) == 0
            && CollapsedGeometry.PanelTop(CollapsedGeometry.Resolve(defaults, CompactStyle.Line, monitor, topBar), topBar, 200) == 0);
        check("A panel below a capsule near the bottom moves up to stay in the work area, above a capsule over the taskbar",
            CollapsedGeometry.Panel(nearBottom, work, 200) == new PanelPlacement(826, 6) && 826 + 6 + 200 == work.Bottom && nearBottom.Surface.Y == 1048
            && CollapsedGeometry.PanelTop(nearBottom, new DipRect(0, 0, 400, 150), 400) == 0
            && CollapsedGeometry.PanelTop(CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 4000 }, CompactStyle.Capsule, monitor, topBar), topBar, 2000) == 48
            && CollapsedGeometry.PanelTop(nearBottom, new DipRect(double.NaN, double.NaN, 0, 0), 200) == 0);
        // No taskbar at the bottom: the work area reaches the monitor's bottom, and the window keeps 12 DIPs below the panel.
        var fullHeight = new DipRect(0, 0, 1920, 1080);
        var bottomed = CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 4000 }, CompactStyle.Capsule, monitor, fullHeight);
        check("A bottom limit moves the panel up instead of shortening it",
            CollapsedGeometry.PanelTop(bottomed, fullHeight, 400) == 674 && CollapsedGeometry.PanelTop(bottomed, fullHeight, 400, 1080 - 12) == 662
            && CollapsedGeometry.PanelTop(bottomed, fullHeight, 400, 1080 - 12) + 6 + 400 <= 1068 && CollapsedGeometry.PanelTop(nearBottom, work, 200, 1080 - 12) == 826
            && CollapsedGeometry.PanelTop(bottomed, fullHeight, 2000, 1068) == 0 && CollapsedGeometry.PanelTop(bottomed, fullHeight, 400, double.NaN) == 674);
        check("A floating panel too tall to fit below the work area's top with the extra space hangs from that top instead",
            CollapsedGeometry.Panel(bottomed, fullHeight, 1063, 1068) == new PanelPlacement(0, 0) && CollapsedGeometry.Panel(bottomed, fullHeight, 1055) == new PanelPlacement(19, 6)
            && CollapsedGeometry.Panel(bottomed, fullHeight, 1075) == new PanelPlacement(0, 0));
        // The window passes the work area's bottom as the limit (MainWindow.PanelBottom), with no room kept below it, so with no
        // taskbar at the bottom a capsule pulled all the way down is still covered; with a bottom taskbar the panel ends at it,
        // and a capsule pulled down over the taskbar stays below the panel.
        var bottomTaskbar = CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 4000 }, CompactStyle.Capsule, monitor, work);
        var aboveTaskbar = CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 1000 }, CompactStyle.Capsule, monitor, work);
        check("Without a bottom taskbar a capsule pulled to the bottom is covered by its panel; with one the panel ends at the taskbar",
            CollapsedGeometry.Panel(bottomed, fullHeight, 220, fullHeight.Bottom) == new PanelPlacement(854, 6) && 854 + 6 + 220 == bottomed.Surface.Bottom
            && bottomed.Surface.Bottom == 1080
            && CollapsedGeometry.Panel(aboveTaskbar, work, 220, work.Bottom) == new PanelPlacement(806, 6) && 806 + 6 + 220 == aboveTaskbar.Surface.Bottom
            && aboveTaskbar.Surface.Bottom == 1032
            && CollapsedGeometry.Panel(bottomTaskbar, work, 220, work.Bottom) == new PanelPlacement(806, 6) && bottomTaskbar.Surface.Y == 1048);

        var tall = CollapsedGeometry.Resolve(new DeckSettings { NotchHeight = 64, CapsuleHeight = 48 }, CompactStyle.Notch, monitor, work);
        var tallCapsule = CollapsedGeometry.Resolve(new DeckSettings { CapsuleHeight = 48 }, CompactStyle.Capsule, monitor, work);
        var shortNotch = CollapsedGeometry.Resolve(new DeckSettings { NotchHeight = 24 }, CompactStyle.Notch, monitor, work);
        check("Scale is the height over 32 with no cap; the inset grows with it; notch corners keep their sizes",
            Near(tall.Scale, 2) && Near(tall.Inset, 60) && Near(tall.ContentWidth, 238 - 120) && Near(tall.BottomRadius, 12) && Near(tall.Shoulder, 16)
            && Near(tallCapsule.Scale, 1.5) && Near(tallCapsule.BottomRadius, 24) && Near(shortNotch.Scale, 0.75) && Near(shortNotch.Inset, 22.5));
        var narrowTall = CollapsedGeometry.Resolve(new DeckSettings { NotchWidth = 180, NotchHeight = 64 }, CompactStyle.Notch, monitor, work);
        check("Content width is never negative", Near(narrowTall.ContentWidth, 60)
            && CollapsedGeometry.Resolve(new DeckSettings { NotchWidth = 180, NotchHeight = 64 }, CompactStyle.Notch, new DipRect(0, 0, 100, 600), new DipRect(0, 0, 100, 560)).ContentWidth == 0);

        check("The notch spans the monitor and the capsule the work area at their largest widths",
            CollapsedGeometry.Resolve(new DeckSettings { NotchWidth = 1920 }, CompactStyle.Notch, monitor, work).Surface == new DipRect(0, 0, 1920, 32)
            && CollapsedGeometry.Resolve(new DeckSettings { CapsuleWidth = 8000 }, CompactStyle.Capsule, monitor, leftBar) is { Surface: var wide } && Near(wide.X, leftBar.X) && Near(wide.Width, leftBar.Width));

        check("Ranges: offsets for a width, widths for a monitor",
            CollapsedGeometry.OffsetXRange(CompactStyle.Notch, 238, monitor, work) == (-841, 841)
            && CollapsedGeometry.OffsetXRange(CompactStyle.Capsule, 196, monitor, leftBar) == (-832, 832)
            && CollapsedGeometry.OffsetXRange(CompactStyle.Notch, 5000, monitor, work) == (0, 0)
            && CollapsedGeometry.WidthRange(CompactStyle.Notch, monitor, work) == (72, 1920)
            && CollapsedGeometry.WidthRange(CompactStyle.Capsule, monitor, leftBar) == (48, leftBar.Width)
            && CollapsedGeometry.WidthRange(CompactStyle.Line, new DipRect(0, 0, 400, 300), new DipRect(0, 0, 400, 300)) == (24, 400)
            && CollapsedGeometry.WidthRange(CompactStyle.Capsule, new DipRect(0, 0, 40, 300), new DipRect(0, 0, 40, 300)) == (40, 40)
            && CollapsedGeometry.LineTopRange(4, work) == (0, 16));

        check("Thickness snaps monotonically to whole device pixels",
            Near(CollapsedGeometry.SnapToDevicePixels(2.5, 1.5), 4 / 1.5) && Near(CollapsedGeometry.SnapToDevicePixels(4, 1), 4)
            && Near(CollapsedGeometry.SnapToDevicePixels(2.5, 1), 3) && Near(CollapsedGeometry.SnapToDevicePixels(0.1, 2), 0.5)
            && Near(CollapsedGeometry.SnapToDevicePixels(3, double.NaN), 3));
        bool monotonic = true;
        foreach (var dpi in new[] { 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 })
            for (double t = 2; t < 8; t += 0.5)
                monotonic &= CollapsedGeometry.SnapToDevicePixels(t + 0.5, dpi) >= CollapsedGeometry.SnapToDevicePixels(t, dpi);
        check("Snapped thickness never shrinks as the setting grows", monotonic);
        var lineAt150 = CollapsedGeometry.Resolve(new DeckSettings { LineThickness = 2.5 }, CompactStyle.Line, monitor, work, 1.5);
        check("The resolved line is a whole number of device pixels thick", Near(lineAt150.Surface.Height * 1.5, 4) && Near(lineAt150.BottomRadius, lineAt150.Surface.Height / 2));

        // Extreme combinations on small monitors at several scales: nothing throws, nothing is negative or not a number,
        // and every surface lies on the monitor.
        bool sane = true;
        var extremes = new List<DeckSettings>
        {
            new() { NotchWidth = 180, NotchHeight = 64, CapsuleWidth = 140, CapsuleHeight = 64, LineLength = 24, LineThickness = 8 },
            new() { NotchWidth = 800, NotchHeight = 24, CapsuleWidth = 800, CapsuleHeight = 24, LineLength = 600, LineThickness = 2 },
            new() { NotchOffsetX = 4000, CapsuleOffsetX = 4000, LineOffsetX = 4000, CapsuleTop = 200, LineTop = 16, NotchWidth = 800, CapsuleWidth = 800, LineLength = 600 },
            new() { NotchOffsetX = -4000, CapsuleOffsetX = -4000, LineOffsetX = -4000, CapsuleTop = 200, CapsuleHeight = 64 },
            new() { CapsuleTop = 4000, CapsuleHeight = 64, CapsuleWidth = 800, CapsuleOffsetX = 4000 },
            new() { CapsuleTop = 4000, CapsuleHeight = 24, CapsuleWidth = 140 },
            new() { NotchWidth = double.NaN, NotchHeight = double.NegativeInfinity, CapsuleWidth = -50, CapsuleHeight = 0, CapsuleTop = double.NaN, LineThickness = double.NaN, LineOffsetX = double.PositiveInfinity },
            new() { NotchOffsetX = 1e300, CapsuleOffsetX = -1e300, LineOffsetX = 1e300, CapsuleTop = 1e300, LineTop = -1e300, LineLength = 1e300 }
        };
        foreach (var scale in new[] { 1.0, 1.5, 2.0 })
        {
            var tiny = new DipRect(0, 0, 800 / scale, 600 / scale);
            var tinyWork = new DipRect(0, 0, 800 / scale, (600 - 48) / scale);
            foreach (var extreme in extremes)
                foreach (var style in Enum.GetValues<CompactStyle>())
                {
                    try
                    {
                        var shape = CollapsedGeometry.Resolve(extreme, style, tiny, tinyWork, scale);
                        var r = shape.Surface;
                        double[] values = [r.X, r.Y, r.Width, r.Height, shape.Scale, shape.Inset, shape.ContentWidth, shape.BottomRadius, shape.TopRadius, shape.Shoulder];
                        bool inside = r.X >= -1e-9 && r.Y >= -1e-9 && r.Right <= tiny.Width + 1e-9 && r.Bottom <= tiny.Height + 1e-9;
                        bool inWork = style != CompactStyle.Line || r.Bottom <= tinyWork.Bottom + 1e-9;
                        sane &= values.All(double.IsFinite) && values.All(v => v >= 0) && inside && inWork && r.Width > 0 && r.Height > 0;
                        var (offsetMin, offsetMax) = CollapsedGeometry.OffsetXRange(style, r.Width, tiny, tinyWork);
                        var (widthMin, widthMax) = CollapsedGeometry.WidthRange(style, tiny, tinyWork);
                        sane &= offsetMin <= 0 && offsetMax >= 0 && widthMin <= widthMax && widthMax > 0;
                    }
                    catch (Exception) { sane = false; }
                }
        }
        check("Extreme sizes and offsets on an 800 x 600 monitor at 100, 150 and 200 percent stay on the monitor", sane);
        bool degenerate = true;
        foreach (var style in Enum.GetValues<CompactStyle>())
        {
            try
            {
                foreach (var (m, w) in new[] { (new DipRect(0, 0, 0, 0), new DipRect(0, 0, 0, 0)), (new DipRect(0, 0, double.NaN, -5), new DipRect(double.NaN, 0, 10, 10)),
                    (new DipRect(0, 0, 100, 20), new DipRect(500, 500, 10, 10)) })
                {
                    var shape = CollapsedGeometry.Resolve(defaults, style, m, w);
                    var r = shape.Surface;
                    double[] values = [r.X, r.Y, r.Width, r.Height, shape.Scale, shape.Inset, shape.ContentWidth];
                    degenerate &= values.All(double.IsFinite) && values.All(v => v >= 0);
                }
            }
            catch (Exception) { degenerate = false; }
        }
        check("Empty or invalid monitors give an empty shape instead of an error", degenerate);

        var store = new SettingsStore(Path.Combine(root, "collapsed-legacy"), "1.0.4");
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(store.FilePath, """{"AppVersion":"1.0.4","Style":1,"Width":600}""");
        var old = store.Load();
        check("A settings file without the collapsed sizes loads the defaults",
            old.Style == CompactStyle.Capsule && old.NotchWidth == 238 && old.NotchHeight == 32 && old.NotchOffsetX == 0 && old.CapsuleWidth == 196
            && old.CapsuleHeight == 32 && old.CapsuleOffsetX == 0 && old.CapsuleTop == 5 && old.LineLength == 56 && old.LineThickness == 4
            && old.LineTop == 4 && old.LineOffsetX == 0);
        old.CapsuleWidth = 420; old.LineThickness = 5.5; old.NotchOffsetX = -120;
        store.Save(old);
        var again = store.Load();
        check("Collapsed sizes survive saving and loading", again.CapsuleWidth == 420 && again.LineThickness == 5.5 && again.NotchOffsetX == -120
            && File.ReadAllText(store.FilePath).Contains("\"CapsuleTop\": 5"));
    }
}