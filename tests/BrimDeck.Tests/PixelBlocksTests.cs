using BrimDeck.Core;

// The pixel blocks theme and the pixel art effects it is made of: steps, texture and the pixel clock.
internal static class PixelBlocksTests
{
    internal static void Run(string root, Action<string, bool> check)
    {
        // The theme itself.
        var pixelTheme = PanelThemes.BuiltIn(PanelThemes.PixelBlocks)!;
        var pixel = PanelThemes.Resolve(pixelTheme);
        // The lightest and darkest squares of the grain are surfaces text stands on, as gradient stops are.
        var lightSquare = ThemeColor.Parse("#FFFFFF").WithAlpha((byte)Math.Round(255 * .05)).Over(pixel.Surface);
        var darkSquare = ThemeColor.Black.WithAlpha((byte)Math.Round(255 * .05)).Over(pixel.Surface);
        check("A texture's lightest and darkest squares count as surfaces for contrast",
            pixel.SurfaceStops.Count == 3 && pixel.SurfaceStops.Contains(lightSquare) && pixel.SurfaceStops.Contains(darkSquare)
            && PanelThemes.Tokens.Where(token => token.Against is PanelThemeBackdrop.Surface or PanelThemeBackdrop.SurfaceAndPopup)
                .All(token => pixel[token.Key].MinimumContrast(pixel.SurfaceStops) >= token.Minimum - .001));
        // A dense grain on a mid gray: the tertiary text that reads on the plain surface is moved until it reads on the squares too.
        var grainFile = new PanelThemeFile { Colors = new() { ["surface"] = "#3A3A3A", ["textTertiary"] = "#B0B0B0" }, Effects = new() { Pixel = 2, Texture = .12 } };
        var plainFile = new PanelThemeFile { Colors = new() { ["surface"] = "#3A3A3A", ["textTertiary"] = "#B0B0B0" } };
        var grain = PanelThemes.Build("grain", grainFile);
        check("Text is held to its minimum on a texture's squares", PanelThemes.Build("plain", plainFile).Adjustments.All(a => a.Key != "textTertiary")
            && grain["textTertiary"].MinimumContrast(grain.SurfaceStops) >= PanelThemes.TextContrast - .001 && grain.Adjustments.Any(a => a.Key == "textTertiary"));

        // Ranges and rounding.
        PanelThemeEffectValues PixelEffects(double? size, double? texture) => PanelThemes.Build("p", new PanelThemeFile { Effects = new() { Pixel = size, Texture = texture } }).Effects;
        check("The pixel size is whole DIP from 1 to 4, a half rounding up",
            PixelEffects(2.5, null).Pixel == 3 && PixelEffects(1.4, null).Pixel == 1 && PixelEffects(9, null).Pixel == 4 && PixelEffects(-1, null).Pixel == 0
            && PixelEffects(double.NaN, null).Pixel == 0 && PixelEffects(null, null).Pixel == 0);
        check("The texture is 0 to 0.12, and only with pixel art", PixelEffects(2, .5).Texture == .12 && PixelEffects(2, -1).Texture == 0
            && PixelEffects(0, .05).Texture == 0 && PixelEffects(null, .05) is { Texture: 0 } && !PanelThemes.Build("t", new PanelThemeFile { Effects = new() { Texture = .05 } }).HasOverlay);
        var inheritsPixel = PanelThemes.Build("child", new PanelThemeFile { Base = PanelThemes.PixelBlocks, Effects = new() { Texture = 0 } });
        check("A theme built on pixel blocks keeps its steps and can drop the grain", inheritsPixel.Effects.Pixel == 2 && inheritsPixel.Effects.Texture == 0 && inheritsPixel.SurfaceStops.Count == 1);

        // Copies.
        var pixelCopy = PanelThemes.Build("copy", PanelThemes.Parse(PanelThemes.FillTemplate("copy", pixel, PanelThemes.PixelBlocks)));
        check("A theme made from pixel blocks writes out its pixel size, texture and clock",
            pixelCopy.Effects.Pixel == 2 && pixelCopy.Effects.Texture == .05 && pixelCopy.Clock is { Style: "pixel" } && pixelCopy.Adjustments.Count == 0);
        // The settings page edits the rim and shadow only; saving its draft as a new theme keeps the pixel art it cannot edit.
        var pixelDraft = new PanelThemeDraft(pixelTheme.Id);
        pixelDraft.SetEffect(pixelTheme.File, "rimWidth", 3);
        var pixelSaved = PanelThemes.SaveDraftAsNew(Path.Combine(root, "pixel-draft"), pixelTheme, pixelDraft);
        var pixelSavedPalette = PanelThemes.Resolve(PanelThemes.LoadFolder(Path.Combine(root, "pixel-draft")).Single(theme => theme.Path == pixelSaved));
        check("A pixel theme saved from the editor keeps its pixel size and texture",
            pixelSavedPalette.Effects is { Pixel: 2, Texture: .05, RimWidth: 3 } && pixelSavedPalette.Effects.RimGradient.SequenceEqual(pixel.Effects.RimGradient));

        // The pixel clock.
        var pixelClockSettings = new DeckSettings { ClockStyle = ClockStyle.Pixel };
        pixelClockSettings.Normalize();
        var beyondClockSettings = new DeckSettings { ClockStyle = (ClockStyle)((int)Enum.GetValues<ClockStyle>().Max() + 1) };
        beyondClockSettings.Normalize();
        check("Settings keep the pixel clock and reset a style beyond it", pixelClockSettings.ClockStyle == ClockStyle.Pixel && beyondClockSettings.ClockStyle == ClockStyle.Minimal);

        // Steps: whole device pixels, a half rounding up.
        check("A pixel size is whole device pixels, at least one, a half rounding up",
            PixelStairs.Step(2, 1) == 2 && PixelStairs.Step(2, 1.25) == 3 / 1.25 && PixelStairs.Step(2, 1.75) == 4 / 1.75 && PixelStairs.Step(1, 1.5) == 2 / 1.5
            && PixelStairs.Step(.1, 1) == 1 && PixelStairs.Step(0, 1.5) == 0 && PixelStairs.Step(double.NaN, 1) == 0 && PixelStairs.Step(2, 0) == 2);
        // The stepped outline of a notch (square top, rounded bottom) and a capsule at the common scales.
        bool whole = true, square = true, mirrored = true, stepped = true;
        foreach (var dpi in new[] { 1, 1.25, 1.5, 1.75 })
            foreach (var (w, h, topRadius, bottomRadius) in new[] { (200.0, 32.0, 0.0, 12.0), (160.0, 32.0, 16.0, 16.0), (236.0, 36.0, 0.0, 10.0) })
            {
                var step = PixelStairs.Step(2, dpi);
                var stairs = PixelStairs.Build(RoundedEdges(w, h, topRadius, bottomRadius), 0, 0, w, h, Math.Max(topRadius, bottomRadius), step);
                stepped &= stairs.Count > 4;
                for (int i = 0; i < stairs.Count; i++)
                {
                    var (a, b) = (stairs[i], stairs[(i + 1) % stairs.Count]);
                    square &= Math.Abs(a.X - b.X) < 1e-9 || Math.Abs(a.Y - b.Y) < 1e-9;
                    // The sizes are whole device pixels at every scale, as layout rounding keeps the island's.
                    whole &= Math.Abs(a.X * dpi - Math.Round(a.X * dpi)) < 1e-6 && Math.Abs(a.Y * dpi - Math.Round(a.Y * dpi)) < 1e-6;
                }
                var points = stairs.Select(p => (Math.Round(p.X, 6), Math.Round(p.Y, 6))).ToHashSet();
                mirrored &= stairs.All(p => points.Contains((Math.Round(w - p.X, 6), Math.Round(p.Y, 6))));
                if (topRadius == bottomRadius) mirrored &= stairs.All(p => points.Contains((Math.Round(p.X, 6), Math.Round(h - p.Y, 6))));
            }
        check("Stepped outlines have steps", stepped);
        check("Every edge of a stepped outline is horizontal or vertical", square);
        check("Every step is a whole number of device pixels at 100, 125, 150 and 175 %", whole);
        check("Stepped outlines mirror left to right, and top to bottom where the corners match", mirrored);
    }

    // A rectangle with round corners, top ones of one radius and bottom ones of another, as straight edges clockwise from the top left.
    private static List<((double X, double Y) A, (double X, double Y) B)> RoundedEdges(double w, double h, double topRadius, double bottomRadius)
    {
        var points = new List<(double X, double Y)>();
        void Arc(double cx, double cy, double r, double from)
        {
            for (int i = 0; i <= 32; i++)
            {
                double angle = (from + 90.0 * i / 32) * Math.PI / 180;
                points.Add((cx + r * Math.Cos(angle), cy + r * Math.Sin(angle)));
            }
        }
        Arc(topRadius, topRadius, topRadius, 180); Arc(w - topRadius, topRadius, topRadius, 270);
        Arc(w - bottomRadius, h - bottomRadius, bottomRadius, 0); Arc(bottomRadius, h - bottomRadius, bottomRadius, 90);
        return [.. points.Select((p, i) => (p, points[(i + 1) % points.Count]))];
    }
}
