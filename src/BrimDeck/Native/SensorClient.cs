using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using BrimDeck.Core;

namespace BrimDeck.Native;

// Asks the sensor service (SensorSetup) for the processor's temperature and power, from the sampling thread: the service is
// started when it is not running (interactive users may start it, nothing more) and answers on its pipe. Nothing here needs
// administrator rights. Without the service, or while it starts, the reading is null and the other sources stand in
// (SystemSampler). Whether it is installed is looked at once, and again only after the settings page set it up or removed it
// (SensorSetup.Generation). A service that does not answer is started again after a pause that grows from StartPause to
// LongestPause, so a broken one costs nearly nothing. Only the system and this computer's signed-in users may open the pipe;
// what comes back is only numbers.
internal sealed class SensorClient
{
    private const double FirstAsk = 2, StartPause = 15, LongestPause = 600;
    private int _generation = -1, _failures;
    private bool _installed;
    private double _nextAsk = double.NegativeInfinity;

    public SensorReport? Last { get; private set; }

    public SensorReport? Read(bool wanted, double now)
    {
        if (!wanted) return Last = null;
        if (_generation != SensorSetup.Generation) { _generation = SensorSetup.Generation; _installed = SensorSetup.ServiceInstalled; _failures = 0; _nextAsk = double.NegativeInfinity; }
        if (!_installed || now < _nextAsk) return Last;
        if (Ask(TimeSpan.FromMilliseconds(250)) is { } report) { _failures = 0; return Last = report; }
        // Not answering: started (it stops itself when idle) and asked again shortly; failing again, the pause grows.
        Start();
        _failures++;
        _nextAsk = now + (_failures == 1 ? FirstAsk : Math.Min(LongestPause, StartPause * Math.Pow(2, _failures - 2)));
        return Last = null;
    }

    // One reading now, for the settings page: starts the service and waits a little for it.
    public static SensorReport? ReadOnce()
    {
        if (!SensorSetup.ServiceInstalled) return null;
        if (Ask(TimeSpan.FromMilliseconds(250)) is { } report) return report;
        Start();
        return Ask(TimeSpan.FromSeconds(4));
    }

    private static void Start()
    {
        try
        {
            using var service = new ServiceController(SensorSetup.ServiceName);
            if (service.Status == ServiceControllerStatus.Stopped) service.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { Debug.WriteLine(ex); }
    }

    private static SensorReport? Ask(TimeSpan connect)
    {
        try
        {
            // Identification: whoever answers on the pipe's name may learn who asks, never act as them.
            using var pipe = new NamedPipeClientStream(".", "BrimDeck.Sensors", PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            pipe.Connect(connect);
            pipe.Write("read\n"u8);
            pipe.Flush();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            // A reply is a few hundred bytes: read into one pooled buffer, asked every second, and decoded from it.
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                int length = 0, read;
                while (length < buffer.Length && (read = pipe.ReadAsync(buffer.AsMemory(length), timeout.Token).AsTask().GetAwaiter().GetResult()) > 0) length += read;
                return SensorReport.Parse(Encoding.UTF8.GetString(buffer, 0, length));
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        catch (Exception ex) when (ex is System.TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException) { return null; }
    }
}
