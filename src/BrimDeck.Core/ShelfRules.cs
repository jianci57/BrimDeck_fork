using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

// A category's rule (ShelfCategory.Rule): what a new copy, or a file added to the file transfer, must be like to go into the
// category by itself. It only adds: anything can still be put in a category by hand, and taking an entry out by hand holds
// until it is copied again. A condition not filled in yet is passed over; a rule with none filled in, or with a regular
// expression that does not read, adds nothing.
public enum RuleMatch { Any, All }
// Kind, Text, Site and Source are the clipboard's; FileType, Extension, FileName and Folder the file transfer's.
public enum RuleField { Kind, Text, Site, Source, FileType, Extension, FileName, Folder }
// How Text and FileName are compared. Format (Text only) takes a TextFormat's name as its value.
public enum RuleTest { Contains, StartsWith, EndsWith, Format, Regex }
public enum TextFormat { Email, Phone, Path, IpAddress, Number, Commit }
public enum FileKind { Folder, Image, Document, Spreadsheet, Presentation, Pdf, Archive, Installer, Video, Audio, Code }

public sealed class ShelfRule
{
    public RuleMatch Match { get; set; } = RuleMatch.Any;
    public List<RuleCondition> Conditions { get; set; } = [];
}

public sealed class RuleCondition
{
    public RuleField Field { get; set; }
    public RuleTest Test { get; set; }
    // Kind: ClipKind names; FileType: FileKind names.
    public List<string> Values { get; set; } = [];
    // Text and FileName: the text, the TextFormat's name or the pattern. Site, Source and Extension: items separated by commas.
    // Folder: the folder's path.
    public string Value { get; set; } = "";
}

public static class ShelfRules
{
    public static readonly RuleField[] ClipFields = [RuleField.Kind, RuleField.Text, RuleField.Site, RuleField.Source];
    public static readonly RuleField[] FileFields = [RuleField.FileType, RuleField.Extension, RuleField.FileName, RuleField.Folder];
    public static RuleField[] Fields(ShelfSide side) => side == ShelfSide.Clipboard ? ClipFields : FileFields;

    // A condition new in the editor: "内容类型" or "文件类型", nothing picked yet.
    public static RuleCondition NewCondition(ShelfSide side) => new() { Field = side == ShelfSide.Clipboard ? RuleField.Kind : RuleField.FileType };

    public static bool Filled(RuleCondition condition) => condition.Field is RuleField.Kind or RuleField.FileType
        ? condition.Values.Count > 0 : condition.Value.Trim().Length > 0;
    static IEnumerable<RuleCondition> Used(ShelfRule rule, ShelfSide side) => rule.Conditions.Where(c => Fields(side).Contains(c.Field) && Filled(c));

    // Whether the rule adds anything: it has a condition filled in and nothing wrong.
    public static bool Active(ShelfRule? rule, ShelfSide side) => rule is not null && Used(rule, side).Any() && Problem(rule, side) is null;

    // What is wrong with a condition: only a regular expression can be; null when nothing is.
    public static string? Problem(RuleCondition condition)
    {
        if (condition.Field is not (RuleField.Text or RuleField.FileName) || condition.Test != RuleTest.Regex || condition.Value.Length == 0) return null;
        try { _ = Pattern(condition.Value); return null; }
        catch (RegexParseException error)
        {
            return error.Error switch
            {
                RegexParseError.InsufficientClosingParentheses => Loc.T("缺少右括号。", "A closing parenthesis is missing."),
                RegexParseError.InsufficientOpeningParentheses => Loc.T("右括号多于左括号。", "There are more closing parentheses than opening ones."),
                RegexParseError.UnterminatedBracket => Loc.T("缺少右方括号。", "A closing bracket is missing."),
                RegexParseError.QuantifierAfterNothing => Loc.T("数量符号前面没有内容。", "A quantifier has nothing before it."),
                RegexParseError.UnescapedEndingBackslash => Loc.T("末尾的反斜杠需要再写一个。", "The backslash at the end needs another one."),
                _ => Loc.T($"第 {error.Offset} 个字符处有误。", $"There is a mistake at character {error.Offset}.")
            };
        }
    }
    public static string? Problem(ShelfRule? rule, ShelfSide side) => rule is null ? null : Used(rule, side).Select(Problem).FirstOrDefault(problem => problem is not null);

