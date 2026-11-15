using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

// The clipboard and file transfer page: what it keeps and the rules for keeping it. The clipboard column holds copied text and
// pictures and the phrases the user writes; the file transfer column holds the places of files, never copies of them.

public enum ClipKind { Text, Link, Code, Color, Image }

public sealed class ClipEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ClipKind Kind { get; set; }
    // The plain text of a text entry; empty for a picture.
    public string Text { get; set; } = "";
    // The rich forms the copying application also offered (HTML, RTF), pasted with the text unless pasting as plain text. They are
    // kept in files of their own beside the history, read only to paste (ShelfService), as they are often many times the text and
    // the page never shows them. Histories saved before held them here; read in, they are moved to those files (TakeRich).
    [JsonPropertyName("Html"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SavedHtml { get; set; }
    [JsonPropertyName("Rtf"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SavedRtf { get; set; }
    // A picture's size in pixels; the picture itself is kept in its own file beside the history (ShelfFiles).
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public DateTimeOffset Copied { get; set; }
    // The application that copied it (process name), shown in the details; empty for a phrase.
    public string Source { get; set; } = "";
    // The categories it is in, "常用" among them; an entry can be in several.
    private List<Guid> _categories = [];
    public List<Guid> Categories { get => _categories; set { _categories = value; CategoriesSpecified = true; } }
    // An explicit list, including [], is a choice. Older single-category histories omitted it.
    [JsonIgnore] public bool CategoriesSpecified { get; private set; }
    // The first of them, also written for the versions up to 1.3.0-alpha.2, which knew one category per entry and read only this.
    [JsonPropertyName("Category")] public Guid? LegacyCategory { get => Shelf.First(Categories); set => Shelf.AddLegacy(Categories, value); }
    // A phrase the user wrote here instead of copying it.
    public bool Manual { get; set; }
}

public sealed class TransferFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = "";
    public DateTimeOffset Added { get; set; }
    // The categories it is in; a file can be in several. "Category" as for ClipEntry.
    public List<Guid> Categories { get; set; } = [];
    [JsonPropertyName("Category")] public Guid? LegacyCategory { get => Shelf.First(Categories); set => Shelf.AddLegacy(Categories, value); }
    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : Path;
}

public sealed class ShelfCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    // "#RRGGBB", taken from the theme's swatches when the category is made.
    public string Color { get; set; } = "#9CB9FF";
    // Clipboard only: whether its entries are kept until deleted, or go with the history (at exit, or after the time it is kept
    // for: DeckSettings.ClipboardKeepHistory). "常用" always keeps. A category made now starts going with the history
    // (AddCategory); one saved before the choice existed has none and stays kept, as every category was then.
    [JsonPropertyName("Keep")] public bool? KeepChosen { get; set; }
    [JsonIgnore] public bool Keep { get => KeepChosen ?? true; set => KeepChosen = value; }
    // What goes in by itself (ShelfRules); null when nothing does.
    public ShelfRule? Rule { get; set; }
}

public enum ShelfSide { Clipboard, Files }

public sealed class ShelfData
{
    // "常用", the clipboard's own category: always first, never renamed or deleted; the phrases the user writes go there.
    public static readonly Guid Favorites = new("5f1c2b7e-0c4a-4f2e-9a61-3d2f8e1b6c01");

    public List<ClipEntry> Clips { get; set; } = [];
    public List<TransferFile> Files { get; set; } = [];
    public List<ShelfCategory> ClipCategories { get; set; } = [new ShelfCategory { Id = Favorites, Name = "常用" }];
    public List<ShelfCategory> FileCategories { get; set; } = [];

    public List<ShelfCategory> Categories(ShelfSide side) => side == ShelfSide.Clipboard ? ClipCategories : FileCategories;
    // The categories the user made, which can be renamed, colored, reordered and deleted.
    public IEnumerable<ShelfCategory> OwnCategories(ShelfSide side) => Categories(side).Where(category => category.Id != Favorites);

    // A phrase, and an entry in "常用" or in a category set to keep, stays until the user deletes it; the rest is history, cleared
    // when BrimDeck exits (and when it starts, after an exit that did not clear it), or kept for a time (Shelf.Expired).
    public bool Kept(ClipEntry clip) => clip.Manual || clip.Categories.Any(id => id == Favorites || ClipCategories.FirstOrDefault(c => c.Id == id)?.Keep == true);

    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };
    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, Options);
    public static ShelfData Deserialize(byte[] bytes)
    {
        var data = JsonSerializer.Deserialize<ShelfData>(bytes, Options) ?? new();
        data.Clips = [.. (data.Clips ?? []).Where(clip => clip is not null && (clip.Kind == ClipKind.Image || clip.Text.Length > 0))];
        data.Files = [.. (data.Files ?? []).Where(file => file is not null && file.Path.Length > 0)];
        data.ClipCategories = [.. (data.ClipCategories ?? []).Where(category => category is not null)];
        data.FileCategories = [.. (data.FileCategories ?? []).Where(category => category is not null && category.Id != Favorites)];
        foreach (var category in data.ClipCategories.Concat(data.FileCategories))
            if (category.Rule is { } rule) rule.Conditions = [.. (rule.Conditions ?? []).Where(condition => condition is not null)];
        // "常用" comes first, also in history saved before it existed, and the phrases written then are in it.
        data.ClipCategories.RemoveAll(category => category.Id == Favorites);
        data.ClipCategories.Insert(0, new ShelfCategory { Id = Favorites, Name = "常用" });
        // A category that is gone is dropped from the entries in it; an entry left in none goes back to the whole list.
        var clips = data.ClipCategories.Select(category => category.Id).ToHashSet();
        foreach (var clip in data.Clips)
        {
            bool legacyPhrase = clip.Manual && !clip.CategoriesSpecified && (clip.Categories?.Count ?? 0) == 0;
            clip.Categories = [.. (clip.Categories ?? []).Where(clips.Contains).Distinct()];
            if (legacyPhrase) clip.Categories.Add(Favorites);
        }
        var files = data.FileCategories.Select(category => category.Id).ToHashSet();
        foreach (var file in data.Files) file.Categories = [.. (file.Categories ?? []).Where(files.Contains).Distinct()];
        return data;
    }
}

