namespace BrimDeck.Core;

// Darmoshark (达摩鲨) mice. Two firmware families, each on a chip maker's shared vendor id, so only these products:
// - The M3 and N3 on Telink's 0x248A: the 2.4 GHz receiver FF30 (product string "RF Dongle", interface 1), the M3 on its
//   cable FF12 and the N3 on its cable FF10, collection usage page 0x8C usage 1. Feature report 0x51 (20 bytes) with
//   opcode 0x06 (identity), read back from the same report: 51 06 <protocol, 2 bytes> <vendor id 8A 24> <product id, 2
//   bytes> <firmware, 2 bytes> <work mode> <percent> <power state>. The receiver answers into the feature buffer from 10 to
//   120 ms later and until then the buffer holds the previous answer, so the bond read (opcode 0x03, which the receiver
//   answers itself at once) is set first: a buffer still holding it is no answer, and a stale identity cannot be read as
//   a fresh one. The bond answer 51 03 01 <the bonded mouse's vendor and product id> <link> says whether the mouse is
//   linked: through the receiver, a buffer still holding it with link 0 is the mouse asleep or off (the receiver then
//   answers every other read "no link", and the identity never lands). The cable does not answer the bond read. The vendor
//   id in the identity tells it from the request read back. Power state: 0 on battery, 1 charging (bits 0 and 1; 2 is
//   full). From Barba2k2/darmoshark-m3-configurator's PROTOCOL.md (MIT; the channel, the receiver's "no link" state and the
//   identity layout, measured on an M3) and, facts only, Asuka-Liu/M3-N87-battery-viewer (no license; FF30 interface 1,
//   page 0x8C, the percent offset and the bond read first, measured on an M3 receiver), TheWebDog/quiet-battery (its own
//   license; the N3 on FF30, the bond answer's link byte, the echoed ids and the power byte 0 discharging, from
//   Darmoshark's web driver) and OpenMouse's Keychron "1k" notes (AGPL; the power state's bits, from Keychron Launcher,
//   which Darmoshark's web driver is built on).
// - The M3S Pro on Nordic's 0x1915: the 1K receiver 0723 and the mouse on its cable 073C, usage page 0xFF0A usage 1, an
//   unnumbered 64-byte output and input report pair. The power query [type 1, 0, opcode 0x81, sub 1, zeros, checksum]
//   (the 64 bytes sum to 0xA1), with 0x40 added to the type through the receiver. The answer echoes the type (bit 6 masked)
//   and the sub-command; its percent is at 6, or at 11 when 6 and 7 read 15 19 (the dms_v2 layout). From
//   ayasaki-exs/darmoshark-battery's PROTOCOL.md and darmoshark_protocol.h (GPL; facts only, as its author reports them).
[BrandFamilies]
public static class DarmosharkMice
{
    public const int TelinkVendor = 0x248A, NordicVendor = 0x1915;
    public const int Receiver = 0xFF30, M3Cable = 0xFF12, N3Cable = 0xFF10, M3sReceiver = 0x0723, M3sCable = 0x073C;

    public static byte[] IdentityRequest() => BrandFamilies.Frame(21, 0x51, 0x06);
    public static byte[] BondRequest() => BrandFamilies.Frame(21, 0x51, 0x03);
    public static BatteryRead? Identity(byte[] r)
        => r.Length >= 13 && r[0] == 0x51 && r[1] == 0x06 && r[4] == 0x8A && r[5] == 0x24 && r[11] is > 0 and <= 100
            ? new(r[11], (r[12] & 0x03) == 1) : null;
    // Through the receiver: the identity, or the bond answer still in the buffer saying the mouse is not linked.
    public static BatteryRead? ReceiverIdentity(byte[] r)
        => r.Length >= 8 && r[0] == 0x51 && r[1] == 0x03 && r[7] == 0x00 ? new(null, Asleep: true) : Identity(r);

    // The power query, report id 0 first (65 bytes).
    public static byte[] PowerRequest(bool receiver)
    {
        var frame = BrandFamilies.Frame(65, 0x00, (byte)(receiver ? 0x41 : 0x01), 0x00, 0x81, 0x01);
        int sum = 0;
        for (int i = 1; i < 64; i++) sum += frame[i];
        frame[64] = (byte)(0xA1 - sum);
        return frame;
    }
    // The answer without its report id (StripZero).
    public static BatteryRead? Power(byte[] r)
    {
        if (r.Length < 12 || (r[0] & 0x3F) != 0x01 || r[3] != 0x01) return null;
        int level = r[6] == 0x15 && r[7] == 0x19 ? r[11] : r[6];
        return level is > 0 and <= 100 ? new(level) : null;
    }

    private static bool Telink(HidShape shape) => shape.Vendor == TelinkVendor && shape.Product is Receiver or M3Cable or N3Cable
        && MouseShapes.Collection(shape, 0x008C, 0x01) && shape.FeatureLength >= 21;
    private static bool Nordic(HidShape shape) => shape.Vendor == NordicVendor && shape.Product is M3sReceiver or M3sCable
        && MouseShapes.Collection(shape, 0xFF0A, 0x01) && shape.InputLength >= 13 && shape.OutputLength >= 65;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Darmoshark", shape => Telink(shape) || Nordic(shape), shape => Telink(shape)
            ? [new BrandWay("darmoshark.identity", WayTransport.Feature, IdentityRequest(), shape.Product == Receiver ? ReceiverIdentity : Identity)
                { Before = [BondRequest()], GapMs = 120, FeatureLength = 21 }]
            : [new BrandWay("darmoshark.power", WayTransport.Output, PowerRequest(shape.Product == M3sReceiver), Power) { TimeoutMs = 800, StripZero = true }])
        {
            Kind = _ => DeviceKind.Mouse, Prefix = "darmoshark",
            DisplayName = shape => shape.Product switch
            {
                M3Cable => "Darmoshark M3", N3Cable => "Darmoshark N3", M3sReceiver or M3sCable => "Darmoshark M3S Pro", _ => null
            },
        },
    ];
}
