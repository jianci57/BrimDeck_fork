namespace BrimDeck.Core;

// The numbers and decisions behind the small answers to the pointer (motion-review F4, F10, F11, F12): how a press is timed,
// when something counts as hovered, when a changed glyph grows into place. The windows (BrimDeck.Soft, Feedback, the music
// page) take them from here so they can be checked without WPF.
public static class PressFeedback
{
    // What shrinks under the pointer goes down in Down milliseconds, a pressed fill or glyph shows in Fill, so the press is
    // felt at once; both let go in Up, as the monitor's hover does. Press shrinks to Scale, never below 0.9.
    public const int Down = 100, Fill = 70, Up = 160;
    public const double Scale = .96;

    // Touch and a pen stand in for the pointer only while they press: over a control by touch or a pen, nothing is hovered.
    public static bool Hovers(bool mouseOver, bool byTouchOrPen) => mouseOver && !byTouchOrPen;

    // How much of a hover/press fill shows: a press shows it whole whatever the hover is, since touch and a pen press without
    // hovering first.
    public static double FillShown(double hover, bool pressed) => pressed ? 1 : hover;

    // The length of a press changing to pressed (or back) for a scale.
    public static int ScaleLength(bool pressed) => pressed ? Down : Up;
}

// Whether animations may run at all: BrimDeck's own setting, Windows' "animation effects", and never in high contrast. The one
// rule for every animation in the app (motion-review F5, F6).
public static class MotionConditions
{
    public static bool Allowed(bool appSetting, bool systemAnimations, bool highContrast) => appSetting && systemAnimations && !highContrast;

    // Whether text too long for its place (a song's title, the current lyric) scrolls to show the rest. Scrolling is how that text
    // is read, not a flourish, so high contrast, which is about colors, does not stop it; the app's setting and Windows' "animation
    // effects" still do.
    public static bool Scrolls(bool appSetting, bool systemAnimations) => appSetting && systemAnimations;
}

// A transport button's glyph (play and pause, the playback mode, the heart) grows into place from nearly its size, and only
// after the pointer clicked that button: a change made by a media key, the keyboard or the player switches at once.
public static class GlyphGrowth
{
    public const double From = .9;
    public const int Milliseconds = 200;
    // How long after a click on the button a change of its glyph still answers that click (Environment.TickCount64 units).
    public const long AnswerWindow = 2000;

    public static bool Grows(long? clickedAt, long now) => clickedAt is { } at && now - at >= 0 && now - at < AnswerWindow;
}

// A fill that changes width (the page switch's capsule, the usage page's selected segment) is laid out once at its new width
// and drawn stretching from its left edge, so the glide lays nothing out again.
public static class StretchGlide
{
    // The horizontal scale the fill starts from (it ends at 1), or null when there is nothing to animate: a width that is not
    // known, or a change of half a DIP or less.
    public static double? From(double was, double width) =>
        double.IsFinite(was) && double.IsFinite(width) && was > 0 && width > 0 && Math.Abs(was - width) > .5 ? was / width : null;
}
