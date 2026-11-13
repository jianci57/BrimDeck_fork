namespace BrimDeck.Core;

// What a gesture over the expanded music page does. Saved as numbers, so new members go at the end.
public enum MusicGestureAction { None, NextTrack, PreviousTrack, NextPlayer, PreviousPlayer, PlayPause, VolumeUp, VolumeDown }
public enum GestureDirection { None, Left, Right, Up, Down }

public static class MusicGestures
{
    // How far, in DIPs, a drag goes along its main axis before it counts as a gesture.
    public const double DragDistance = 36;
    // How far the pointer moves with the button down before the drag starts and its trail shows.
    public const double DragStart = 6;
    // How many times a touchpad swipe set to the volume presses the volume key.
    public const int VolumeSteps = 5;
    // How much the volume (0 to 1) changes per DIP a volume drag goes on after it is recognised: 200 DIPs cover the whole range.
    public const double VolumePerDip = .005;

    // Whether a drag in this direction, set to this action, raises the volume as the pointer moves towards positive x or y
    // (1), lowers it (-1), or is no volume drag (0). Moving back past where it was recognised turns the change the other way.
    public static int VolumeSign(GestureDirection direction, MusicGestureAction action)
    {
        int towards = direction is GestureDirection.Right or GestureDirection.Down ? 1 : direction is GestureDirection.Left or GestureDirection.Up ? -1 : 0;
        return action switch { MusicGestureAction.VolumeUp => towards, MusicGestureAction.VolumeDown => -towards, _ => 0 };
    }
    public static double VolumeLevel(double start, int sign, double moved) => Math.Clamp(start + sign * moved * VolumePerDip, 0, 1);

    // How straight a drag must be: the distance from its start to its end over the length of its path.
    public const double Straightness = .8;
    // How much longer the drag must be along its main axis than across it.
    public const double AxisRatio = 1.5;
    // How far the path may stray across its main axis: this share of its length along it, and at least SideSlack DIPs.
    public const double SideShare = .3, SideSlack = 20;

    // The direction of a drag along the path it took, first point to last; screen y grows downward. A drag that is too short,
    // slanted, wanders across its axis, or turns back on itself has none.
    public static GestureDirection Direction(IReadOnlyList<(double X, double Y)> path)
    {
        if (path.Count < 2) return GestureDirection.None;
        var (startX, startY) = path[0]; var (endX, endY) = path[^1];
        double dx = endX - startX, dy = endY - startY;
        bool across = Math.Abs(dx) >= Math.Abs(dy);
        double along = Math.Abs(across ? dx : dy), side = Math.Abs(across ? dy : dx);
        if (along < DragDistance || along < side * AxisRatio) return GestureDirection.None;
        double length = 0;
        for (int i = 1; i < path.Count; i++) length += Math.Sqrt(Math.Pow(path[i].X - path[i - 1].X, 2) + Math.Pow(path[i].Y - path[i - 1].Y, 2));
        if (Math.Sqrt(dx * dx + dy * dy) < length * Straightness) return GestureDirection.None;
        var sides = path.Select(p => across ? p.Y : p.X).ToList();
        if (sides.Max() - sides.Min() > Math.Max(SideSlack, along * SideShare)) return GestureDirection.None;
        return across ? dx < 0 ? GestureDirection.Left : GestureDirection.Right : dy < 0 ? GestureDirection.Up : GestureDirection.Down;
    }

    public static MusicGestureAction Drag(DeckSettings settings, GestureDirection direction) => direction switch
    {
        GestureDirection.Left => settings.MusicDragLeft,
        GestureDirection.Right => settings.MusicDragRight,
        GestureDirection.Up => settings.MusicDragUp,
        GestureDirection.Down => settings.MusicDragDown,
        _ => MusicGestureAction.None
    };
    public static bool AnyDrag(DeckSettings settings) =>
        new[] { settings.MusicDragLeft, settings.MusicDragRight, settings.MusicDragUp, settings.MusicDragDown }.Any(action => action != MusicGestureAction.None);

    // The direction the fingers moved for a horizontal wheel step (1: the view scrolls right, as WM_MOUSEHWHEEL reports a positive
    // value). With the touchpad's reversed setting (ScrollDirection not 0) the view scrolls the way the fingers move; with the
    // standard one it scrolls the other way, as content pushed by the fingers.
    public static GestureDirection Fingers(int step, bool reversed) => step == 0 ? GestureDirection.None
        : (step > 0) == reversed ? GestureDirection.Right : GestureDirection.Left;

    public static MusicGestureAction Swipe(DeckSettings settings, GestureDirection fingers) => fingers switch
    {
        GestureDirection.Left => settings.MusicSwipeLeft,
        GestureDirection.Right => settings.MusicSwipeRight,
        _ => MusicGestureAction.None
    };
}
