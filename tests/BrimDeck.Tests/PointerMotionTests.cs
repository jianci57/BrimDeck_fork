using BrimDeck.Core;

// The small answers to the pointer (motion-review F4, F5, F6, F8, F10, F11, F12): how a press is timed, when a control counts as
// hovered, whether animations may run at all, when a changed glyph grows into place, and the stretch a width change is drawn
// with. The windows take these numbers and decisions from BrimDeck.Core (Soft, Feedback, the music page).
internal static class PointerMotionTests
{
    public static void Run(Action<string, bool> check)
    {
        // F12: a press shows at once and lets go as hover does.
        check("A press fill shows faster than a hover arrives and faster than the press lets go",
            PressFeedback.Fill < (int)(Eased.Hover * 1000) && PressFeedback.Fill < PressFeedback.Up && PressFeedback.Fill == 70);
        check("A pressed scale goes down faster than it comes back", PressFeedback.ScaleLength(true) == 100 && PressFeedback.ScaleLength(false) == 160
            && PressFeedback.ScaleLength(true) < PressFeedback.ScaleLength(false));
        // F11: one press for the transport buttons and the cover, never smaller than 0.9.
        check("A press shrinks to 0.96, no further than 0.9", PressFeedback.Scale == .96 && PressFeedback.Scale >= .9 && PressFeedback.Scale < 1);

        // F4: touch and a pen leave no hover behind.
        check("A mouse over a control hovers it", PressFeedback.Hovers(true, false));
        check("Touch or a pen over a control does not hover it", !PressFeedback.Hovers(true, true));
        check("Nothing over a control, nothing hovered", !PressFeedback.Hovers(false, false) && !PressFeedback.Hovers(false, true));
        // A press by touch or a pen starts with no hover, and still shows its fill.
        check("A press shows the whole fill even with no hover", PressFeedback.FillShown(0, true) == 1 && PressFeedback.FillShown(.3, true) == 1);
        check("Without a press the fill follows the hover", PressFeedback.FillShown(0, false) == 0 && PressFeedback.FillShown(.4, false) == .4);

        // F5, F6: one rule for every animation.
        check("Animations run when the app and Windows allow them", MotionConditions.Allowed(true, true, false));
        check("Animations off in the app stop them", !MotionConditions.Allowed(false, true, false));
        check("Windows' animation effects off stop them", !MotionConditions.Allowed(true, false, false));
        check("High contrast stops them", !MotionConditions.Allowed(true, true, true));
        // Text too long for its place still scrolls in high contrast; the app's setting and Windows' animation effects stop it.
        check("Long text scrolls when the app and Windows allow animations", MotionConditions.Scrolls(true, true));
        check("Long text does not scroll with animations off in the app or in Windows", !MotionConditions.Scrolls(false, true) && !MotionConditions.Scrolls(true, false));

        // F10: a glyph grows into place only for a click on its own button, not long after.
        long now = 1_000_000;
        check("A glyph changed right after the pointer clicked its button grows", GlyphGrowth.Grows(now - 120, now));
        check("A glyph changed with no click on its button switches at once", !GlyphGrowth.Grows(null, now));
        check("A glyph changed long after the click (the player's own change) switches at once", !GlyphGrowth.Grows(now - GlyphGrowth.AnswerWindow, now)
            && !GlyphGrowth.Grows(now - 60_000, now));
        check("A click from a clock that went back grows nothing", !GlyphGrowth.Grows(now + 5, now));
        check("A glyph grows from no less than 0.9 in 200 ms", GlyphGrowth.From >= .9 && GlyphGrowth.From < 1 && GlyphGrowth.Milliseconds == 200);

        // F8: a width change is drawn as a stretch from the left edge.
        check("A fill getting wider starts from the share of the new width it had", StretchGlide.From(28, 56) == .5 && StretchGlide.From(56, 28) == 2);
        check("A change of half a DIP or less is not animated", StretchGlide.From(40, 40.4) is null && StretchGlide.From(40, 40) is null);
        check("A width that is not known is not animated", StretchGlide.From(0, 40) is null && StretchGlide.From(40, 0) is null
            && StretchGlide.From(double.NaN, 40) is null && StretchGlide.From(40, double.PositiveInfinity) is null);
    }
}
