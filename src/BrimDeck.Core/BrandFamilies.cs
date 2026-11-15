using System.Reflection;

namespace BrimDeck.Core;

// What a USB HID collection says of itself, as the brand families match on it.
public readonly record struct HidShape(int Vendor, int Product, int UsagePage, int Usage, int Interface, int InputLength, int OutputLength,
    int FeatureLength, IReadOnlySet<byte> OutputIds, IReadOnlySet<byte> FeatureIds, string Name)
{
    public bool Declares(BrandWay way) => way.Transport switch
    {
        WayTransport.Output or WayTransport.OutputListen or WayTransport.OutputGetInput => OutputIds.Contains(way.Request.Length > 0 ? way.Request[0] : (byte)0),
        WayTransport.Feature or WayTransport.FeatureListen or WayTransport.FeatureGet => FeatureIds.Contains(way.Request.Length > 0 ? way.Request[0] : (byte)0),
        WayTransport.Listen => InputLength > 0,
        _ => false
    };
}

// How a read goes over the collection:
// - Output: an output report (Request, report id first) is written; the answer comes as an input report (Parse).
// - Feature: a feature report is set (Request), then after GapMs got back (report id Request[0], FeatureLength bytes) (Parse).
// - FeatureListen: a feature report is set; the answer comes as an input report.
// - OutputListen: as Output, the request only waking the device into sending what it sends anyway.
// - Listen: nothing is sent; the device's own input reports are read for TimeoutMs (a battery it reports by itself).
// - OutputGetInput: an output report is written, then input report ReplyId is fetched (HidD_GetInputReport) every GapMs
//   until it parses or TimeoutMs passes; the report as it was before the request (a stale answer) is never taken.
// - FeatureGet: nothing is written; feature report Request[0] is fetched (FeatureLength bytes) and parsed.
public enum WayTransport { Output, Feature, FeatureListen, OutputListen, Listen, OutputGetInput, FeatureGet }

// One of a brand's battery reads. Name: stable across versions (kept in device-reads.json), "brand.read". Before: frames sent
// first the same way (a session or mode a read needs), After: frames that undo them, sent whatever the answer. Attempts: how
// many times the request is sent before the read counts as unanswered. Parse: the reading in one report, or null for any
// other report (another command's answer, the request echoed, a notice). StripZero: input reports handed over without a
// leading zero report id.
public sealed record BrandWay(string Name, WayTransport Transport, byte[] Request, Func<byte[], BatteryRead?> Parse)
{
    public int TimeoutMs { get; init; } = 600;
    public int Attempts { get; init; } = 2;
    public int GapMs { get; init; } = 30;
    public int FeatureLength { get; init; }
    public IReadOnlyList<byte[]> Before { get; init; } = [];
    public IReadOnlyList<byte[]> After { get; init; } = [];
    public bool StripZero { get; init; }
    // OutputGetInput: the input report fetched for the answer.
    public byte ReplyId { get; init; }
    // A question asked first, the same way, whose answer says whether the device can answer the read at all (a headset
    // linked to its base): Gated true goes on, false means the device is off or asleep (the read answers that), null is not
    // the answer. With no answer to it, the read is not sent.
    public byte[]? Gate { get; init; }
    public Func<byte[], bool?>? Gated { get; init; }
    // At most one read in this time on one collection; between, the last reading stands (a device whose link suffers from
    // being asked often).
    public int MinIntervalMs { get; init; }
    // A second question, asked the same way only once the level came, whose answer says whether the device charges (a headset
    // that tells it only there): Then is sent, Charging reads its answer (null: not its answer). Unanswered, the level stands
    // as read. Not for Listen (nothing is sent).
    public byte[]? Then { get; init; }
    public Func<byte[], bool?>? Charging { get; init; }

    // The level with the charging question's answer.
    public static BatteryRead WithCharging(BatteryRead level, bool? charging) => charging is true ? level with { Charging = true } : level;

    // How every question of the read goes, its gate and charging question as well as its request: the transport's way.
    public WayStep Step => Transport switch
    {
        WayTransport.Feature => WayStep.SetThenGet,
        WayTransport.FeatureGet => WayStep.Get,
        WayTransport.OutputGetInput => WayStep.WriteThenFetch,
        WayTransport.Listen => WayStep.Listen,
        _ => WayStep.SendThenListen
    };
    // The length a feature report is got back with, for any question: FeatureLength where the read gives it (a firmware that
    // stalls on any other, such as the Redragon M690 PRO's 8 bytes), else the collection's, and never shorter than the
    // question set.
    public int GetLength(byte[] question, int collectionLength)
        => FeatureLength > 0 ? FeatureLength : Transport == WayTransport.FeatureGet ? collectionLength : Math.Max(question.Length, collectionLength);
}

