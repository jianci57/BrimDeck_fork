using System.Text;

namespace BrimDeck.Core;

// One toast notification as the notifications page shows it. Filled from WinRT by
// BrimDeck.Native.NotificationReader; kept in Core so ordering and text cleanup stay testable off Windows.
public sealed record NotificationItem(
    uint Id, string AppName, string AppId, string Title, string Body, DateTimeOffset CreationTime, bool HasText)
{
    public const int Maximum = 100;
    // The body is shown as it was sent (its line breaks kept, so a message reads as a message); only its
    // length is capped, in case an app sends a notice the size of a document.
    public const int BodyLimit = 2000;
    public static string Limit(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= BodyLimit) return text ?? "";
        int end = BodyLimit;
        // Never cut between the two halves of a surrogate pair: an emoji cut in half would end in an orphan.
        if (char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end].TrimEnd() + "…";
    }
    // The page mirrors the notification center as it is right now: newest first, at most Maximum items,
    // no history of its own — a notification that disappears from the center disappears from the page too.
    public static IReadOnlyList<NotificationItem> NewestFirst(IEnumerable<NotificationItem> notifications) =>
        notifications.OrderByDescending(n => n.CreationTime).ThenByDescending(n => n.Id).Take(Maximum).ToList();
    // Toast text carries line breaks, tabs and runs of spaces; the card shows it as one plain run of text.
    public static string Flatten(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c)) { space = builder.Length > 0; continue; }
            if (space) { builder.Append(' '); space = false; }
            builder.Append(c);
        }
        return builder.ToString();
    }
}
