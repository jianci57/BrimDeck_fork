using System.Text.Json;
using BrimDeck.Core;

// The background transparency setting (DeckSettings.SurfaceTransparency): opaque by default and in files saved before it existed,
// and held to 0 to MaxSurfaceTransparency percent whatever a hand-edited file says.
internal static class SurfaceTransparencyTests
{
    public static void Run(Action<string, bool> check)
    {
        check("the surface starts opaque", new DeckSettings().SurfaceTransparency == 0);
        var old = JsonSerializer.Deserialize<DeckSettings>("{}")!; old.Normalize();
        check("a file without the setting keeps the surface opaque", old.SurfaceTransparency == 0);
        var high = new DeckSettings { SurfaceTransparency = 150 }; high.Normalize();
        var low = new DeckSettings { SurfaceTransparency = -20 }; low.Normalize();
        var kept = new DeckSettings { SurfaceTransparency = 35 }; kept.Normalize();
        check("the transparency is held to its range", high.SurfaceTransparency == DeckSettings.MaxSurfaceTransparency && low.SurfaceTransparency == 0 && kept.SurfaceTransparency == 35);
        check("a copy keeps the transparency", kept.Copy().SurfaceTransparency == 35);
    }
}
