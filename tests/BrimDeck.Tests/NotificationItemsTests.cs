using BrimDeck.Core;

internal static class NotificationItemsTests
{
    internal static void Run(Action<string, bool> check)
    {
        // Newest first: a later creation time comes first, and equal times fall back to the higher id.
        var first = new NotificationItem(10, "A", "a", "t", "", new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero), true);
        var second = new NotificationItem(11, "B", "b", "t", "", new DateTimeOffset(2026, 11, 12, 9, 1, 0, TimeSpan.Zero), true);
        var third = new NotificationItem(12, "C", "c", "t", "", new DateTimeOffset(2026, 11, 12, 9, 1, 0, TimeSpan.Zero), true);
        var ordered = NotificationItem.NewestFirst([second, first, third]);
        check("Newest first puts later notifications on top", ordered[0] == third && ordered[1] == second && ordered[2] == first);
        // Ties in creation time read by id: the system hands out rising ids.
        check("Equal times order by id, descending",
            NotificationItem.NewestFirst([new NotificationItem(5, "A", "", "t", "", first.CreationTime, true),
                new NotificationItem(9, "B", "", "t", "", first.CreationTime, true)])[0].Id == 9);
        // The page mirrors the center, so it keeps at most the newest Maximum notifications.
        var many = Enumerable.Range(0, NotificationItem.Maximum + 40)
            .Select(i => new NotificationItem((uint)i, "A", "", "t", "", first.CreationTime.AddMinutes(i), true)).ToList();
        var capped = NotificationItem.NewestFirst(many);
        check($"The list is capped at {NotificationItem.Maximum}", capped.Count == NotificationItem.Maximum);
        check("The cap keeps the newest ones", capped[0].CreationTime == first.CreationTime.AddMinutes(NotificationItem.Maximum + 39));

        // Toast text: control characters and runs of spaces collapse into single spaces.
        check("Flatten collapses blanks and control characters",
            NotificationItem.Flatten("  行一\n\r行二\t\t行三  ") == "行一 行二 行三");
        check("Flatten keeps inner words intact", NotificationItem.Flatten("a  b") == "a b" && NotificationItem.Flatten("abc") == "abc");
        check("Flatten of nothing is empty", NotificationItem.Flatten(null) == "" && NotificationItem.Flatten("") == "" && NotificationItem.Flatten(" \n ") == "");
        // The body is capped, never cutting a surrogate pair in half.
        check("The body limit keeps a whole text under it", NotificationItem.Limit(new string('字', NotificationItem.BodyLimit)).Length == NotificationItem.BodyLimit);
        var withEmoji = "a" + new string('字', NotificationItem.BodyLimit - 2) + "😀" + new string('字', 100);
        var limited = NotificationItem.Limit(withEmoji);
        check("The body limit never orphans a surrogate pair",
            limited.Length == NotificationItem.BodyLimit && !char.IsHighSurrogate(limited[^1]) && limited.EndsWith("…"));
        check("An empty body stays empty", NotificationItem.Limit(null) == "" && NotificationItem.Limit("") == "");
    }
}