// SendThenListen: the question is sent (written, or set as a feature report for FeatureListen) and the answer read off the input
// reports; SetThenGet: set as a feature report, then, GapMs later, the feature report of the question's id got back; Get: only
// got back, nothing set; WriteThenFetch: GetInputExchange; Listen: nothing sent, the input reports read.
public enum WayStep { SendThenListen, SetThenGet, Get, WriteThenFetch, Listen }

// WayTransport.OutputGetInput, apart from the device so it can be tried. A receiver can hold several old reports, each fetch
// giving the next (vigil-watt's akg.md counted 13): they are fetched first, until one repeats (or nothing comes, or DrainMost),
// and none of them is taken. Then the request is sent and the reply fetched every gapMs until it parses or timeoutMs passes. A
// reply the same as the last old report is passed over (it may be the old one still) except at the last fetch: a level that
// has not changed comes back byte for byte the same (HaloBattery's skullcandy.py takes it there too).
public static class GetInputExchange
{
    public const int DrainMost = 16;
    public static T? Run<T>(Func<byte[]?> fetch, Action send, Func<byte[], T?> parse, int timeoutMs, int gapMs, Action<int> sleep, Func<long> clock) where T : struct
    {
        byte[]? stale = null;
        for (int i = 0; i < DrainMost; i++)
        {
            var old = fetch();
            if (old is null || stale is not null && old.AsSpan().SequenceEqual(stale)) break;
            stale = old;
        }
        send();
        int gap = Math.Max(10, gapMs);
        long deadline = clock() + timeoutMs;
        while (true)
        {
            sleep(gap);
            bool last = clock() + gap >= deadline;
            if (fetch() is { } reply && (last || stale is null || !reply.AsSpan().SequenceEqual(stale)) && parse(reply) is { } value) return value;
            if (last) return null;
        }
    }
}

// A brand's devices and their reads. Takes: whether a collection is the brand's to read (its vendor, and for a vendor id
// other brands share, its products), from its shape alone, never by sending anything. Ways: the reads that collection takes,
// best first; a way is only sent to a collection that declares its report id (HidShape.Declares). Kind: what the device is
// when its name does not say. Prefix: the device key's prefix (stable: user choices are kept under the key). DisplayName: a
// better name than the product string, if any.
public sealed record BrandFamily(string Brand, Func<HidShape, bool> Takes, Func<HidShape, IReadOnlyList<BrandWay>> Ways)
{
    public Func<HidShape, DeviceKind?> Kind { get; init; } = _ => null;
    public string Prefix { get; init; } = "brand";
    public Func<HidShape, string?> DisplayName { get; init; } = _ => null;
    // Read only while none of these processes runs (the brand's own software would lose its answers to us).
    public IReadOnlyList<string> StandAsideFor { get; init; } = [];
}

// Marks a class whose static Families (IEnumerable<BrandFamily>) are brand families: each brand lives in its own file, so
// adding one touches no other.
[AttributeUsage(AttributeTargets.Class)]
public sealed class BrandFamiliesAttribute : Attribute;

// Every brand family, in the order of their brands' names.
public static class BrandFamilies
{
    private static readonly Lazy<IReadOnlyList<BrandFamily>> Loaded = new(() => [.. typeof(BrandFamilies).Assembly.GetTypes()
        .Where(type => type.GetCustomAttribute<BrandFamiliesAttribute>() is not null)
        .OrderBy(type => type.Name, StringComparer.Ordinal)
        .SelectMany(type => type.GetProperty("Families", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IEnumerable<BrandFamily> ?? [])]);

    public static IReadOnlyList<BrandFamily> All => Loaded.Value;

    // The family a collection belongs to, and the reads it takes there that it declares; null when no family takes it.
    public static (BrandFamily Family, IReadOnlyList<BrandWay> Ways)? Find(HidShape shape)
    {
        foreach (var family in All)
        {
            if (!family.Takes(shape)) continue;
            var ways = family.Ways(shape).Where(shape.Declares).ToList();
            return (family, ways);
        }
        return null;
    }

    // A frame of the given length, starting with the given bytes.
    public static byte[] Frame(int length, params byte[] head) { var frame = new byte[Math.Max(length, head.Length)]; head.CopyTo(frame, 0); return frame; }
}
