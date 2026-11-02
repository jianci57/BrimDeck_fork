using BrimDeck.Core;

internal static class CollapsedHoverTests
{
    internal static void Run(Action<string, bool> check)
    {
        check("A capsule up to 20 DIPs from the edge keeps the strip",
            CollapsedHover.HasBridge(CompactStyle.Capsule, new DipRect(0, 20, 196, 32), 0)
            && CollapsedHover.Zone(CompactStyle.Capsule, new DipRect(10, 20, 196, 32), 0) == new DipRect(10, 0, 196, 52));
        check("A capsule lower than that senses only itself",
            !CollapsedHover.HasBridge(CompactStyle.Capsule, new DipRect(0, 20.5, 196, 32), 0)
            && CollapsedHover.Zone(CompactStyle.Capsule, new DipRect(10, 60, 300, 48), 0) == new DipRect(10, 60, 300, 48));
        check("Notch and indicator are always bridged",
            CollapsedHover.HasBridge(CompactStyle.Notch, new DipRect(0, 80, 238, 32), 0) && CollapsedHover.HasBridge(CompactStyle.Line, new DipRect(0, 80, 56, 4), 0)
            && CollapsedHover.Zone(CompactStyle.Line, new DipRect(300, 30, 120, 6), 0) == new DipRect(292, 0, 136, 38));
        check("The sensing area follows the surface sideways and in size",
            CollapsedHover.Zone(CompactStyle.Notch, new DipRect(-40, 0, 400, 48), 0) == new DipRect(-40, 0, 400, 48)
            && CollapsedHover.Zone(CompactStyle.Capsule, new DipRect(900, 8, 500, 64), 0) == new DipRect(900, 0, 500, 72));

        // With default settings the resolved shapes sense the same boxes as the fixed sizes did, centred on a 1920-wide monitor.
        var monitor = new DipRect(0, 0, 1920, 1080);
        var bottomTaskbar = new DipRect(0, 0, 1920, 1032);
        DipRect Default(CompactStyle style, DipRect work) { var shape = CollapsedGeometry.Resolve(new DeckSettings(), style, monitor, work); return CollapsedHover.Zone(shape.Style, shape.Surface, work.Y); }
        check("Default shapes sense today's notch, capsule and indicator boxes",
            Default(CompactStyle.Notch, bottomTaskbar) == new DipRect(841, 0, 238, 32)
            && Default(CompactStyle.Capsule, bottomTaskbar) == new DipRect(862, 0, 196, 37)
            && Default(CompactStyle.Line, bottomTaskbar) == new DipRect(924, 0, 72, 10));
        // A taskbar at the top moves the capsule below it, so the strip would cover the taskbar; the notch stays at the edge.
        var topTaskbar = new DipRect(0, 48, 1920, 1032);
        check("Below a top taskbar the capsule bridges to the work area, not over the taskbar",
            Default(CompactStyle.Capsule, topTaskbar) == new DipRect(862, 48, 196, 37) && Default(CompactStyle.Notch, topTaskbar).Y == 0);
        check("The capsule's threshold is measured from the work area's top",
            CollapsedHover.HasBridge(CompactStyle.Capsule, new DipRect(0, 68, 196, 32), 48)
            && CollapsedHover.Zone(CompactStyle.Capsule, new DipRect(10, 68, 196, 32), 48) == new DipRect(10, 48, 196, 52)
            && !CollapsedHover.HasBridge(CompactStyle.Capsule, new DipRect(0, 68.5, 196, 32), 48)
            && CollapsedHover.Zone(CompactStyle.Capsule, new DipRect(10, 90, 196, 32), 48) == new DipRect(10, 90, 196, 32)
            && !CollapsedHover.HasBridge(CompactStyle.Capsule, new DipRect(0, 25, 196, 32), 0));
        check("A capsule anywhere down to the bottom of the work area senses only itself",
            CollapsedHover.Zone(CompactStyle.Capsule, new DipRect(862, 1000, 196, 32), 0) == new DipRect(862, 1000, 196, 32)
            && CollapsedHover.Zone(CompactStyle.Capsule, new DipRect(862, 1048, 196, 32), 48) == new DipRect(862, 1048, 196, 32)
            && CollapsedHover.Zone(CompactStyle.Capsule, new DipRect(0, 540, 800, 64), 0) == new DipRect(0, 540, 800, 64));
        check("Notch and line ignore the work area's top",
            CollapsedHover.Zone(CompactStyle.Notch, new DipRect(100, 0, 238, 32), 48) == new DipRect(100, 0, 238, 32)
            && CollapsedHover.Zone(CompactStyle.Line, new DipRect(500, 52, 56, 4), 48) == new DipRect(492, 0, 72, 58)
            && CollapsedHover.HasBridge(CompactStyle.Line, new DipRect(0, 200, 56, 4), 48));
    }
}