    public static bool Matches(ShelfRule rule, ClipEntry clip) => Combine(rule, ShelfSide.Clipboard, condition => Test(condition, clip));
    public static bool Matches(ShelfRule rule, string path) => Combine(rule, ShelfSide.Files, condition => Test(condition, path));
    static bool Combine(ShelfRule rule, ShelfSide side, Func<RuleCondition, bool> test)
    {
        if (!Active(rule, side)) return false;
        var used = Used(rule, side);
        return rule.Match == RuleMatch.All ? used.All(test) : used.Any(test);
    }

    // A new copy or a copy made again goes into every category whose rule it meets, staying in those it is in. Phrases are left alone.
    public static void Apply(ShelfData data, ClipEntry clip)
    {
        if (clip.Manual) return;
        foreach (var category in data.OwnCategories(ShelfSide.Clipboard))
            if (category.Rule is { } rule && !clip.Categories.Contains(category.Id) && Matches(rule, clip)) clip.Categories.Add(category.Id);
    }
    public static void Apply(ShelfData data, TransferFile file)
    {
        foreach (var category in data.FileCategories)
            if (category.Rule is { } rule && !file.Categories.Contains(category.Id) && Matches(rule, file.Path)) file.Categories.Add(category.Id);
    }
    // What is already on the page and meets the rule but is not in the category: the editor counts these and can put them in.
    public static List<ClipEntry> Outside(ShelfData data, ShelfCategory category) => category.Rule is { } rule
        ? data.Clips.Where(clip => !clip.Manual && !clip.Categories.Contains(category.Id) && Matches(rule, clip)).ToList() : [];
    public static List<TransferFile> OutsideFiles(ShelfData data, ShelfCategory category) => category.Rule is { } rule
        ? data.Files.Where(file => !file.Categories.Contains(category.Id) && Matches(rule, file.Path)).ToList() : [];

    // ---- One condition ----
    static bool Test(RuleCondition condition, ClipEntry clip) => condition.Field switch
    {
        RuleField.Kind => condition.Values.Contains(clip.Kind.ToString()),
        RuleField.Text => clip.Kind != ClipKind.Image && TestText(condition, clip.Text),
        RuleField.Site => clip.Kind == ClipKind.Link && Uri.TryCreate(clip.Text.Trim(), UriKind.Absolute, out var uri) && Items(condition.Value).Any(site => OnSite(uri.Host, site)),
        RuleField.Source => ClipboardExclusions.Excludes(Items(condition.Value), clip.Source),
        _ => false
    };
    static bool Test(RuleCondition condition, string path)
    {
        string name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        string extension = System.IO.Path.GetExtension(name).TrimStart('.');
        return condition.Field switch
        {
            RuleField.FileType => condition.Values.Any(value => Enum.TryParse<FileKind>(value, out var kind) && IsKind(path, extension, kind)),
            RuleField.Extension => extension.Length > 0 && Items(condition.Value).Any(item => string.Equals(item.TrimStart('.'), extension, StringComparison.OrdinalIgnoreCase)),
            RuleField.FileName => TestText(condition, name),
            RuleField.Folder => InFolder(path, condition.Value.Trim()),
            _ => false
        };
    }
    static bool TestText(RuleCondition condition, string text)
    {
        string value = condition.Value;
        return condition.Test switch
        {
            RuleTest.Contains => text.Contains(value, StringComparison.CurrentCultureIgnoreCase),
            RuleTest.StartsWith => text.TrimStart().StartsWith(value, StringComparison.CurrentCultureIgnoreCase),
            RuleTest.EndsWith => text.TrimEnd().EndsWith(value, StringComparison.CurrentCultureIgnoreCase),
            RuleTest.Format => Enum.TryParse<TextFormat>(value, out var format) && IsFormat(text.Trim(), format),
            RuleTest.Regex => Problem(condition) is null && Search(value, text),
            _ => false
        };
    }

