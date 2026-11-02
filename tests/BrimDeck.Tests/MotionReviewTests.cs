using BrimDeck.Core;

// The motion review's panel fixes that can be checked without WPF: the panel's closing and keyboard lengths (C1, C4), a move
// turned back part way (F1, F2), and when the picture beneath a pop-up is taken (F7).
internal static class MotionReviewTests
{
    internal static void Run(Action<string, bool> check)
    {
        // C1, C4.
        check("Motion review: the panel opens in the set time and closes in three quarters of it",
            PanelMotion.Length(360, closing: false, keyboard: false) == 360 && PanelMotion.Length(360, closing: true, keyboard: false) == 270
            && PanelMotion.Length(1000, closing: true, keyboard: false) == 750 && PanelMotion.Length(100, closing: true, keyboard: false) == 75);
        check("Motion review: a move the keyboard asks for takes 0.6 of the set time, opening or closing",
            PanelMotion.Length(360, closing: false, keyboard: true) == 216 && PanelMotion.Length(360, closing: true, keyboard: true) == 216
            && PanelMotion.Length(360, true, true) < PanelMotion.Length(360, true, false));
        check("Motion review: without animations every move takes no time", PanelMotion.Length(0, true, true) == 0 && PanelMotion.Length(0, false, false) == 0);

        // F1: turned back, the surface starts at once; otherwise it still starts a little after the content.
        check("Motion review: a closing that turns an opening around starts its surface at once",
            PanelMotion.CloseSurfaceStart(true) == 0 && PanelMotion.CloseSurfaceStart(false) == PanelMotion.CloseSurfaceDelay && PanelMotion.CloseSurfaceDelay > 0);

        // F2: the share of a whole move a turned-back move takes.
        check("Motion review: turned back half way, a move takes half its time",
            Math.Abs(PanelMotion.RemainingShare((300, 400, 200)) - .5) < 1e-9 && Math.Abs(PanelMotion.RemainingShare((300, 200, 400)) - .5) < 1e-9);
        check("Motion review: turned back just after it started, it still takes a readable share",
            PanelMotion.RemainingShare((205, 400, 200)) == PanelMotion.RedirectMin && PanelMotion.RedirectMin == .4);
        check("Motion review: turned back near its end, it takes nearly the whole move", PanelMotion.RemainingShare((395, 400, 200)) is > .97 and <= 1
            && PanelMotion.RemainingShare((420, 400, 200)) == 1);
        check("Motion review: the size that has most to undo decides, a size that does not change has no say",
            Math.Abs(PanelMotion.RemainingShare((300, 400, 200), (90, 100, 40)) - (50.0 / 60)) < 1e-9
            && Math.Abs(PanelMotion.RemainingShare((300, 400, 200), (36, 36, 36)) - .5) < 1e-9);
        check("Motion review: with nothing to compare, the whole move", PanelMotion.RemainingShare((36, 36, 36)) == 1
            && PanelMotion.RemainingShare((double.NaN, double.NaN, 200)) == 1 && PanelMotion.RemainingShare() == 1);

        // F1, F2: only an opening or closing still under way is turned back (MoveTurn).
        var turn = new MoveTurn();
        turn.Started(1000, 360, opening: true, 480, 300);
        check("Motion review: closing well after the opening has ended is a plain closing", !turn.Turns(1000 + 360 + 300, false) && !turn.Turns(1360, false)
            && turn.Share(1660, false, 480, 300, 200, 36) == 1);
        check("Motion review: closing while the opening is under way turns it back, in the share it has to undo",
            turn.Turns(1150, false) && Math.Abs(turn.Share(1150, false, 340, 168, 200, 36) - .5) < 1e-9);
        check("Motion review: a move the same way as the one under way turns nothing back", !turn.Turns(1150, true) && turn.Share(1150, true, 340, 168, 480, 300) == 1);
        turn.Started(2000, PanelMotion.Length(360, closing: true, keyboard: false), opening: false, 200, 36);
        check("Motion review: opening after the closing has ended is a plain opening", !turn.Turns(2000 + 270, true) && !turn.Turns(2600, true)
            && turn.Share(2600, true, 200, 36, 480, 300) == 1);
        check("Motion review: opening while the closing is under way turns it back",
            turn.Turns(2100, true) && turn.Share(2100, true, 400, 250, 480, 300) is >= .4 and < 1);
        // The collapsed surface laid out again mid-closing (a new collapsed width) carries the closing on.
        check("Motion review: a lay-out during a closing carries it on over what is left of it", turn.Carried(2100, false, 360) == 170
            && turn.Carried(2250, false, 360) == 144 && turn.Carried(2100, false, 0) == 0);
        check("Motion review: a lay-out after the closing, or against the move under way, is a move of its own",
            turn.Carried(2270, false, 360) == 0 && turn.Carried(2100, true, 360) == 0);
        turn.Started(3000, 0, opening: true, 480, 300);
        check("Motion review: a move without animation leaves nothing to turn back", !turn.Turns(3000, false) && turn.Carried(3000, true, 360) == 0);

        // F7: the picture beneath a pop-up waits for its entrance, and then follows changes once a hover has settled.
        var popup = new PopupPicture();
        bool early = popup.Changed();
        bool missed = popup.Enter();
        check("Motion review: a pop-up's picture is not taken before it has entered, and is after", !early && popup.Changed() && popup.Entered);
        check("Motion review: a change while a pop-up entered is taken right after its first picture, once", missed && !popup.Enter());
        var still = new PopupPicture();
        check("Motion review: a pop-up that did not change while it entered takes only its first picture", !still.Enter() && still.Changed());
        check("Motion review: a pop-up's picture is taken again once a hover's fade has settled",
            PopupPicture.SettleMilliseconds((int)(Eased.Hover * 1000)) == (int)(Eased.Hover * 1000) + 40);
    }
}
