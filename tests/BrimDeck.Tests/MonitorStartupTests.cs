using BrimDeck.Core;

// The pieces that keep the monitor's start light: swallowed failures noted once, and the cache of program names kept bounded.
static class MonitorStartupTests
{
    public static void Run(Action<string, bool> check)
    {
        double clock = 0;
        var lines = new List<string>();
        var notes = new FailureNotes(() => clock, lines.Add, 600);
        var error = new InvalidOperationException("boom");
        check("Failure notes: the first failure of a kind is written", notes.Note("battery", error) && lines.Count == 1 && lines[0].StartsWith("battery: System.InvalidOperationException: boom"));
        clock = 5;
        check("Failure notes: the same kind from the same source is not written again within the quiet time", !notes.Note("battery", new InvalidOperationException("again")) && lines.Count == 1);
        check("Failure notes: another kind, or another source, is written", notes.Note("battery", new System.IO.IOException("io")) && notes.Note("storage", error) && lines.Count == 3);
        clock = 700;
        check("Failure notes: after the quiet time the kind is written again", notes.Note("battery", error) && lines.Count == 4);
        for (int i = 0; i < 200; i++) notes.Note("flood", "kind " + i, "x");
        check("Failure notes: the kinds kept are bounded", lines.Count(line => line.StartsWith("flood")) <= 64);

        // A full set of kinds: one whose quiet time ran out is still written again, and it makes room by letting the expired ones go.
        double later = 5000;
        var full = new FailureNotes(() => later, lines.Add, 600);
        for (int i = 0; i < 64; i++) full.Note("full", "kind " + i, "x");
        later = 5700;
        int before = lines.Count;
        check("Failure notes: with every kind slot taken, a kind whose quiet time ran out is written again", full.Note("full", "kind 3", "again") && lines.Count == before + 1);

        var snapshot = new MonitorSnapshot(DateTimeOffset.Now, 4, Reading<CpuSample>.Of(new CpuSample([], Reading<double>.Unavailable("x"))), Reading<MemorySample>.Of(new MemorySample(8, 4, 1, 2, 0)),
            Reading<NetworkSample>.Of(new NetworkSample(1, 1)), Reading<GpuSample>.Unavailable("x"), Reading<ProcessList>.Unavailable("x"))
        { DownHistory = [1, 2], CpuHistory = [.5], PowerHistory = [10], Power = Reading<PowerSample>.Unavailable("x") };
        var early = snapshot.WithoutWindowed("waiting");
        check("Early snapshot: the memory stays, the readings that are rates and their curves wait", early.Memory.HasValue && !early.Cpu.HasValue && !early.Network.HasValue
            && !early.Gpu.HasValue && !early.Processes.HasValue && !early.Power.HasValue && early.Cpu.Reason == "waiting"
            && early.DownHistory.Count == 0 && early.CpuHistory.Count == 0 && early.PowerHistory.Count == 0);

        var cache = Enumerable.Range(0, 300).ToDictionary(i => "app" + i + ".exe", i => i, StringComparer.OrdinalIgnoreCase);
        check("Name cache: under the limit nothing is dropped", NameCache.Trim(cache, ["APP1.EXE"], 300) == 0 && cache.Count == 300);
        int dropped = NameCache.Trim(cache, ["APP1.EXE", "app2.exe"], 256);
        check("Name cache: past the limit only the programs running are kept", dropped == 298 && cache.Count == 2 && cache.ContainsKey("app1.exe") && cache.ContainsKey("app2.exe"));
    }
}