// What dragging files out of the file transfer does (DeckSettings.TransferDragOut). Move lets the target move the originals; a
// target that only takes copies (a chat window, a browser) still copies them. Files moved away leave the list.
public enum TransferDragOut { Copy, Move }

// How long the history is kept past an exit (DeckSettings.ClipboardKeepFor): 1, 7 or 30 days.
public enum HistoryLength { Day, Week, Month }

public static class Shelf
{
    // The most text one entry keeps; longer copies are not recorded.
    public const int MaxText = 1_000_000;

    // Which kind a copied text is: a single web address, a color value, code, or plain text.
    public static ClipKind Classify(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return ClipKind.Text;
        if (!trimmed.Contains('\n') && trimmed.Length <= 2048 && IsLink(trimmed)) return ClipKind.Link;
        if (!trimmed.Contains('\n') && TryColor(trimmed, out _)) return ClipKind.Color;
        return LooksLikeCode(trimmed) ? ClipKind.Code : ClipKind.Text;
    }

    public static bool IsLink(string text)
        => !text.Any(char.IsWhiteSpace) && Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.Host.Length > 0;

    // The host and the rest of a link, drawn in two colors.
    public static (string Host, string Path) SplitLink(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)) return (text, "");
        string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        if (!uri.IsDefaultPort) host += ":" + uri.Port;
        string rest = uri.PathAndQuery == "/" ? "" : uri.PathAndQuery;
        return (host, rest + uri.Fragment);
    }

    private static readonly Regex CodeLine = new(@"^\s*(using |import |from \S+ import|def |class |public |private |internal |static |function |const |let |var |return |if\s*\(|for\s*\(|while\s*\(|#include|package |fn |func |<\w+[ >/]|\}|\{|//|/\*|#!)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    // Code: several lines of which a good share start like code or end with ; { or }, or one line with clear code punctuation.
    public static bool LooksLikeCode(string text)
    {
        try
        {
            var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0).Take(200).ToList();
            if (lines.Count == 0) return false;
            int coded = lines.Count(line => CodeLine.IsMatch(line) || line.TrimEnd().EndsWith(';') || line.TrimEnd().EndsWith('{') || line.TrimEnd().EndsWith('}'));
            if (lines.Count >= 2) return coded * 2 >= lines.Count;
            string one = lines[0].Trim();
            return one.Length > 8 && one.EndsWith(';') && (one.Contains('(') || one.Contains('=')) || CodeLine.IsMatch(one) && (one.Contains('(') || one.Contains('{') || one.Contains('='));
        }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static readonly Regex Hex = new(@"^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.CultureInvariant);
    private static readonly Regex Rgb = new(@"^rgba?\(\s*(\d{1,3})\s*[, ]\s*(\d{1,3})\s*[, ]\s*(\d{1,3})\s*(?:[,/]\s*[\d.]+%?\s*)?\)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    // A color written as #RGB, #RRGGBB, #AARRGGBB or rgb(r, g, b); the color without its alpha.
    public static bool TryColor(string text, out (byte R, byte G, byte B) color)
    {
        color = default;
        text = text.Trim();
        if (Hex.Match(text) is { Success: true } hex)
        {
            string digits = hex.Groups[1].Value;
            if (digits.Length == 3) digits = string.Concat(digits.Select(c => $"{c}{c}"));
            if (digits.Length == 8) digits = digits[2..];
            color = (Convert.ToByte(digits[..2], 16), Convert.ToByte(digits[2..4], 16), Convert.ToByte(digits[4..], 16));
            return true;
        }
        if (Rgb.Match(text) is { Success: true } rgb)
        {
            var parts = new[] { 1, 2, 3 }.Select(i => int.Parse(rgb.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
            if (parts.Any(part => part > 255)) return false;
            color = ((byte)parts[0], (byte)parts[1], (byte)parts[2]);
            return true;
        }
        return false;
    }

    // The three ways a color is written in the details; a click pastes the one clicked.
    public static (string Hex, string Rgb, string Hsl) ColorForms((byte R, byte G, byte B) c)
    {
        double r = c.R / 255d, g = c.G / 255d, b = c.B / 255d, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, h = 0, s = 0;
        if (max != min)
        {
            double d = max - min;
            s = l > .5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h *= 60;
        }
        return ($"#{c.R:X2}{c.G:X2}{c.B:X2}", $"rgb({c.R}, {c.G}, {c.B})",
            string.Create(CultureInfo.InvariantCulture, $"hsl({Math.Round(h)}, {Math.Round(s * 100)}%, {Math.Round(l * 100)}%)"));
    }

    // The first line that has text and how many lines follow it, for the one-line row ("+N 行").
    public static (string First, int More) FirstLine(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int first = Array.FindIndex(lines, line => line.Trim().Length > 0);
        if (first < 0) return ("", 0);
        int last = Array.FindLastIndex(lines, line => line.Trim().Length > 0);
        return (lines[first].Trim().Replace('\t', ' '), last - first);
    }

    public static int LineCount(string text) => text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1;

    // What a text holds, for the row with room to say it (ClipView.Three): its characters, line breaks apart, and its lines when
    // it has more than one.
    public static string Count(string text)
    {
        string body = text.Trim().Replace("\r\n", "\n");
        int characters = new StringInfo(body.Replace("\n", "")).LengthInTextElements, lines = LineCount(body);
        string count = Loc.T($"{characters} 字", characters == 1 ? "1 char" : $"{characters} chars");
        return lines > 1 ? count + Loc.T($" · {lines} 行", $" · {lines} lines") : count;
    }

    // A file's name and its extension as the rows show them: an extension of up to four letters (.docx) is kept whole when the
    // name is cut; a longer one, or a name that is nothing but one, counts as part of the name.
    public static (string Stem, string Extension) SplitName(string name)
    {
        string extension = Path.GetExtension(name);
        if (extension.Length is < 2 or > 5 || extension.Length == name.Length) extension = "";
        return (name[..^extension.Length], extension);
    }

    // A file's name under a large icon (FileView.Grid, Row and Column), on one or two lines of `width` as `measure` measures text.
    // The first line takes as much of the name as fits; where that would break a word or a number written in Latin letters and
    // digits ("1.3." and "0"), it ends at the space before it instead, when that leaves it at least half full. Short of room, the
    // name is cut at the end of its stem, and the extension always shows after it, never broken over the two lines. Two: there is a second line, which is Second and the extension; else
    // the one line is First and the extension.
    public sealed record TileLines(string First, string Second, string Extension, bool Two)
    {
        public bool Cut => First.EndsWith('…') || Second.EndsWith('…');
    }
    public static TileLines TileName(string name, double width, int lines, Func<string, double> measure)
    {
        var (stem, extension) = SplitName(name);
        if (measure(name) <= width) return new(stem, "", extension, false);
        // The longest start of `text` that fits `room` with `tail` after it; at least one character, and never half of a pair.
        string Fit(string text, double room, string tail)
        {
            int low = 1, high = text.Length;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (measure(text[..middle] + tail) <= room) low = middle; else high = middle - 1;
            }
            if (low < text.Length && char.IsLowSurrogate(text[low])) low = Math.Max(1, low - 1);
            return text[..Math.Min(low, text.Length)];
        }
        string Cut(string text, double room) => text.Length == 0 ? "" : Fit(text, room, "…").TrimEnd() + "…";
        if (lines < 2 || stem.Length == 0) return new(Cut(stem, width - measure(extension)), "", extension, false);
        string first = Fit(stem, width, "");
        if (first.Length == stem.Length) return new(stem, "", extension, true);
        static bool Word(char c) => c < 0x2E80 && !char.IsWhiteSpace(c);
        if (Word(first[^1]) && Word(stem[first.Length]) && first.LastIndexOf(' ') is var space && space >= first.Length / 2) first = first[..(space + 1)];
        string rest = stem[first.Length..].TrimStart();
        if (measure(rest + extension) <= width) return new(first.TrimEnd(), rest, extension, true);
        return new(first.TrimEnd(), Cut(rest, width - measure(extension)), extension, true);
    }

    // A copy is added at the top. The same text copied again moves its entry up instead of adding a second one, keeping its categories
    // (and its rich forms, unless the new copy brings its own: ShelfService.Copied).
    public static ClipEntry Add(ShelfData data, ClipEntry entry)
    {
        if (entry.Kind != ClipKind.Image)
        {
            var same = data.Clips.FirstOrDefault(clip => clip.Kind != ClipKind.Image && clip.Text == entry.Text);
            if (same is not null)
            {
                data.Clips.Remove(same);
                same.Copied = entry.Copied; same.Kind = entry.Kind;
                if (entry.Source.Length > 0) same.Source = entry.Source;
                data.Clips.Insert(0, same);
                return same;
            }
        }
        data.Clips.Insert(0, entry);
        return entry;
    }

    // The same for the file transfer, which has no "常用": every file in "全部", a category's files in it. Only the records go; the
    // files themselves stay where they are.
    public static List<TransferFile> ClearableFiles(ShelfData data, Guid? category)
        => data.Files.Where(file => category is not { } id || file.Categories.Contains(id)).ToList();
    // "清除" in the file transfer: in "全部" every record goes (returned); in a category, the files only in it go and the others leave it.
    public static List<TransferFile> ClearFiles(ShelfData data, Guid? category) => Clear(ClearableFiles(data, category), category, file => file.Categories);

    // An entry copied again from the page: it goes to the top as a new copy would, with the time of this copy; it keeps its categories.
    public static void Recopied(ShelfData data, ClipEntry entry, DateTimeOffset now)
    {
        if (!data.Clips.Remove(entry)) return;
        entry.Copied = now;
        data.Clips.Insert(0, entry);
    }

    // The history, without what is kept (ShelfData.Kept): cleared when BrimDeck exits.
    public static List<ClipEntry> History(ShelfData data) => data.Clips.Where(clip => !data.Kept(clip)).ToList();

    // The rich forms a history saved before held in its entries, each entry's taken out of it to be kept in its own files.
    public static List<(ClipEntry Clip, string Html, string Rtf)> TakeRich(ShelfData data)
    {
        var taken = new List<(ClipEntry, string, string)>();
        foreach (var clip in data.Clips)
        {
            if (clip.SavedHtml is null && clip.SavedRtf is null) continue;
            if ((clip.SavedHtml ?? "").Length > 0 || (clip.SavedRtf ?? "").Length > 0) taken.Add((clip, clip.SavedHtml ?? "", clip.SavedRtf ?? ""));
            clip.SavedHtml = null; clip.SavedRtf = null;
        }
        return taken;
    }

    // The history kept past an exit goes once it is older than the length chosen, counted from its last copy.
    public static DateTimeOffset KeptSince(HistoryLength length, DateTimeOffset now)
        => now.AddDays(length switch { HistoryLength.Day => -1, HistoryLength.Week => -7, _ => -30 });
    public static List<ClipEntry> Expired(ShelfData data, HistoryLength length, DateTimeOffset now)
    {
        var since = KeptSince(length, now);
        return data.Clips.Where(clip => clip.Copied < since && !data.Kept(clip)).ToList();
    }

    // What "清除" reaches where it is pressed: in "全部" everything not kept (ShelfData.Kept: phrases, "常用" and the categories set
    // to keep stay); in one of the user's categories, what is in it. Nothing in "常用".
    public static List<ClipEntry> Clearable(ShelfData data, Guid? category) => category switch
    {
        null => History(data),
        { } id when id == ShelfData.Favorites => [],
        { } id => data.Clips.Where(clip => clip.Categories.Contains(id)).ToList()
    };
    // "清除" itself: in "全部" all it reaches is deleted (returned). In a category, the entries only in it are deleted; those also in
    // another category only leave this one, so clearing one category never takes anything out of another.
    public static List<ClipEntry> Clear(ShelfData data, Guid? category) => Clear(Clearable(data, category), category, clip => clip.Categories);
    private static List<T> Clear<T>(List<T> reached, Guid? category, Func<T, List<Guid>> categories)
    {
        if (category is not { } id) return reached;
        var gone = reached.Where(item => categories(item).All(other => other == id)).ToList();
        foreach (var item in reached) categories(item).Remove(id);
        return gone;
    }

    // Puts entries in a category, or takes them out of it; their other categories stay.
    public static void SetCategory(IEnumerable<List<Guid>> categories, Guid id, bool add)
    {
        foreach (var list in categories)
            if (!add) list.Remove(id);
            else if (!list.Contains(id)) list.Add(id);
    }

    // The "Category" of the versions with one category per entry (ClipEntry.LegacyCategory): written as the first, read in first.
    public static Guid? First(List<Guid> categories) => categories.Count > 0 ? categories[0] : null;
    public static void AddLegacy(List<Guid> categories, Guid? id)
    {
        if (id is { } known && !categories.Contains(known)) categories.Insert(0, known);
    }

    // Files are added at the top in the order they were dropped; a file already on the shelf moves up rather than appearing twice.
    // Returns the files added or moved up.
    public static List<TransferFile> AddFiles(ShelfData data, IEnumerable<string> paths, Guid? category, DateTimeOffset now)
    {
        var added = new List<TransferFile>();
        var list = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var existing = data.Files.FirstOrDefault(file => string.Equals(file.Path, list[i], StringComparison.OrdinalIgnoreCase));
            if (existing is not null) data.Files.Remove(existing);
            var file = existing ?? new TransferFile { Path = list[i] };
            file.Added = now;
            if (category is { } id && !file.Categories.Contains(id)) file.Categories.Add(id);
            data.Files.Insert(0, file);
            added.Add(file);
        }
        return added;
    }

    // The name shown for a category; "常用" in the language of the interface.
    public static string CategoryName(ShelfCategory category) => category.Id == ShelfData.Favorites ? Loc.T("常用", "Favorites") : category.Name;

    // A category the user makes goes last, in the next of the colors given (the panel theme's swatches).
    public static ShelfCategory AddCategory(ShelfData data, ShelfSide side, string name, IReadOnlyList<string> colors)
    {
        var categories = data.Categories(side);
        int own = data.OwnCategories(side).Count();
        var category = new ShelfCategory { Name = name, Color = colors.Count > 0 ? colors[own % colors.Count] : "#9CB9FF", Keep = false };
        categories.Add(category);
        return category;
    }

    // The user's categories in a new order; "常用" stays first, and a category missing from the order keeps its place at the end.
    public static void Reorder(ShelfData data, ShelfSide side, IReadOnlyList<Guid> order)
    {
        var categories = data.Categories(side);
        var rank = new Dictionary<Guid, int>();
        for (int i = 0; i < order.Count; i++) rank.TryAdd(order[i], i);
        var sorted = categories.Where(category => category.Id != ShelfData.Favorites)
            .OrderBy(category => rank.TryGetValue(category.Id, out int at) ? at : int.MaxValue).ToList();
        categories.RemoveAll(category => category.Id != ShelfData.Favorites);
        categories.AddRange(sorted);
    }

    // A phrase the user writes goes to the top of "常用".
    public static ClipEntry AddPhrase(ShelfData data, string text, DateTimeOffset now)
    {
        var phrase = new ClipEntry { Kind = Classify(text), Text = text, Manual = true, Copied = now, Categories = [ShelfData.Favorites] };
        data.Clips.Insert(0, phrase);
        return phrase;
    }

    public static void DeleteCategory(ShelfData data, ShelfSide side, Guid id)
    {
        if (id == ShelfData.Favorites) return;
        data.Categories(side).RemoveAll(category => category.Id == id);
        if (side == ShelfSide.Clipboard) foreach (var clip in data.Clips) clip.Categories.Remove(id);
        else foreach (var file in data.Files) file.Categories.Remove(id);
    }

    // A search matches the text of an entry, or a file's name, ignoring case; pictures match nothing.
    public static bool Matches(string text, string query) => query.Trim().Length > 0 && text.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase);

    // Where the query occurs in a line, for the blue background under the match.
    public static IEnumerable<(int Start, int Length)> MatchesIn(string line, string query)
    {
        query = query.Trim();
        if (query.Length == 0) yield break;
        int at = 0;
        while (at < line.Length && (at = line.IndexOf(query, at, StringComparison.CurrentCultureIgnoreCase)) >= 0) { yield return (at, query.Length); at += query.Length; }
    }

    // File sizes as the Explorer shows them: whole bytes below 1 KB, then one decimal below 10 and none above.
    public static string Size(long bytes)
    {
        if (bytes < 1024) return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes; int unit = -1;
        do { value /= 1024; unit++; } while (value >= 1024 && unit < units.Length - 1);
        return (value < 10 ? value.ToString("0.#", CultureInfo.InvariantCulture) : value.ToString("0", CultureInfo.InvariantCulture)) + " " + units[unit];
    }

    // A time on a row: "刚刚", minutes and hours today, "昨天", then the date.
    public static string When(DateTimeOffset time, DateTimeOffset now)
    {
        var span = now - time;
        if (span < TimeSpan.FromMinutes(1)) return Loc.T("刚刚", "Now");
        if (span < TimeSpan.FromHours(1)) return Loc.T($"{(int)span.TotalMinutes} 分钟前", $"{(int)span.TotalMinutes} min");
        var local = time.ToLocalTime(); var today = now.ToLocalTime().Date;
        if (local.Date == today) return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today.AddDays(-1)) return Loc.T("昨天", "Yesterday");
        return local.Year == today.Year ? local.ToString(Loc.T("M月d日", "MMM d"), CultureInfo.CurrentCulture) : local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
    // The time on a row with room for the hour as well (ClipView.Three): what When says, and after it the time of day wherever When
    // does not give it: within the last hour, yesterday and on a date of this year. Earlier today it is the time of day alone, and
    // a date of another year stays a date.
    public static string WhenExact(DateTimeOffset time, DateTimeOffset now)
    {
        string when = When(time, now);
        var local = time.ToLocalTime(); var today = now.ToLocalTime().Date;
        string clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (when == clock) return when;
        return now - time < TimeSpan.FromHours(1) || local.Year == today.Year ? when + " · " + clock : when;
    }
}

// The sizes of the page at a height and width (the table in the design): margins, row and header heights, type sizes and what the
// file column has room for. Side by side, the file column takes its share of the room between the margins (DeckSettings.ShelfFileShare,
// set by dragging the divider or in the settings), never under MinFile, and leaves the clipboard MinClip where the page is wide
// enough. Stacked (ShelfArrangement.Stacked), the two columns are cards of one width (ShelfDeck). The cards reach out past the
// page's margins, to CardSide from the panel's edges, so that a column in its card (CardPad and the card's edge inside it) starts
// where it does side by side, at Side; the card in front leaves the side strip of the other showing (Peek). The cards start
// CardTop below the toolbar and end CardBottom above the panel's bottom edge. Showing one column only (ShelfShown), it takes the
// whole room between the margins and the other is 0 wide; it is never stacked.
public sealed record ShelfLayout(ShelfTier Tier, double Side, double Gap, double Header, double Row, double Body, double Aux, double Title,
    double FileColumn, double ClipColumn, bool FileSize, bool FileDate, bool TwoLines, double Thumb, double ThumbHeight, double Icon, double DetailBody,
    bool Stacked = false, double Peek = 0, double CardTop = 0, double CardBottom = 0, double CardPad = 0, double CardSide = 0,
    ClipView ClipView = ClipView.Single, FileView FileView = FileView.Single, int FileColumns = 1, int ClipColumns = 1)
{
    // How each column shows its entries (ClipView, FileView), and the sizes of the ways added to the one-line rows; where three
    // numbers stand together they are for the compact, standard and spacious page.
    private double Step(double compact, double standard, double spacious) => Tier switch { ShelfTier.Compact => compact, ShelfTier.Standard => standard, _ => spacious };
    // A file under a large icon (FileView.Grid, Row and Column): the icon, the cell's height, the name's size and line, and the
    // space above the icon and between it and the name. In the grid a cell is at least TileMin wide and the columns share the
    // column's width; in the one row every cell is TileAcross wide; in the one column a cell is as wide as the column, so the
    // name has the whole width, and the icon stays as it is.
    public double TileIcon => Step(24, 32, 40);
    public double TileHeight => Step(60, 69, 88);
    public double TileMin => Step(56, 68, 76);
    public double TileAcross => Step(60, 72, 80);
    public double TileText => Step(10.5, 11, 12);
    public double TileLine => Step(14, 14, 16);
    public double TileTop => Step(4, 4, 6);
    public double TileGap => Step(2, 3, 5);
    // A file on two lines (FileView.Two): the row's height; its icon is the tile's.
    public double TwoRow => Step(36, 44, 54);
    // A clipboard entry three lines high (ClipView.Three): a line of its text, the space above and below, the width of the
    // three lines of facts at its right, and a picture's height.
    public double ThreeLine => Math.Round(Body * 1.45);
    public double ThreePad => Step(4, 5, 6);
    public double ThreeRow => 3 * ThreeLine + 2 * ThreePad;
    public double ThreeSide => Step(84, 96, 104);
    public double ThreeThumb => Step(36, 48, 60);
    // A clipboard entry as a block (ClipView.Blocks): the least width of one, the space between blocks, a block's height, the
    // lines of text it shows with their size and height, its padding, and the height of the line at its bottom.
    public double BlockMin => Step(96, 104, 108);
    public double BlockGap => Step(5, 6, 8);
    public double BlockHeight => Step(56, 74, 100);
    public int BlockLines => (int)Step(2, 3, 4);
    public double BlockText => Step(11.5, 12, 12.5);
    public double BlockLine => Step(15, 15, 16.5);
    public double BlockPad => Step(6, 7, 9);
    public double BlockSide => Step(8, 9, 10);
    public double BlockFoot => Step(18, 20, 23);
    // The columns a width holds: of the file grid, and of the clipboard's blocks with the space between them.
    public static int TileColumns(double width, double least) => Math.Max(1, (int)Math.Floor(width / least));
    public static int BlockColumns(double width, double least, double gap) => Math.Max(1, (int)Math.Floor((width + gap) / (least + gap)));

    // The file transfer's share of the width side by side, in percent: the default and the range the divider and the setting keep to.
    public const double DefaultShare = 40, MinShare = 20, MaxShare = 80;
    // The narrowest the columns are made side by side.
    public const double MinFile = 170, MinClip = 180;
    // In one column (FileView.Column) the file transfer may be a narrow strip, which is what that way of showing files is for:
    // down to ColumnFile wide (its name, the filters' "⌄" and "清除" still fit its header) and ColumnShare of the width.
    public const double ColumnFile = 104, ColumnShare = 8;
    public static double LeastFile(FileView view) => view == FileView.Column ? ColumnFile : MinFile;
    public static double LeastShare(FileView view) => view == FileView.Column ? ColumnShare : MinShare;

    public static ShelfLayout For(double width, double height, double share = DefaultShare, bool stacked = false, ShelfShown shown = ShelfShown.Both,
        ClipView clipView = ClipView.Single, FileView fileView = FileView.Single)
    {
        stacked &= shown == ShelfShown.Both;
        var tier = height < 180 ? ShelfTier.Compact : height < 280 ? ShelfTier.Standard : ShelfTier.Spacious;
        var (side, gap, header, row, body, aux, title) = tier switch
        {
            ShelfTier.Compact => (14d, 24d, 26d, 22d, 11.5, 10.5, 12d),
            ShelfTier.Standard => (20d, 32d, 30d, 26d, 12.5, 11d, 13d),
            _ => (24d, 40d, 32d, 30d, 13d, 11.5, 13d)
        };
        var (peek, cardBottom, pad) = tier switch
        {
            ShelfTier.Compact => (44d, 6d, 7d),
            ShelfTier.Standard => (50d, 8d, 9d),
            _ => (56d, 8d, 11d)
        };
        // The card's edge (1) and its padding bring the column back to the page's margin.
        double cardTop = 2, cardSide = side - pad - 1;
        double files, clips;
        if (stacked)
            files = clips = Math.Max(0, width - 2 * cardSide - peek - 2 * (pad + 1));
        else if (shown != ShelfShown.Both)
        {
            double whole = Math.Max(0, width - 2 * side);
            files = shown == ShelfShown.Files ? whole : 0; clips = shown == ShelfShown.Clipboard ? whole : 0;
        }
        else
        {
            double inner = Math.Max(0, width - 2 * side - gap);
            files = FileWidth(inner, share, fileView); clips = Math.Max(0, inner - files);
        }
        bool spacious = tier == ShelfTier.Spacious;
        // A file's size shows on its row at every width the column has (it was hidden under 210, where the row then said nothing
        // of the file but its name); the date still needs the spacious page and 300.
        var layout = new ShelfLayout(tier, side, gap, header, row, body, aux, title, files, clips, files > 0, spacious && files >= 300, spacious,
            spacious ? 48 : 34, spacious ? 30 : Math.Max(14, row - 8), spacious ? 18 : 16, DetailText,
            stacked, stacked ? peek : 0, stacked ? cardTop : 0, stacked ? cardBottom : 0, stacked ? pad : 0, stacked ? cardSide : 0, clipView, fileView);
        return layout with
        {
            FileColumns = fileView == FileView.Grid ? TileColumns(files, layout.TileMin) : 1,
            ClipColumns = clipView == ClipView.Blocks ? BlockColumns(clips, layout.BlockMin, layout.BlockGap) : 1
        };
    }
    // The file column's width side by side for the room both columns share (less the gap between them).
    public static double FileWidth(double inner, double share, FileView view = FileView.Single)
    {
        double want = Math.Round(inner * ClampShare(share, view) / 100);
        return Math.Max(LeastFile(view), Math.Min(want, inner - MinClip));
    }
    // The share a dragged divider leaves the file column: its width over the room both columns share, in whole percent.
    public static double ShareOf(double fileWidth, double inner, FileView view = FileView.Single) => inner <= 0 ? DefaultShare : ClampShare(Math.Round(fileWidth / inner * 100), view);
    public static double ClampShare(double share, FileView view = FileView.Single) => double.IsFinite(share) ? Math.Clamp(share, LeastShare(view), MaxShare) : DefaultShare;
    // The details' text is one size, whatever the panel's: reading a whole entry, the text should not grow and shrink with it.
    public const double DetailText = 13.5;
    // The details show their facts in a column at the right from this width on, else in a line at the bottom.
    public static bool DetailSide(double width) => width >= 560;
    // The search box: 36 % of the panel's width, between 180 and 280.
    public static double SearchWidth(double width) => Math.Round(Math.Clamp(width * .36, 180, 280));
}

// The stacked page (MainWindow.ShelfArrange.cs). The two cards are alike, the clipboard's against the left edge of the page and the
// file transfer's against the right. The one in front leaves Peek of the other showing; the one behind is narrowed to that strip
// and Under more, which lies beneath the card in front, and stands Inset lower at the top and higher at the bottom. Switching,
// both change width together in SwapSeconds on cubic-bezier(.22, 1, .36, 1), the one coming forward on top, so that one edge
// moves across the page; their fills change in ToneSeconds, and in each card the column and the strip's parts trade places in
// FadeSeconds. Nothing slides in from outside, nothing is scaled, and no shadow is drawn.
public static class ShelfDeck
{
    public const double Under = 12, Inset = 4, SwapSeconds = .28, FadeSeconds = .16, ToneSeconds = .18;
    // The cards' widths for the card in front, on a page `full` wide between its margins.
    public static (double Clips, double Files) Widths(ShelfSide front, double full, double peek)
    {
        double wide = Math.Max(0, full - peek), narrow = Math.Min(wide, peek + Under);
        return front == ShelfSide.Clipboard ? (wide, narrow) : (narrow, wide);
    }
    // What a side strip of a height has room for under its name (its length along the strip): the count goes first, then the icon.
    public static (bool Icon, bool Count) StripParts(double height, double padding, double gap, double icon, double name, double count)
    {
        double room = height - padding;
        if (icon + gap + name + gap + count <= room) return (true, true);
        return (icon + gap + name <= room, false);
    }
}

// How the page shows its two columns: side by side with a divider between them, or as two cards, one in front over most of the
// width and the other behind it, showing only its side strip (the clipboard at the left, the file transfer at the right).
public enum ShelfArrangement { Split, Stacked }

// Which of the page's columns are shown: both, or one of them over the whole width.
public enum ShelfShown { Both, Clipboard, Files }

public enum ShelfTier { Compact, Standard, Spacious }

// How the clipboard shows its entries: one line each; three lines high, the text on up to three lines at the left and three lines
// of facts at the right (when it was copied, what it holds, its categories); or as blocks in as many columns as the width holds.
public enum ClipView { Single, Three, Blocks }

// How the file transfer shows its files: one line each; two lines, a large icon with the name and, under it, the size and date;
// large icons over the names in as many columns as the width holds (Grid), in one row that scrolls sideways (Row), or in one
// column (Column).
public enum FileView { Single, Two, Grid, Row, Column }
