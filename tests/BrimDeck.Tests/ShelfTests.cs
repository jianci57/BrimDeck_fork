using BrimDeck.Core;

// The clipboard and file transfer page's rules (Shelf, ShelfLayout): what kind a copy is, how the history is kept and cleared,
// how files are added, how the page sizes itself, and that the settings and the saved data read back.
internal static class ShelfTests
{
    internal static void Run(Action<string, bool> check)
    {
        check("Shelf kinds: a single web address is a link, other text is not",
            Shelf.Classify("https://github.com/MoFeng2223/BrimDeck/pull/175") == ClipKind.Link && Shelf.Classify("see https://example.com") == ClipKind.Text
            && Shelf.Classify("ftp://example.com/file") != ClipKind.Link);
        check("Shelf kinds: colors in hex and rgb() are colors",
            Shelf.Classify("#0A84FF") == ClipKind.Color && Shelf.Classify("#fff") == ClipKind.Color && Shelf.Classify("rgb(10, 132, 255)") == ClipKind.Color
            && Shelf.Classify("#12345") != ClipKind.Color && Shelf.Classify("rgb(300, 0, 0)") != ClipKind.Color);
        check("Shelf kinds: code is told from prose",
            Shelf.Classify("public static void Main()\n{\n    Console.WriteLine(1);\n}") == ClipKind.Code
            && Shelf.Classify("var total = items.Sum(item => item.Price);") == ClipKind.Code
            && Shelf.Classify("明天下午三点开会，请准时参加。\n地点在三楼会议室。") == ClipKind.Text);
        Shelf.TryColor("#0A84FF", out var blue);
        var forms = Shelf.ColorForms(blue);
        check("Shelf colors: HEX, RGB and HSL forms", forms.Hex == "#0A84FF" && forms.Rgb == "rgb(10, 132, 255)" && forms.Hsl == "hsl(210, 100%, 52%)");
        check("Shelf links: the host and the rest are split, www dropped",
            Shelf.SplitLink("https://www.example.com/a/b?c=1") == ("example.com", "/a/b?c=1") && Shelf.SplitLink("https://example.com/") == ("example.com", ""));
        check("Shelf rows: the first line with text and how many follow", Shelf.FirstLine("\n  first\nsecond\n\nthird\n") == ("first", 3) && Shelf.FirstLine("one") == ("one", 0));

        // The same text copied again moves up and keeps its category; pictures are always new entries.
        var data = new ShelfData();
        var category = new ShelfCategory { Name = "工作" };
        data.ClipCategories.Add(category);
        var now = DateTimeOffset.Now;
        var first = Shelf.Add(data, new ClipEntry { Text = "hello", Copied = now.AddMinutes(-5) });
        first.Category = category.Id;
        Shelf.Add(data, new ClipEntry { Text = "other", Copied = now.AddMinutes(-4) });
        var again = Shelf.Add(data, new ClipEntry { Text = "hello", Copied = now });
        check("Shelf history: copying the same text again moves its entry to the top and keeps its category",
            ReferenceEquals(first, again) && data.Clips.Count == 2 && data.Clips[0] == first && first.Category == category.Id && first.Copied == now);

        var other = data.Clips[1];
        Shelf.Recopied(data, other, now.AddMinutes(1));
        check("Shelf history: an entry copied again from the page goes to the top with the new time and keeps its category",
            data.Clips[0] == other && other.Copied == now.AddMinutes(1) && data.Clips.Count == 2 && first.Category == category.Id);
        Shelf.Recopied(data, first, now);

        // Clearing: history older than the rule, or past the latest 500; entries in a category and phrases stay.
        var history = new ShelfData();
        for (int i = 0; i < 520; i++) history.Clips.Add(new ClipEntry { Text = "t" + i, Copied = now.AddDays(-i * .1) });
        history.Clips.Add(new ClipEntry { Text = "kept", Copied = now.AddDays(-90), Manual = true });
        history.Clips.Add(new ClipEntry { Text = "filed", Copied = now.AddDays(-90), Category = category.Id });
        var week = Shelf.Expired(history, ClipRetention.Week, now);
        var recent = Shelf.Expired(history, ClipRetention.Recent500, now);
        check("Shelf history: a week clears what is older than seven days, never what is kept",
            week.Count > 0 && week.All(clip => now - clip.Copied > TimeSpan.FromDays(7)) && week.All(clip => !clip.Kept));
        check("Shelf history: the latest 500 keeps 500 history entries", recent.Count == 20 && !recent.Any(clip => clip.Kept));
        check("Shelf history: until exit clears nothing while running, and clearing the history keeps phrases and categories",
            Shelf.Expired(history, ClipRetention.UntilExit, now).Count == 0 && Shelf.History(history).Count == 520);

        // Files: newest first, a file dropped again moves up, the category is kept or given.
        var shelf = new ShelfData();
        Shelf.AddFiles(shelf, [@"C:\a.txt", @"C:\b.txt"], null, now);
        Shelf.AddFiles(shelf, [@"c:\A.TXT"], category.Id, now.AddMinutes(1));
        check("Shelf files: dropped files go to the top in their order, and a file dropped again moves up into the category",
            shelf.Files.Count == 2 && shelf.Files[0].Path == @"C:\a.txt" && shelf.Files[0].Category == category.Id && shelf.Files[1].Path == @"C:\b.txt"
            && shelf.Files[0].Name == "a.txt");
        check("Shelf files: 清除 takes every file in 全部 and only a category's files in it",
            Shelf.ClearableFiles(shelf, null).Count == 2 && Shelf.ClearableFiles(shelf, category.Id).Single().Path == @"C:\a.txt");
        shelf.FileCategories.Add(category);
        Shelf.DeleteCategory(shelf, ShelfSide.Files, category.Id);
        check("Shelf categories: deleting a category puts its files back in all", shelf.FileCategories.Count == 0 && shelf.Files.All(file => file.Category is null));

        // Saved data reads back; an entry whose category is gone returns to all.
        data.Clips.Add(new ClipEntry { Kind = ClipKind.Image, PixelWidth = 10, PixelHeight = 5, Copied = now });
        data.Clips.Add(new ClipEntry { Text = "lost", Category = Guid.NewGuid() });
        var read = ShelfData.Deserialize(data.Serialize());
        check("Shelf data: saved and read back, with a category that is gone dropped",
            read.Clips.Count == data.Clips.Count && read.Clips[0].Category == category.Id && read.Clips.Single(c => c.Text == "lost").Category is null
            && read.Clips.Any(c => c.Kind == ClipKind.Image && c.PixelWidth == 10) && read.OwnCategories(ShelfSide.Clipboard).Single().Name == "工作");

        // "常用": first in every history, old ones included; phrases written before it existed move into it; it is never deleted.
        var old = ShelfData.Deserialize(System.Text.Encoding.UTF8.GetBytes(
            "{\"Clips\":[{\"Text\":\"hi\",\"Manual\":true},{\"Text\":\"copied\"}],\"ClipCategories\":[{\"Id\":\"" + category.Id + "\",\"Name\":\"工作\"}]}"));
        check("Shelf favorites: an older history gains 常用 first, with its phrases in it",
            old.ClipCategories.Count == 2 && old.ClipCategories[0].Id == ShelfData.Favorites && old.ClipCategories[1].Id == category.Id
            && old.Clips.Single(c => c.Text == "hi").Category == ShelfData.Favorites && old.Clips.Single(c => c.Text == "copied").Category is null);
        Shelf.DeleteCategory(old, ShelfSide.Clipboard, ShelfData.Favorites);
        var phrase = Shelf.AddPhrase(old, "new phrase", now);
        check("Shelf favorites: 常用 cannot be deleted, and a new phrase goes to the top of it",
            old.ClipCategories[0].Id == ShelfData.Favorites && old.Clips[0] == phrase && phrase.Manual && phrase.Category == ShelfData.Favorites && phrase.Kept);
        var second = Shelf.AddCategory(old, ShelfSide.Clipboard, "代码", ["#111111", "#222222"]);
        Shelf.Reorder(old, ShelfSide.Clipboard, [second.Id, category.Id]);
        check("Shelf categories: new ones take the next color and go last; reordering keeps 常用 first",
            second.Color == "#222222" && old.ClipCategories.Select(c => c.Id).SequenceEqual([ShelfData.Favorites, second.Id, category.Id]));
        old.Clips.Add(new ClipEntry { Text = "filed", Category = second.Id });
        check("Shelf clearing: 全部 clears all but 常用, a category of the user's only its own entries, 常用 nothing",
            Shelf.Clearable(old, null).Select(c => c.Text).Order().SequenceEqual(["copied", "filed"])
            && Shelf.Clearable(old, second.Id).Single().Text == "filed" && Shelf.Clearable(old, ShelfData.Favorites).Count == 0);

        check("Shelf sizes: file sizes as the Explorer writes them",
            Shelf.Size(512) == "512 B" && Shelf.Size(1536) == "1.5 KB" && Shelf.Size(25 * 1024 * 1024) == "25 MB" && Shelf.Size(3L * 1024 * 1024 * 1024) == "3 GB");
        check("Shelf search: matches ignore case and are found in the line",
            Shelf.Matches("Hello World", "world") && !Shelf.Matches("Hello", " ") && Shelf.MatchesIn("abcABC", "bc").SequenceEqual([(1, 2), (4, 2)]));

        // The page's sizes (the design's table): three steps by height; the file column 40 % of the room within 170 and 340.
        var compact = ShelfLayout.For(440, 140);
        var standard = ShelfLayout.For(620, 220);
        var spacious = ShelfLayout.For(1200, 400);
        check("Shelf layout: steps by height", compact.Tier == ShelfTier.Compact && compact.Row == 22 && standard.Tier == ShelfTier.Standard && standard.Row == 26
            && spacious.Tier == ShelfTier.Spacious && spacious.Row == 30 && spacious.TwoLines && ShelfLayout.For(620, 179).Tier == ShelfTier.Compact && ShelfLayout.For(620, 280).Tier == ShelfTier.Spacious);
        check("Shelf layout: the file column is 40 % within 170 and 340, and shows sizes from 210 and dates from 300 when spacious",
            compact.FileColumn == 170 && !compact.FileSize && standard.FileColumn == Math.Round((620 - 40 - 32) * .4) && standard.FileSize
            && spacious.FileColumn == 340 && spacious.FileDate && !ShelfLayout.For(860, 260).FileDate);
        check("Shelf layout: the search box is 36 % of the width within 180 and 280, and the details have a side column from 560",
            ShelfLayout.SearchWidth(440) == 180 && ShelfLayout.SearchWidth(620) == 223 && ShelfLayout.SearchWidth(1200) == 280 && ShelfLayout.DetailSide(560) && !ShelfLayout.DetailSide(559));

        // Settings: the page is on and records from the start; the page has its own size; the count part shows only with the page.
        var settings = new DeckSettings();
        check("Shelf settings: the page is shown, recording is on, the history is kept 30 days",
            settings.ClipboardPage && settings.ClipboardRecord && settings.ClipboardRetention == ClipRetention.Month && DeckPages.Order.Contains(DeckPage.Clipboard));
        check("Shelf settings: a settings file that turned recording off keeps it off",
            !System.Text.Json.JsonSerializer.Deserialize<DeckSettings>("{\"ClipboardRecord\":false}")!.ClipboardRecord);
        DeckPages.SetSize(settings, DeckPage.Clipboard, 2000, 100);
        check("Shelf settings: the page's size is its own and kept within the limits", DeckPages.Size(settings, DeckPage.Clipboard) == (1200, 140) && settings.MonitorWidth == MonitorSizes.Default.Width);
        settings.ClipboardPage = false;
        check("Shelf settings: the file transfer's count needs the page", !settings.ShowsTransfer(CompactStyle.Notch) && new DeckSettings().ShowsTransfer(CompactStyle.Capsule)
            && !new DeckSettings().ShowsTransfer(CompactStyle.Line));
        var normalized = new DeckSettings { ClipboardExcluded = ["KeePass.exe", " keepass ", "", "1Password"], ClipboardWidth = double.NaN };
        normalized.Normalize();
        check("Shelf settings: excluded applications lose .exe and repeats, and a bad size returns to the default",
            normalized.ClipboardExcluded.SequenceEqual(["KeePass", "1Password"]) && normalized.ClipboardWidth == ShelfSizes.Default.Width);
        check("Shelf settings: the page order of an older file gains the page at the end",
            DeckPages.Normalize([DeckPage.Monitor, DeckPage.Usage, DeckPage.Music]).SequenceEqual([DeckPage.Monitor, DeckPage.Usage, DeckPage.Music, DeckPage.Clipboard, DeckPage.Devices]));
    }
}
