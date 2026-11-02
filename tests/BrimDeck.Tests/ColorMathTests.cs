using BrimDeck.Core;

internal static class ColorMathTests
{
    internal static void Run(Action<string, bool> check)
    {
        check("HSV primaries convert to RGB",
            ColorMath.FromHsv(0, 1, 1) == (255, 0, 0) && ColorMath.FromHsv(120, 1, 1) == (0, 255, 0) && ColorMath.FromHsv(240, 1, 1) == (0, 0, 255));
        check("HSV hue wraps and out-of-range values clamp",
            ColorMath.FromHsv(360, 1, 1) == (255, 0, 0) && ColorMath.FromHsv(-120, 1, 1) == (0, 0, 255) && ColorMath.FromHsv(30, 2, -1) == (0, 0, 0));
        check("Zero saturation gives gray", ColorMath.FromHsv(200, 0, .5) == (128, 128, 128));
        var (h, s, v) = ColorMath.ToHsv(0xB9, 0xA8, 0xFF);
        check("RGB converts to HSV", Math.Abs(h - 251.7) < .1 && Math.Abs(s - .341) < .001 && v == 1);
        check("Grays report no hue or saturation", ColorMath.ToHsv(128, 128, 128) is (0, 0, _) && ColorMath.ToHsv(0, 0, 0) == (0, 0, 0));
        bool roundTrip = true;
        for (int r = 0; r < 256; r += 15) for (int g = 0; g < 256; g += 17) for (int b = 0; b < 256; b += 51)
        {
            var hsv = ColorMath.ToHsv((byte)r, (byte)g, (byte)b);
            roundTrip &= ColorMath.FromHsv(hsv.H, hsv.S, hsv.V) == ((byte)r, (byte)g, (byte)b);
        }
        check("RGB to HSV and back is lossless", roundTrip);
        foreach (var preset in AppPresets.Palette)
        {
            var c = Convert.FromHexString(preset[1..]);
            var hsv = ColorMath.ToHsv(c[0], c[1], c[2]); var back = ColorMath.FromHsv(hsv.H, hsv.S, hsv.V);
            roundTrip &= ColorMath.ToHex(back.R, back.G, back.B) == preset;
        }
        check("Preset colors survive the picker round trip", roundTrip);
        check("Hex input is normalized for the picker",
            AppPresets.NormalizeColor("b9a8ff") == "#B9A8FF" && AppPresets.NormalizeColor(" #b9a8ff ") == "#B9A8FF"
            && AppPresets.NormalizeColor("b9a8f") == "" && AppPresets.NormalizeColor("zzzzzz") == "" && ColorMath.ToHex(1, 171, 255) == "#01ABFF");
    }
}
