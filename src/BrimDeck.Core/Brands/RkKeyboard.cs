namespace BrimDeck.Core;

// The RK R98Pro V2 keyboard on its 2.4 GHz receiver, 258A:01BB or 258A:0233. 0x258A is Sinowealth's, and these receivers
// serve other makers' keyboards too, so only their vendor collection (usage page 0xFF02 usage 2, 20-byte input and output
// reports) is taken, and the keyboard is asked who it is before its battery: only the R98Pro V2's identity lets the read go
// on. A request is 13 <command> 01, zeros, and at 19 the sum of bytes 0 to 18 (0x13 + command + 1). An answer is
// 13 <command> 01 00 <length> <data>, its byte 19 the sum of bytes 0 to 18; the request read back has length 0. The identity
// (command 05) answers 6 bytes, 0A 00 00 00 00 46 for the R98Pro V2; the battery (command 4A) 2 bytes, the percent first.
// Another keyboard's identity, or none (the keyboard off: the receiver leaves identity queries unanswered), stops the read.
// From mousedock's RkKeyboardProtocol.cs (MIT; taken from RK's own Windows driver, CDev3632::GetPower). A zero percent
// is not taken; the battery answer has no charging state.
[BrandFamilies]
public static class RkKeyboard
{
    public const int Vendor = 0x258A;
    public static readonly int[] Receivers = [0x01BB, 0x0233];
    public const byte Identity = 0x05, Battery = 0x4A;
    private static readonly byte[] R98ProV2 = [0x0A, 0x00, 0x00, 0x00, 0x00, 0x46];

    public static byte[] Request(byte command)
    {
        var frame = BrandFamilies.Frame(20, 0x13, command, 0x01);
        frame[19] = (byte)(0x13 + command + 1);
        return frame;
    }

    private static bool Valid(byte[] r, byte command, byte length)
    {
        if (r.Length != 20 || r[0] != 0x13 || r[1] != command || r[2] != 0x01 || r[3] != 0x00 || r[4] != length) return false;
        int sum = 0;
        for (int i = 0; i < 19; i++) sum += r[i];
        return (byte)sum == r[19];
    }

    // The gate: true for the R98Pro V2's identity, null for anything else (another keyboard is never read).
    public static bool? IsR98ProV2(byte[] r) => Valid(r, Identity, 6) && r.AsSpan(5, 6).SequenceEqual(R98ProV2) ? true : null;
    public static BatteryRead? Level(byte[] r) => Valid(r, Battery, 2) && r[5] is > 0 and <= 100 ? new(r[5]) : null;

    private static bool Takes(HidShape s) => s.Vendor == Vendor && Receivers.Contains(s.Product) && s.UsagePage == 0xFF02 && s.Usage == 0x0002
        && s.InputLength == 20 && s.OutputLength == 20;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("RK", Takes,
            _ => [new BrandWay("rk.r98pro", WayTransport.Output, Request(Battery), Level) { Gate = Request(Identity), Gated = IsR98ProV2, TimeoutMs = 1000 }])
        { Kind = _ => DeviceKind.Keyboard, Prefix = "rk", DisplayName = _ => "RK R98Pro V2" },
    ];
}
