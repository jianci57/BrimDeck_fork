namespace BrimDeck.Core;

// Moving the collapsed surface with the move shortcut (HotkeyKind.Move): each press of an arrow key moves it one step, within the
// places its settings allow on its monitor. The notch and the line move along the edge they hang from (Left and Right on the top
// or bottom edge, Up and Down on a side edge); the capsule moves in all four directions. Holding a key repeats it.
public static class KeyboardMove
{
    // One step, in DIPs.
    public const double Step = 24;

    // How a press of the arrow key at this place in HotkeyText.ArrowKeys moves the surface: along its edge (the saved offset, which
    // runs to the right on the top and bottom edges and down the side edges) and down the screen (the capsule's distance from the
    // top). Zero where the key does not move this surface.
    public static (double Along, double Down) Delta(int arrow, CompactStyle style, ScreenEdge edge)
    {
        double x = arrow switch { 0 => -Step, 1 => Step, _ => 0 }, y = arrow switch { 2 => -Step, 3 => Step, _ => 0 };
        if (style == CompactStyle.Capsule) return (x, y);
        return edge is ScreenEdge.Left or ScreenEdge.Right ? (y, 0) : (x, 0);
    }

    // The value one step on from where it is, kept within its limits and in whole DIPs. A value already past a limit (a smaller
    // monitor) comes back inside it first.
    public static double Next(double current, double delta, double min, double max)
    {
        max = Math.Max(min, max);
        double from = double.IsFinite(current) ? Math.Clamp(current, min, max) : (min + max) / 2;
        return Math.Round(Math.Clamp(from + delta, min, max));
    }
}
