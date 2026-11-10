using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace BrimDeck;

// The custom quota script box: a code editor with JavaScript colouring drawn from the settings palette.
internal static class ScriptEditor
{
    private static readonly string[] Keywords =
    [
        "async", "await", "function", "return", "const", "let", "var", "if", "else", "for", "of", "in", "while", "do", "break", "continue",
        "switch", "case", "default", "try", "catch", "finally", "throw", "new", "typeof", "instanceof", "delete", "void", "class", "extends",
        "this", "true", "false", "null", "undefined"
    ];

    // The editor's face is Fonts.Code (Consolas, with Chinese from YaHei UI scaled to two columns).
    public static (Border Frame, TextEditor Editor) Create(string text, SettingsPalette palette)
    {
        var editor = new TextEditor
        {
            Text = text, FontFamily = Fonts.Code, FontSize = 12, Foreground = UI.Brush(palette.Primary), Background = Brushes.Transparent,
            WordWrap = false, ShowLineNumbers = false, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            SyntaxHighlighting = Highlighting(palette.Dark)
        };
        editor.Options.ConvertTabsToSpaces = true; editor.Options.IndentationSize = 2;
        editor.Options.EnableHyperlinks = false; editor.Options.EnableEmailHyperlinks = false;
        editor.TextArea.Caret.CaretBrush = UI.Brush(palette.Accent);
        var selection = UI.Brush(palette.Dark ? "#4D88B0EE" : "#40347BDF");
        editor.TextArea.SelectionBrush = selection; editor.TextArea.SelectionBorder = null; editor.TextArea.SelectionForeground = null;
        editor.TextArea.SelectionCornerRadius = 0;
        var frame = new Border
        {
            CornerRadius = new CornerRadius(6), Background = UI.Brush(palette.Input), BorderBrush = UI.Brush(palette.Border), BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8), Height = 210, Child = editor
        };
        // The frame follows the other inputs: its border takes the accent colour while the editor has focus.
        editor.IsKeyboardFocusWithinChanged += (_, _) => frame.BorderBrush = UI.Brush(editor.IsKeyboardFocusWithin ? (palette.Dark ? "#348AEB" : "#2374DE") : palette.Border);
        return (frame, editor);
    }

    private static IHighlightingDefinition Highlighting(bool dark)
    {
        string keyword = dark ? "#82AEEB" : "#1F5FBF", text = dark ? "#D9A17A" : "#A3531E", comment = dark ? "#7F8591" : "#767B87", number = dark ? "#7CC7B6" : "#1A7F6E";
        string Quoted(string mark, bool multiline) =>
            $"""<Span color="String" multiline="{(multiline ? "true" : "false")}"><Begin>{mark}</Begin><End>{mark}</End><RuleSet><Span begin="\\" end="." /></RuleSet></Span>""";
        var xshd = $"""
            <SyntaxDefinition name="BrimDeckJavaScript" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="{comment}" />
              <Color name="String" foreground="{text}" />
              <Color name="Keyword" foreground="{keyword}" />
              <Color name="Number" foreground="{number}" />
              <RuleSet>
                <Span color="Comment" begin="//" />
                <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
                {Quoted("\"", false)}
                {Quoted("'", false)}
                {Quoted("`", true)}
                <Keywords color="Keyword">{string.Concat(Keywords.Select(word => $"<Word>{word}</Word>"))}</Keywords>
                <Rule color="Number">\b0[xX][0-9a-fA-F]+|(\b\d+(\.[0-9]+)?|\.[0-9]+)([eE][+-]?[0-9]+)?</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