    // Items typed with commas (either width), semicolons or spaces between them.
    public static List<string> Items(string value) => [.. value.Split([',', '，', ';', '；', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(item => item.Trim())];

    // "github.com" also covers "gist.github.com"; a whole address typed in counts by its host.
    static bool OnSite(string host, string site)
    {
        site = site.Trim().ToLowerInvariant();
        if (Uri.TryCreate(site.Contains("://") ? site : "https://" + site, UriKind.Absolute, out var typed)) site = typed.Host;
        if (site.StartsWith("www.", StringComparison.Ordinal)) site = site[4..];
        host = host.ToLowerInvariant();
        return site.Length > 0 && (host == site || host.EndsWith("." + site, StringComparison.Ordinal));
    }

    static bool InFolder(string path, string folder)
    {
        folder = folder.TrimEnd('\\', '/');
        return folder.Length > 0 && path.Length > folder.Length + 1 && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && path[folder.Length] is '\\' or '/';
    }

    // ---- Regular expressions: read once, each search stopped after 50 ms (a pattern that takes longer does not match) ----
    static readonly Dictionary<string, Regex> Patterns = [];
    static Regex Pattern(string pattern)
    {
        lock (Patterns)
        {
            if (Patterns.TryGetValue(pattern, out var known)) return known;
            var regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            if (Patterns.Count > 200) Patterns.Clear();
            return Patterns[pattern] = regex;
        }
    }
    static bool Search(string pattern, string text)
    {
        try { return Pattern(pattern).IsMatch(text); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    // ---- Built-in formats: the whole text, trimmed, is one ----
    static readonly Regex Email = new(@"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.CultureInvariant);
    static readonly Regex Phone = new(@"^(\+?86)?1[3-9]\d{9}$", RegexOptions.CultureInvariant);
    static readonly Regex WindowsPath = new(@"^([A-Za-z]:\\|\\\\[^\\\s]+\\)[^\r\n]*$", RegexOptions.CultureInvariant);
    static readonly Regex Ipv4 = new(@"^(\d{1,3}(\.\d{1,3}){3})(:\d{1,5})?$", RegexOptions.CultureInvariant);
    static readonly Regex Number = new(@"^[+-]?(\d{1,3}(,\d{3})+|\d+)(\.\d+)?$", RegexOptions.CultureInvariant);
    static readonly Regex Commit = new(@"^[0-9a-fA-F]{7,40}$", RegexOptions.CultureInvariant);
    public static bool IsFormat(string text, TextFormat format) => format switch
    {
        TextFormat.Email => Email.IsMatch(text),
        TextFormat.Phone => Phone.IsMatch(text.Replace(" ", "").Replace("-", "")),
        TextFormat.Path => WindowsPath.IsMatch(text),
        TextFormat.IpAddress => Ipv4.Match(text) is { Success: true } v4 ? IPAddress.TryParse(v4.Groups[1].Value, out _)
            : text.Contains(':') && IPEndPoint.TryParse(text, out var end) && end.Address.AddressFamily == AddressFamily.InterNetworkV6,
        TextFormat.Number => Number.IsMatch(text),
        TextFormat.Commit => Commit.IsMatch(text),
        _ => false
    };

    // ---- File kinds, by extension ----
    static readonly Dictionary<FileKind, string[]> Extensions = new()
    {
        [FileKind.Image] = ["jpg", "jpeg", "png", "gif", "bmp", "webp", "tif", "tiff", "heic", "heif", "avif", "svg", "ico", "psd"],
        [FileKind.Document] = ["doc", "docx", "odt", "rtf", "txt", "md", "pages", "wps"],
        [FileKind.Spreadsheet] = ["xls", "xlsx", "xlsm", "csv", "ods", "numbers", "et"],
        [FileKind.Presentation] = ["ppt", "pptx", "odp", "key", "dps"],
        [FileKind.Pdf] = ["pdf"],
        [FileKind.Archive] = ["zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zst", "iso", "cab"],
        [FileKind.Installer] = ["exe", "msi", "msix", "msixbundle", "appx", "appxbundle"],
        [FileKind.Video] = ["mp4", "mkv", "mov", "avi", "wmv", "flv", "webm", "m4v"],
        [FileKind.Audio] = ["mp3", "flac", "wav", "aac", "m4a", "ogg", "opus", "wma", "ape"],
        [FileKind.Code] = ["cs", "js", "ts", "tsx", "jsx", "py", "java", "c", "cpp", "h", "hpp", "go", "rs", "rb", "php", "swift", "kt",
            "json", "xml", "yaml", "yml", "toml", "html", "css", "sql", "sh", "ps1", "bat"],
    };
    public static IReadOnlyList<string> ExtensionsOf(FileKind kind) => Extensions.TryGetValue(kind, out var list) ? list : [];
    static bool IsKind(string path, string extension, FileKind kind) => kind == FileKind.Folder
        ? Directory.Exists(path) : extension.Length > 0 && ExtensionsOf(kind).Contains(extension.ToLowerInvariant());

    // ---- Names, for the editor and the summary ----
    public static string Name(RuleField field) => field switch
    {
        RuleField.Kind => Loc.T("内容类型", "Kind"), RuleField.Text => Loc.T("文字", "Text"), RuleField.Site => Loc.T("链接网站", "Link site"),
        RuleField.Source => Loc.T("来源应用", "Source app"), RuleField.FileType => Loc.T("文件类型", "File type"), RuleField.Extension => Loc.T("扩展名", "Extension"),
        RuleField.FileName => Loc.T("文件名", "File name"), _ => Loc.T("所在文件夹", "Folder")
    };
    public static string Name(RuleTest test) => test switch
    {
        RuleTest.Contains => Loc.T("包含", "contains"), RuleTest.StartsWith => Loc.T("开头是", "starts with"), RuleTest.EndsWith => Loc.T("结尾是", "ends with"),
        RuleTest.Format => Loc.T("是", "is"), _ => Loc.T("符合正则", "matches regex")
    };
    public static string Name(ClipKind kind) => kind switch
    {
        ClipKind.Link => Loc.T("链接", "Link"), ClipKind.Code => Loc.T("代码", "Code"), ClipKind.Color => Loc.T("颜色", "Color"), ClipKind.Image => Loc.T("图片", "Picture"), _ => Loc.T("文字", "Text")
    };
    public static string Name(TextFormat format) => format switch
    {
        TextFormat.Email => Loc.T("邮箱地址", "Email address"), TextFormat.Phone => Loc.T("手机号码", "Mobile number"), TextFormat.Path => Loc.T("文件路径", "File path"),
        TextFormat.IpAddress => Loc.T("IP 地址", "IP address"), TextFormat.Number => Loc.T("纯数字", "Number"), _ => Loc.T("Git 提交号", "Git commit")
    };
    public static string Example(TextFormat format) => format switch
    {
        TextFormat.Email => "name@example.com", TextFormat.Phone => "138 0013 8000", TextFormat.Path => @"D:\Projects\BrimDeck",
        TextFormat.IpAddress => "192.168.1.10:8080", TextFormat.Number => "1,280.50", _ => "5e43f0f"
    };
    public static string Name(FileKind kind) => kind switch
    {
        FileKind.Folder => Loc.T("文件夹", "Folder"), FileKind.Image => Loc.T("图片", "Image"), FileKind.Document => Loc.T("文档", "Document"),
        FileKind.Spreadsheet => Loc.T("表格", "Spreadsheet"), FileKind.Presentation => Loc.T("演示文稿", "Presentation"), FileKind.Pdf => "PDF",
        FileKind.Archive => Loc.T("压缩包", "Archive"), FileKind.Installer => Loc.T("安装包", "Installer"), FileKind.Video => Loc.T("视频", "Video"),
        FileKind.Audio => Loc.T("音频", "Audio"), _ => Loc.T("代码", "Code")
    };

    // One line for the category's row: each condition said on its own ("图片或链接", "来自 chrome"), joined by "或" or "且".
    public static string Summary(ShelfRule rule, ShelfSide side)
    {
        var parts = Used(rule, side).Select(Describe).ToList();
        return string.Join(rule.Match == RuleMatch.All ? Loc.T("，且", "; and ") : Loc.T("，或", "; or "), parts);
    }
    static string Describe(RuleCondition c)
    {
        string or = Loc.T("或", " or ");
        string Quote(string text) => Loc.T($"“{text}”", $"\"{text}\"");
        string list = string.Join(Loc.T("、", ", "), Items(c.Value));
        string Apps() => string.Join(Loc.T("、", ", "), Items(c.Value).Select(ClipboardExclusions.ProcessName));
        return c.Field switch
        {
            RuleField.Kind => string.Join(or, c.Values.Select(v => Enum.TryParse<ClipKind>(v, out var k) ? Name(k) : v)),
            RuleField.FileType => string.Join(or, c.Values.Select(v => Enum.TryParse<FileKind>(v, out var k) ? Name(k) : v)),
            RuleField.Site => Loc.T($"{list} 的链接", $"links to {list}"),
            RuleField.Source => Loc.T($"来自 {Apps()}", $"from {Apps()}"),
            RuleField.Extension => list,
            RuleField.Folder => Loc.T($"在 {c.Value.Trim()} 中", $"in {c.Value.Trim()}"),
            _ when c.Test == RuleTest.Format => Enum.TryParse<TextFormat>(c.Value, out var f) ? Name(f) : c.Value,
            _ when c.Test == RuleTest.Regex => Loc.T($"{Name(c.Field)}符合 {c.Value}", $"{Name(c.Field)} matches {c.Value}"),
            _ => Loc.T($"{Name(c.Field)}{Name(c.Test)}{Quote(c.Value)}", $"{Name(c.Field)} {Name(c.Test)} {Quote(c.Value)}")
        };
    }
}
