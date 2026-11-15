using BrimDeck.Core;
using Windows.Networking.Connectivity;

namespace BrimDeck.Native;

// Windows' own record of the traffic over each network, the one the Data usage page in Windows Settings shows, summed over the
// period chosen (TrafficPeriods). It covers the time BrimDeck was not running and needs nothing saved. Only the Wi-Fi, Ethernet and
// cellular networks count, as on that page: a VPN or another virtual network carries traffic the network under it counts as well.
// Windows writes its record down about once a minute, so it is read every RefreshSeconds on a pool thread, never on the sampler's.
internal sealed class TrafficRecord
{
    public const double RefreshSeconds = 30;
    // The IANA interface type of Ethernet (ifType 6); Wi-Fi and cellular profiles say what they are themselves.
    private const uint Ethernet = 6;
    private sealed record Figures(TrafficPeriod Period, double Received, double Sent);
    private volatile Figures? _latest;
    private int _reading;
    private double _askedAt = double.NegativeInfinity;
    private TrafficPeriod _askedFor;

    // The figures for this period, or null until they are read for it (a period just chosen) or where Windows gives none.
    public (double Received, double Sent)? For(TrafficPeriod period) => _latest is { } latest && latest.Period == period ? (latest.Received, latest.Sent) : null;

    // Reads the record again when the last reading is older than RefreshSeconds or was for another period, unless a reading is under way.
    // Called on the sampling thread with its clock in seconds.
    public void Refresh(TrafficPeriod period, double now)
    {
        if (period == _askedFor && now - _askedAt < RefreshSeconds) return;
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0) return;
        _askedFor = period; _askedAt = now;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _latest = Read(period, DateTimeOffset.Now) is { } sums ? new Figures(period, sums.Received, sums.Sent) : null; }
            finally { Volatile.Write(ref _reading, 0); }
        });
    }

    // The bytes received and sent over the counted networks since the period's start; null where Windows' record cannot be read.
    private static (double Received, double Sent)? Read(TrafficPeriod period, DateTimeOffset now)
    {
        try
        {
            var states = new NetworkUsageStates { Roaming = TriStates.DoNotCare, Shared = TriStates.DoNotCare };
            var start = TrafficPeriods.Start(period, now);
            double received = 0, sent = 0;
            foreach (var profile in NetworkInformation.GetConnectionProfiles())
            {
                if (!Counts(profile)) continue;
                foreach (var usage in profile.GetNetworkUsageAsync(start, now, DataUsageGranularity.Total, states).AsTask().GetAwaiter().GetResult())
                { received += usage.BytesReceived; sent += usage.BytesSent; }
            }
            return (received, sent);
        }
        catch (Exception) { return null; }
    }

    private static bool Counts(ConnectionProfile profile)
    {
        try { return profile.IsWlanConnectionProfile || profile.IsWwanConnectionProfile || profile.NetworkAdapter?.IanaInterfaceType == Ethernet; }
        catch (Exception) { return false; }
    }
}
