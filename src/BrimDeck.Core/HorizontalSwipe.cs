namespace BrimDeck.Core;

// Turns the horizontal wheel into single swipes. A precision touchpad reports a two-finger swipe as many small parts and keeps
// sending them while the swipe coasts; a tilting mouse wheel repeats whole notches while it is held. Either way one gesture acts
// once: the parts add up until a whole notch, and the rest of the gesture is passed over until the wheel has been quiet for
// Quiet. A gesture that also scrolls up or down is a scroll, not a swipe, and never acts.
public sealed class HorizontalSwipe
{
    // One notch (WHEEL_DELTA).
    public const int Threshold = 120;
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);
    private int _rest;
    private bool _spent;
    private DateTimeOffset _last = DateTimeOffset.MinValue, _vertical = DateTimeOffset.MinValue;

    // A vertical wheel part arrived.
    public void Vertical(DateTimeOffset now) => _vertical = now;

    // Takes one horizontal part (positive: to the right, as WM_MOUSEHWHEEL reports it) and answers 1 for a swipe to the right,
    // -1 for one to the left, or 0 while the gesture has not acted or has already acted.
    public int Feed(int delta, DateTimeOffset now)
    {
        if (now - _last >= Quiet) { _rest = 0; _spent = false; }
        _last = now;
        if (now - _vertical < Quiet) _spent = true;
        if (_spent || delta == 0) return 0;
        if (Math.Sign(delta) != Math.Sign(_rest)) _rest = 0;
        _rest += delta;
        if (Math.Abs(_rest) < Threshold) return 0;
        int direction = Math.Sign(_rest);
        _rest = 0; _spent = true;
        return direction;
    }
}
