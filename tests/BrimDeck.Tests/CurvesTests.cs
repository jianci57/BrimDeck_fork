using BrimDeck.Core;

// The curves behind the panel's timed animations (BrimDeck.Soft).
internal static class CurvesTests
{
    public static void Run(Action<string, bool> check)
    {
        double Out(double x) => Curves.Bezier(.22, .8, .28, 1, x);
        check("Curves: a Bézier curve starts at 0 and ends at 1", Out(0) == 0 && Math.Abs(Out(1) - 1) < 1e-9);
        // The soft landing is ahead of time from the start: past nine tenths halfway through, as the CSS curve is. Read the
        // other way round (EasingMode.EaseOut on EaseInCore) it would be at a few percent there.
        check("Curves: the soft landing is quick at first", Out(.25) > .65 && Out(.5) > .9);
        check("Curves: a CSS ease matches its published values", Math.Abs(Curves.Bezier(.25, .1, .25, 1, .5) - .8024) < .002);
        var samples = Enumerable.Range(0, 101).Select(i => Curves.Bezier(.45, 0, .12, 1, i / 100.0)).ToList();
        check("Curves: the settling curve rises steadily", samples.Zip(samples.Skip(1)).All(p => p.Second >= p.First - 1e-9));
        check("Curves: the settling curve starts slowly", samples[20] < .2);
        check("Curves: a fraction outside the move is held at its ends", Curves.Bezier(.22, .8, .28, 1, -1) == 0 && Math.Abs(Curves.Bezier(.22, .8, .28, 1, 2) - 1) < 1e-9);

        // The monitor's move: what the panel's content, selections and hover follow.
        var follow = Enumerable.Range(0, 101).Select(i => Curves.Follow(i / 100.0)).ToList();
        check("Curves: the monitor's move starts at 0 and ends at 1", follow[0] == 0 && follow[^1] == 1);
        check("Curves: the monitor's move rises steadily and never passes its end", follow.Zip(follow.Skip(1)).All(p => p.Second >= p.First - 1e-12) && follow.All(v => v <= 1));
        check("Curves: the monitor's move leaves at one and a half times its average speed", Math.Abs(follow[1] / .01 - 1.5) < .01);
        var eased = new Eased(0);
        eased.Set(1, true, 100);
        check("Curves: the monitor's move is the one its readings follow (Eased)", Enumerable.Range(0, 21).All(i => Math.Abs(eased.At(100 + Eased.Seconds * i / 20) - Curves.Follow(i / 20.0)) < 1e-9));

        // A hover's feedback leaves quicker than it arrives, and one turned back on its way out goes on from where it is.
        var hover = new Eased(0, Eased.Hover);
        hover.Set(1, true, 200, Eased.Hover);
        check("Curves: a hover arrives over Eased.Hover", hover.Moving(200 + Eased.Hover * .99) && !hover.Moving(200 + Eased.Hover * 1.01));
        hover.Set(0, true, 201, Eased.HoverOut);
        check("Curves: a hover leaves over Eased.HoverOut", hover.Moving(201 + Eased.HoverOut * .99) && !hover.Moving(201 + Eased.HoverOut * 1.01) && hover.At(201 + Eased.HoverOut * 1.01) == 0);
        var turned = new Eased(0, Eased.Hover);
        turned.Set(1, true, 300, Eased.Hover);
        double halfway = turned.At(300 + Eased.Hover / 2);
        turned.Set(0, true, 300 + Eased.Hover / 2, Eased.HoverOut);
        check("Curves: a hover turned back leaves from where it was", Math.Abs(turned.At(300 + Eased.Hover / 2) - halfway) < 1e-9);

        // ExpandBounceTests holds the opening spring's ends, give and attack.
        var bounce = Enumerable.Range(0, 1001).Select(i => Curves.Bounce(i / 1000.0)).ToList();
        check("Curves: the opening spring has settled before the end", bounce.Skip(900).All(v => Math.Abs(v - 1) < .005));
    }
}
