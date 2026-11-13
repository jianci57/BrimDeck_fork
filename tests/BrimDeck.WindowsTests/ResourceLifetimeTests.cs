using System.Reflection;
using System.Windows.Threading;
using BrimDeck.Core;

// Isolated checks against the built app, without starting App, warming the sampler, or opening audio devices.
// Main reviewer wiring: ResourceLifetimeTests.Run(Check, absolutePathToBrimDeckDll).
// No existing Program.cs wiring is changed. Run on an STA thread, like the WindowsTests entry point.
internal static class ResourceLifetimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run(Action<string, bool> check, string appAssembly)
    {
        var assembly = Assembly.LoadFrom(appAssembly);
        CheckSampler(check, assembly.GetType("BrimDeck.Native.SystemSampler", throwOnError: true)!);
        CheckMeter(check, assembly.GetType("BrimDeck.Native.AudioLevelMeter", throwOnError: true)!);
    }

    private static object? Get(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static void Call(object instance, string name, params object?[] arguments) =>
        instance.GetType().GetMethod(name, Private | BindingFlags.Public)!.Invoke(instance, arguments);

    private static void CheckSampler(Action<string, bool> check, Type type)
    {
        var sampler = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [Dispatcher.CurrentDispatcher, new Func<(string?, string?)>(() => (null, null))], null)!;
        var wake = (AutoResetEvent)Get(sampler, "_wake")!;
        Call(sampler, "UsePlan", MemoryPlan.For(MemoryMode.Smooth));
        wake.WaitOne(0);
        Call(sampler, "UsePlan", MemoryPlan.For(MemoryMode.Balanced));
        check("resource lifetime: dropping caches wakes an already resting sampler", wake.WaitOne(0));

        using var releaseOld = new ManualResetEventSlim();
        using var releaseCurrent = new ManualResetEventSlim();
        using var enteredOld = new ManualResetEventSlim();
        using var enteredCurrent = new ManualResetEventSlim();
        // Model both generations already inside a reading. The actual Run finally uses this same exit path.
        var old = new Thread(() => { enteredOld.Set(); releaseOld.Wait(); Call(sampler, "ThreadFinished"); }) { IsBackground = true };
        var current = new Thread(() => { enteredCurrent.Set(); releaseCurrent.Wait(); Call(sampler, "ThreadFinished"); }) { IsBackground = true };
        Set(sampler, "_running", 2);
        Set(sampler, "_thread", current);
        old.Start(); current.Start();
        try
        {
            if (!enteredOld.Wait(5000) || !enteredCurrent.Wait(5000)) throw new TimeoutException("fake sampling workers did not start");
            ((IDisposable)sampler).Dispose(); // Actual Join(1000) expires while the fake reading remains blocked.
            check("resource lifetime: Join timeout retains resources used by live generations", !(bool)Get(sampler, "_cleaned")! && !wake.SafeWaitHandle.IsClosed);
            ((IDisposable)sampler).Dispose();
            releaseCurrent.Set();
            if (!current.Join(5000)) throw new TimeoutException("current generation did not end");
            check("resource lifetime: current generation ending leaves the old generation's shared resources alive",
                (int)Get(sampler, "_running")! == 1 && !(bool)Get(sampler, "_cleaned")! && !wake.SafeWaitHandle.IsClosed);
            releaseOld.Set();
            if (!old.Join(5000)) throw new TimeoutException("old generation did not end");
            check("resource lifetime: the last generation performs delayed cleanup", (int)Get(sampler, "_running")! == 0 && (bool)Get(sampler, "_cleaned")! && wake.SafeWaitHandle.IsClosed);
            Call(sampler, "Cleanup");
            ((IDisposable)sampler).Dispose();
            check("resource lifetime: repeated cleanup and Dispose preserve the completed state", (bool)Get(sampler, "_cleaned")!);
        }
        finally
        {
            releaseOld.Set(); releaseCurrent.Set();
            old.Join(5000); current.Join(5000);
        }
    }

    private static void CheckMeter(Action<string, bool> check, Type type)
    {
        var meter = Activator.CreateInstance(type, nonPublic: true)!;
        // A non-null placeholder prevents SetSource from launching its COM worker; only target/timer synchronization runs.
        Set(meter, "_thread", Thread.CurrentThread);
        type.GetProperty("Linger")!.SetValue(meter, TimeSpan.FromHours(1));
        try
        {
            Call(meter, "SetSource", null, false, true);
            Call(meter, "SetSource", null, false, false);
            long oldGeneration = (long)Get(meter, "_releaseGeneration")!;
            Call(meter, "SetSource", null, false, true); // Disposes timer A, whose callback may already be queued.
            Call(meter, "SetSource", null, false, false); // Creates timer B.
            object? currentTimer = Get(meter, "_release");
            long currentGeneration = (long)Get(meter, "_releaseGeneration")!;
            Call(meter, "Release", oldGeneration);
            check("resource lifetime: an old queued release leaves the new timer and requested source intact",
                currentGeneration != oldGeneration && ReferenceEquals(currentTimer, Get(meter, "_release")) && Get(meter, "_requested") is not null);
            Call(meter, "Release", currentGeneration);
            check("resource lifetime: the current release ends its own linger", Get(meter, "_release") is null && Get(meter, "_requested") is null);
        }
        finally
        {
            ((IDisposable)meter).Dispose();
            ((AutoResetEvent)Get(meter, "_wake")!).Dispose();
        }
    }
}
