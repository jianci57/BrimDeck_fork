using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using BrimDeck.Core;

namespace BrimDeck.Sensors;

// The service BrimDeck's enhanced sensors page installs (Native.SensorSetup): started on demand by BrimDeck, it answers each
// request on the pipe with one reading (SensorReport) and stops itself once nobody has asked for IdleSeconds. "--console" reads
// twice and prints the readings, for a look by hand from an elevated prompt.
internal static class Program
{
    public const string PipeName = "BrimDeck.Sensors";

    public static int Main(string[] args)
    {
        if (args is ["--console"])
        {
            using var reader = new CpuReader(AppContext.BaseDirectory);
            var clock = Stopwatch.StartNew();
            reader.Read(clock.Elapsed.TotalSeconds);
            Thread.Sleep(1000);
            Console.Write(reader.Read(clock.Elapsed.TotalSeconds).Format());
            return 0;
        }
        ServiceBase.Run(new SensorService());
        return 0;
    }
}

internal sealed class SensorService : ServiceBase
{
    private const double IdleSeconds = 60, RetrySeconds = 30;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _worker;

    public SensorService() { ServiceName = "BrimDeckSensors"; CanStop = true; CanShutdown = true; }

    protected override void OnStart(string[] args)
    {
        _worker = new Thread(Serve) { IsBackground = true, Name = "Sensors" };
        _worker.Start();
    }

    protected override void OnStop() { _stop.Cancel(); _worker?.Join(3000); }
    protected override void OnShutdown() => OnStop();

    // Readings are taken on this one thread (a core's registers need the thread on that core), one per request; the energy
    // counters give their watts over the time between two requests. A driver that was not running yet (the service started with
    // Windows before it) is looked for again every RetrySeconds while BrimDeck keeps asking.
    private void Serve()
    {
        var clock = Stopwatch.StartNew();
        double lastAsked = 0, builtAt = 0;
        var reader = Reader();
        // Only this computer's signed-in users and the system may connect; nobody over the network.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(Program.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 256, 4096, security);
                var waiting = pipe.WaitForConnectionAsync(_stop.Token);
                // Asked nothing for a while: the service stops, and BrimDeck starts it again when it next wants a reading.
                if (!waiting.Wait(TimeSpan.FromSeconds(Math.Max(1, IdleSeconds - (clock.Elapsed.TotalSeconds - lastAsked))))) { if (clock.Elapsed.TotalSeconds - lastAsked >= IdleSeconds) break; continue; }
                var request = new byte[64];
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    timeout.CancelAfter(2000);
                    int read = pipe.ReadAsync(request, timeout.Token).AsTask().GetAwaiter().GetResult();
                    if (read == 0 || !Encoding.ASCII.GetString(request, 0, read).StartsWith("read", StringComparison.Ordinal)) continue;
                }
                lastAsked = clock.Elapsed.TotalSeconds;
                if (reader?.Status is null or SensorStatus.NoDriver && lastAsked - builtAt >= RetrySeconds) { reader?.Dispose(); reader = Reader(); builtAt = lastAsked; }
                var reply = Encoding.UTF8.GetBytes((reader?.Read(lastAsked) ?? new SensorReport(SensorStatus.Failed)).Format());
                pipe.Write(reply);
                pipe.Flush();
                pipe.WaitForPipeDrain();
            }
            catch (OperationCanceledException) { if (_stop.IsCancellationRequested) break; }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException) { if (_stop.IsCancellationRequested) break; }
            catch (IOException) { }
            // Another process holds the pipe's name: tried again, and given up like an idle wait.
            catch (UnauthorizedAccessException) { if (clock.Elapsed.TotalSeconds - lastAsked >= IdleSeconds) break; Thread.Sleep(1000); }
        }
        reader?.Dispose();
        if (!_stop.IsCancellationRequested) ThreadPool.QueueUserWorkItem(_ => Stop());
    }

    private static CpuReader? Reader()
    {
        try { return new CpuReader(AppContext.BaseDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { Debug.WriteLine(ex); return null; }
    }
}
