namespace BrimDeck.Core;

// Finalmouse on its own vendor id 0x361D: the dongle's vendor collection (usage page 0xFF00 usage 1), as Finalmouse's web
// configurator XPanel talks to it. Requests are output report 0x04, 63 bytes: <length> <0x80 | command> <payload length>
// <payload>, the length counting the two bytes after it and the payload; the dongle relays them to the mouse, and the
// answers come back on input report 0x05: <length> <command> <payload length> <payload> (the command's high bit masked).
// Command 38 (battery status) answers the state of charge, 0 to 100, and the millivolts; command 5 the millivolts alone,
// which XPanel turns into a percent by its discharge curve (volts 3.0, 3.62, 3.66, 3.74, 3.88, 4.17, 4.38 for 0.2, 5, 10,
// 25, 50, 75 and 100 %, a line between the points, rounded); millivolts outside 2500 to 4600 are no reading (MouseDock). The
// UltralightX's dongle (0100) measured by HaloBattery never answered command 38, so its reading is the voltage's (coarse,
// and read high while the mouse charges: command 37's charging flag is a second request, not sent). Command 36 (link
// state, 0 while the mouse is not connected) is not asked first: MouseDock finds the UltralightX's dongle may not answer
// it, and an unanswered first question would stop the read. The mouse on its cable (0102) only takes firmware updates and
// is not sent to; the Starlight X dongles use output report 0x01 (OpenMouse and MouseDock) and so never declare report
// 0x04. From HaloBattery's finalmouse.py (MIT; measured on a 361D:0100 dongle: 0x1064 = 4196 mV, 78 %), MouseDock's
// Finalmouse reader (MIT; the frame checks and the voltage bounds) and OpenMouse's Finalmouse module (AGPL; facts only:
// the same curve and report ids).
[BrandFamilies]
public static class FinalmouseMice
{
    public const int Vendor = 0x361D, UltralightXDongle = 0x0100, UltralightXCable = 0x0102;
    public const byte BatteryStatus = 38, BatteryVoltage = 5;

    public static byte[] Request(byte command) => BrandFamilies.Frame(4, 0x04, 0x02, (byte)(0x80 | command), 0x00);
    // The payload of an answer to the command, or null.
    private static ReadOnlySpan<byte> Payload(byte[] r, byte command)
    {
        if (r.Length < 4 || r[0] != 0x05 || (r[2] & 0x7F) != command || r[1] != r[3] + 2 || r.Length < 4 + r[3]) return default;
        return r.AsSpan(4, r[3]);
    }
    public static BatteryRead? Status(byte[] r)
    {
        var payload = Payload(r, BatteryStatus);
        return payload.Length >= 3 && payload[0] is > 0 and <= 100 ? new(payload[0]) : null;
    }
    public static BatteryRead? Voltage(byte[] r)
    {
        var payload = Payload(r, BatteryVoltage);
        if (payload.Length < 2) return null;
        int millivolts = payload[0] | payload[1] << 8;
        return millivolts is >= 2500 and <= 4600 && Percent(millivolts) is int percent and > 0 ? new(percent, Coarse: true) : null;
    }

    private static readonly double[] Volts = [3.0, 3.62, 3.66, 3.74, 3.88, 4.17, 4.38];
    private static readonly double[] Percents = [0.2, 5, 10, 25, 50, 75, 100];
    public static int? Percent(int millivolts)
    {
        if (millivolts is 0 or 0xFFFF) return null;
        double volts = millivolts / 1000.0;
        if (volts >= Volts[^1]) return 100;
        if (volts <= Volts[0]) return 0;
        for (int i = 0; i < Volts.Length - 1; i++)
            if (volts <= Volts[i + 1])
                return (int)Math.Floor(Percents[i] + (Percents[i + 1] - Percents[i]) * (volts - Volts[i]) / (Volts[i + 1] - Volts[i]) + 0.5);
        return 100;
    }

    public static bool Takes(HidShape shape) => shape.Vendor == Vendor && shape.Product != UltralightXCable
        && MouseShapes.Collection(shape, 0xFF00, 0x01) && shape.InputLength >= 7;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Finalmouse", Takes, _ =>
        [
            new BrandWay("finalmouse.status", WayTransport.Output, Request(BatteryStatus), Status) { TimeoutMs = 500, Attempts = 1 },
            new BrandWay("finalmouse.voltage", WayTransport.Output, Request(BatteryVoltage), Voltage) { TimeoutMs = 500 },
        ])
        { Kind = _ => DeviceKind.Mouse, Prefix = "finalmouse", DisplayName = shape => shape.Product == UltralightXDongle ? "Finalmouse UltralightX" : null },
    ];
}
