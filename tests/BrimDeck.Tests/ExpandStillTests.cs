using System.Text.RegularExpressions;

// The open panel's content only fades while the panel opens and closes. It used to scale between .96 and 1 as well, and text
// under a scale that is not exactly 1 is drawn unhinted at fractional places on the pixel grid: for the last hundred milliseconds of
// the opening (the scale's tail) the text shimmered, and on the frame the scale reached 1 it snapped onto the grid. Measured offscreen
// (tests/BrimDeck.FxPreview --expand-jitter --snap): at a scale of .9992, 1249 of the page's pixels at 100 % (3195 at 150 %) differ
// from the page at rest. What is checked here is that nothing puts a transform on the page's content again; the moves themselves
// are the FxPreview's.
internal static class ExpandStillTests
{
    // Where the page content (ExpandedContent) is given a render transform: in XAML as a property element or attribute, in code by
    // assignment or by an animation of a transform named for it.
    internal static List<string> Transforms(string fileName, string text)
    {
        var found = new List<string>();
        if (fileName.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match element in Regex.Matches(text, @"<(?<name>\w+)\s[^>]*x:Name=""ExpandedContent""[^>]*>"))
            {
                // The transform may be an attribute of the element, or a property element among its direct children, whatever comes
                // before it (Grid.Resources first, say) and however many elements are nested under it; a transform on a nested element
                // is that element's own and does not count.
                string start = element.Value;
                bool transformed = Regex.IsMatch(start, @"\b(RenderTransform|LayoutTransform)=");
                if (!start.EndsWith("/>"))
                {
                    int depth = 0;
                    foreach (Match tag in Regex.Matches(text[(element.Index + element.Length)..], @"<!--.*?-->|<(?<close>/?)(?<tag>[\w.:]+)(?<rest>[^>]*?)(?<self>/?)>", RegexOptions.Singleline))
                    {
                        if (tag.Value.StartsWith("<!--")) continue;
                        if (tag.Groups["close"].Value.Length > 0) { if (--depth < 0) break; continue; }
                        if (depth == 0 && Regex.IsMatch(tag.Groups["tag"].Value, @"\.(RenderTransform|LayoutTransform)$")) transformed = true;
                        if (tag.Groups["self"].Value.Length == 0) depth++;
                    }
                }
                if (transformed) found.Add(fileName + ": ExpandedContent has a render transform");
            }
            return found;
        }
        foreach (Match m in Regex.Matches(text, @"ExpandedContent\s*\.\s*(RenderTransform|LayoutTransform)\s*=[^=]|\bContentScale\b|Animate\(\s*ExpandedContent[^;]*ScaleTransform"))
            found.Add(fileName + ": " + m.Value.Trim());
        return found;
    }

    public static void Run(Action<string, bool> check)
    {
        // The finder itself: what the panel had before the fix is found, what it has now is not.
        check("Expand still: the old scale on the page's content is found (XAML and code)",
            Transforms("old.xaml", """<Grid x:Name="ExpandedContent" Visibility="Hidden" Opacity="0" RenderTransformOrigin="0.5,0"><Grid.RenderTransform><ScaleTransform x:Name="ContentScale"/></Grid.RenderTransform><Grid x:Name="ContentCanvas"/></Grid>""").Count == 1
            && Transforms("old.cs", "Animate(ContentScale, ScaleTransform.ScaleXProperty, expanded ? 1 : .96, duration, content, easeOut);").Count >= 1
            && Transforms("old2.cs", "ExpandedContent.RenderTransform = new ScaleTransform(.96, .96);").Count == 1);
        check("Expand still: a page that only fades, and a transform on something else, are not found",
            Transforms("new.xaml", """<Grid x:Name="ExpandedContent" Visibility="Hidden" Opacity="0"><Grid x:Name="ContentCanvas"><Grid.RenderTransform><ScaleTransform/></Grid.RenderTransform></Grid></Grid>""").Count == 0
            && Transforms("new.cs", "Animate(ExpandedContent, OpacityProperty, expanded ? 1 : 0, duration, content, easeOut);").Count == 0);

        // The XAML finder looks at the element's direct children in whatever order they come, and only those.
        check("Expand still: a transform after Grid.Resources, a transform as an attribute, and one after nested elements are found",
            Transforms("order.xaml", """<Grid x:Name="ExpandedContent"><Grid.Resources><SolidColorBrush x:Key="B"/></Grid.Resources><Grid.RenderTransform><ScaleTransform/></Grid.RenderTransform><Grid x:Name="ContentCanvas"/></Grid>""").Count == 1
            && Transforms("attr.xaml", """<Grid x:Name="ExpandedContent" RenderTransform="{StaticResource Shrink}"><Grid x:Name="ContentCanvas"/></Grid>""").Count == 1
            && Transforms("nested.xaml", """<Grid x:Name="ExpandedContent"><Grid x:Name="A"><Grid x:Name="B"/></Grid><Grid.LayoutTransform><ScaleTransform/></Grid.LayoutTransform></Grid>""").Count == 1);
        check("Expand still: a transform on a nested element, a comment about one, and a sibling's transform are not found",
            Transforms("inner.xaml", """<Grid x:Name="ExpandedContent"><Grid.Resources/><!-- <Grid.RenderTransform> --><Grid x:Name="A"><Grid.RenderTransform><ScaleTransform/></Grid.RenderTransform><Grid x:Name="B"/></Grid></Grid>""").Count == 0
            && Transforms("sibling.xaml", """<Grid><Grid x:Name="ExpandedContent"/><Grid x:Name="Other"><Grid.RenderTransform><ScaleTransform/></Grid.RenderTransform></Grid></Grid>""").Count == 0
            && Transforms("selfclosing.xaml", """<Grid><Grid x:Name="ExpandedContent" Opacity="0"/><Border><Border.RenderTransform><ScaleTransform/></Border.RenderTransform></Border></Grid>""").Count == 0);

        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "BrimDeck.sln"))) source = source.Parent;
        check("Expand still: the source tree is found", source is not null);
        if (source is null) return;
        var app = Path.Combine(source.FullName, "src", "BrimDeck");
        var files = Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(file => (file.EndsWith(".cs") || file.EndsWith(".xaml")) && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)).ToList();
        check("Expand still: the window's XAML is among the sources", files.Any(file => file.EndsWith("MainWindow.xaml")) && File.ReadAllText(Path.Combine(app, "MainWindow.xaml")).Contains("x:Name=\"ExpandedContent\""));
        var found = files.SelectMany(file => Transforms(Path.GetFileName(file), File.ReadAllText(file))).ToList();
        check("Expand still: nothing scales or moves the open panel's content as it fades: " + string.Join("; ", found.Take(5)), found.Count == 0);
    }
}
