namespace BrimDeck.Core;

// Turns the horizontal wheel into single swipes. A precision touchpad reports a two-finger swipe as many small parts and keeps
// sending them while the swipe coasts; a tilting mouse wheel repeats whole notches while it is held. Either way one gesture acts
// once: the parts add up until a whole notch, and the rest of the gesture is passed over until the wheel has been quiet for
// Quiet. A swipe the other way made while the first still coasts is a swipe of its own, though: the coasting parts keep the wheel
// from ever being quiet for a second or so, and a swipe back made then was lost (one had to wait, or move the pointer, which
// ends the coasting). Its parts add up by themselves, and a whole notch of them acts. A gesture that also scrolls up or down is
// a scroll, not a swipe, and never acts.
public sealed class HorizontalSwipe
{
    // One notch (WHEEL_DELTA).
    public const int Threshold = 120;
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);
    // _acted: the way the gesture acted, 0 while it has not (or was a scroll). _back: the parts the other way since.
    private int _rest, _back, _acted;
    private bool _spent;
    private DateTimeOffset _last = DateTimeOffset.MinValue, _vertical = DateTimeOffset.MinValue;

    // A vertical wheel part arrived.
    public void Vertical(DateTimeOffset now) => _vertical = now;

    // Takes one horizontal part (positive: to the right, as WM_MOUSEHWHEEL reports it) and answers 1 for a swipe to the right,
    // -1 for one to the left, or 0 while the gesture has not acted or has already acted.
    public int Feed(int delta, DateTimeOffset now)
    {
        if (now - _last >= Quiet) { _rest = 0; _back = 0; _acted = 0; _spent = false; }
        _last = now;
        if (now - _vertical < Quiet) { _spent = true; _acted = 0; }
        if (delta == 0) return 0;
        if (_spent)
        {
            // Parts with the swipe that acted are its coasting rest, and start the count of the way back afresh: a flick of the
            // fingers as they lift, between such parts, never adds up to a notch.
            if (_acted == 0 || Math.Sign(delta) == _acted) { _back = 0; return 0; }
            _back += delta;
            if (Math.Abs(_back) < Threshold) return 0;
            _acted = -_acted; _back = 0;
            return _acted;
        }
        if (Math.Sign(delta) != Math.Sign(_rest)) _rest = 0;
        _rest += delta;
        if (Math.Abs(_rest) < Threshold) return 0;
        int direction = Math.Sign(_rest);
        _rest = 0; _spent = true; _acted = direction;
        return direction;
    }
}
