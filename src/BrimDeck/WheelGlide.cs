using System.Windows.Controls;
using System.Windows.Media;

namespace BrimDeck;

// The wheel on a list of the clipboard and file transfer page. A step of the wheel adds `step` (a row or two, a tile, a block) to
// where the list is going, and the list is drawn there as by a spring that never swings (critically damped): it gathers speed,
// slows as it arrives, and a further step only moves the place it is drawn to, so its speed never jumps. (Each step once began
// an eased move of its own from a standstill; steps made in a row then went quick and slow in turn, which read as a stutter.)
// It stops at the place and never passes it, nothing springs back, and the list is not moved again to line an entry up. Without
// animations the list jumps. A list with nothing to scroll leaves the wheel alone (the panel's own wheel then takes it:
// MainWindow.PassWheel). across: the list is the one row that scrolls sideways (FileView.Row).
internal sealed class WheelGlide
{
    // How hard the list is drawn, a second: it has covered most of a step in a tenth of a second and rests in about a quarter.
    public const double Pull = 26;

    private readonly ScrollViewer _viewer;
    private readonly bool _across;
    private double _target, _at, _speed;
    private bool _moving;
    private TimeSpan _frame;

    public WheelGlide(ScrollViewer viewer, double step, bool across = false)
    {
        _viewer = viewer; _across = across;
        viewer.PreviewMouseWheel += (_, e) =>
        {
            if (Room < 1 || e.Delta == 0) return;
            e.Handled = true;
            To(Place - e.Delta / 120.0 * step);
        };
        viewer.Unloaded += (_, _) => Rest();
    }

    private double Room => _across ? _viewer.ScrollableWidth : _viewer.ScrollableHeight;
    private double Offset => _across ? _viewer.HorizontalOffset : _viewer.VerticalOffset;
    // Where the list stands, or where it is going while it moves.
    public double Place => _moving ? _target : Offset;

    // On whole pixels of the display: between them the text would be drawn soft in one frame and sharp in the next.
    private void Set(double offset)
    {
        double scale = VisualTreeHelper.GetDpi(_viewer).DpiScaleX;
        offset = Math.Round(offset * scale) / scale;
        if (_across) _viewer.ScrollToHorizontalOffset(offset); else _viewer.ScrollToVerticalOffset(offset);
    }
    private void Rest()
    {
        if (!_moving) return;
        _moving = false; _speed = 0;
        CompositionTarget.Rendering -= Frame;
    }
    private void To(double target)
    {
        target = Math.Clamp(target, 0, Room);
        if (!Soft.Enabled) { Rest(); Set(target); return; }
        _target = target;
        if (_moving) return;
        // The list's place is kept here while it moves: the viewer takes a new offset only at its next layout, and would answer
        // with the old one meanwhile.
        _at = Offset; _speed = 0; _moving = true; _frame = TimeSpan.MinValue;
        CompositionTarget.Rendering += Frame;
    }
    private void Frame(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs rendering ? rendering.RenderingTime : TimeSpan.Zero;
        // The event comes more than once for a frame at times.
        if (now == _frame) return;
        double passed = _frame == TimeSpan.MinValue ? 1.0 / 120 : Math.Clamp((now - _frame).TotalSeconds, 0, .05);
        _frame = now;
        _target = Math.Clamp(_target, 0, Room);
        (double at, double speed) = Drawn(_at - _target, _speed, passed);
        // Arrived, or about to pass the place (a step back made at speed): it stops there.
        if (Math.Abs(at) < .4 && Math.Abs(speed) < 12 || at != 0 && _at != _target && Math.Sign(at) != Math.Sign(_at - _target)) { _at = _target; Rest(); }
        else { _at = _target + at; _speed = speed; }
        Set(_at);
    }
    // A critically damped spring after `seconds`: `away` from where it rests and moving at `speed` at the start.
    internal static (double Away, double Speed) Drawn(double away, double speed, double seconds)
    {
        double fall = Math.Exp(-Pull * seconds), turn = speed + Pull * away;
        return ((away + turn * seconds) * fall, (speed - turn * Pull * seconds) * fall);
    }

    // The horizontal wheel over the one row (a touchpad's two-finger sideways swipe): the row moves with the fingers, half a DIP
    // for each unit of the wheel. Reaches: whether the row has anywhere to go that way.
    public bool Reaches(int delta) => Room >= 1 && Math.Abs(Math.Clamp(Place + delta * .5, 0, Room) - Place) >= .5;
    public void Swipe(int delta)
    {
        double to = Math.Clamp(Place + delta * .5, 0, Room);
        Rest(); Set(to);
    }
}
