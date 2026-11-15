using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck;
using BrimDeck.Core;



// The panel theme cards, built by the production CompactStylePreview.Theme from real built-in themes: beside the capsule a card shows
// a corner of the open panel, in the theme's own surface, text and accent colors. Nothing is shown on screen.
internal static class ThemeCardChecks
{
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var inner in Walk(child)) yield return inner;
    }

    private static Color ColorOf(PanelPalette palette, string key) => ((SolidColorBrush)UI.Brush(palette[key].ToString())).Color;

    // The bars drawn on the open panel's corner: small rounded fills of one theme color each.
    private static List<Color> Bars(FrameworkElement card)
        => [.. Walk(card).OfType<Border>().Where(b => b is not IslandBorder && b.Height is > 0 and <= 8 && b.Background is SolidColorBrush)
            .Select(b => ((SolidColorBrush)b.Background).Color)];

    public static void Run(Action<string, bool> check)
    {
        foreach (var id in new[] { PanelThemes.Dark, "dracula", "github-light" })
        {
            var palette = PanelThemes.Resolve(PanelThemes.BuiltIn(id)!);
            var card = CompactStylePreview.Theme(palette);
            var bars = Bars(card);
            check($"theme card {id}: the open panel's corner shows the theme's primary text color", bars.Contains(ColorOf(palette, "textPrimary")));
            check($"theme card {id}: the corner shows the secondary text color", bars.Contains(ColorOf(palette, "textSecondary")));
            check($"theme card {id}: the corner shows the accent color", bars.Contains(ColorOf(palette, "accent")));
            // The corner's panel is the one island border that holds the bars; the capsule and its shadow shape have their own surface.
            var corners = Walk(card).OfType<IslandBorder>().Where(b => b.Child is Grid g && g.Children.OfType<Border>().Any()).ToList();
            check($"theme card {id}: the card has exactly one open-panel corner", corners.Count == 1);
            check($"theme card {id}: the corner's own panel has the theme's surface color", corners.Count == 1 && corners[0].Background is SolidColorBrush s && s.Color == ColorOf(palette, "surface"));
        }
        // Themes that differ show different corners, which is what tells apart capsules that look alike.
        var dark = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!);
        var dracula = PanelThemes.Resolve(PanelThemes.BuiltIn("dracula")!);
        check("two themes' cards differ in the corner's accent", !Bars(CompactStylePreview.Theme(dark)).SequenceEqual(Bars(CompactStylePreview.Theme(dracula))));
    }
}
