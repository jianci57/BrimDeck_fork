using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck.Native;

// Plays the notification chime and checks custom sound files, one media task at a time on its own short-lived STA
// thread. A dedicated thread per task is not a luxury: a file the media pipeline cannot digest poisons WPF's shared
// media integration for the whole process — every MediaPlayer opened after it never raises an event again (valid →
// garbage → valid poisons, observed on the production probe; deferring or skipping Close does not help). A fresh STA
// thread that opens the file, runs its own dispatcher for the events and exits at once is immune: the garbage file
// fails alone (its MediaFailed fires) and the next valid one plays.
//
// The tasks are bounded: at most one play worker and one probe worker exist, a new task stops the old one instead of
// queueing behind it, and the old one is given a bounded moment to exit before the new one starts — so a worker that
// somehow hangs is reaped where possible and never queues new threads behind itself. Every wait is capped (4 s to
// open, 6 s of playback), Dispose ends what runs, and results are marshaled back to the caller's (UI) dispatcher. A
// replaced task reports at most once and then goes silent, so a picker awaiting a superseded probe still receives an
// answer and never hangs.
internal sealed class NotificationSoundPlayer
{
    private readonly string _folder;
    private readonly Dispatcher _dispatcher;
    private readonly object _lock = new();
    private Worker? _play;
    private Worker? _probe;

    internal NotificationSoundPlayer(string folder, Dispatcher dispatcher) { _folder = folder; _dispatcher = dispatcher; }

    // Plays now; the callers run on the UI thread. failed, when given, receives the reason a play produced no sound
    // (the settings page's preview shows it; background arrivals pass nothing and stay silent). A volume of 0 is
    // silence by choice: whatever played stops first, and nothing new starts.
    internal void Play(string? customPath, int volumePercent, Action<string>? failed = null)
    {
        if (_disposed) return;
        // The previous play is stopped — a volume of zero and a missing file stop it too — and given one bounded
        // moment to leave before the new worker exists. The slot stays held while the old worker leaves, so a
        // concurrent caller (the picker's serial check guards the page, but the API itself makes no promise about
        // its thread) sees the slot busy and starts no second worker behind the first.
        bool refused;
        lock (_lock)
        {
            var previous = _play;
            _play = null;
            previous?.Stop();
            refused = previous is not null && !previous.Join(300);
            if (refused) _play = previous;   // the one that would not leave keeps its slot
        }
        if (refused) return;
        if (volumePercent <= 0) return;
        string file = Source(customPath, failed);
        if (file.Length == 0) return;
        try
        {
            var info = new FileInfo(file);
            int megabytes = (int)(NotificationSound.MaximumCustomBytes / 1024 / 1024);
            if (info.Length > NotificationSound.MaximumCustomBytes)
            {
                failed?.Invoke(Loc.T($"文件超过 {megabytes} MB 上限。", $"The file is larger than the {megabytes} MB limit."));
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed?.Invoke(ex.Message); return; }
        var worker = new Worker(_dispatcher, failed: failed, done: null);
        lock (_lock) _play = worker;
        worker.Start(() =>
        {
            var media = Dispatcher.CurrentDispatcher;
            worker.SetMedia(media);
            if (worker.Cancelled) { media.InvokeShutdown(); return; }
            var player = new MediaPlayer();
            worker.SetPlayer(player);
            player.Volume = Math.Clamp(volumePercent, 0, NotificationSound.MaximumVolume) / 100.0;
            // The deadline timers live on the worker's own dispatcher, so they die with it. The open deadline is 4
            // seconds; the playback one 6 — past it the sound is stopped, whatever the file's own length said.
            DispatcherTimer? playback = null;
            var openTimer = Deadline(media, 4000, () => worker.Report(Loc.T("读取音频信息超时。", "Reading the audio's details timed out.")));
            player.MediaEnded += (_, _) => { openTimer.Stop(); playback?.Stop(); End(media, player); };
            player.MediaFailed += (_, e) => { openTimer.Stop(); playback?.Stop(); worker.Report(Loc.T("无法播放该文件：", "The file cannot be played: ") + e.ErrorException.Message); End(media, player); };
            player.MediaOpened += (_, _) =>
            {
                if (worker.Cancelled) return;
                openTimer.Stop();
                var duration = player.NaturalDuration;
                // The file may have been swapped after it was picked: the duration is judged where the sound
                // starts, and one the pipeline cannot measure is refused like an over-long one.
                if (!duration.HasTimeSpan || duration.TimeSpan <= TimeSpan.Zero || duration.TimeSpan > NotificationSound.MaximumDuration)
                {
                    playback?.Stop();
                    int seconds = (int)NotificationSound.MaximumDuration.TotalSeconds;
                    worker.Report(Loc.T($"音频时长无法读取或超过 {seconds} 秒上限。", $"The audio's duration cannot be read, or is longer than the {seconds} second limit."));
                    End(media, player);
                    return;
                }
                player.Play();
                playback = Deadline(media, 6000, null);
            };
            try { player.Open(new Uri(file)); }
            catch (Exception exception) { openTimer.Stop(); playback?.Stop(); worker.Report(exception.Message); End(media, player); }
        });
    }

    // Checks a custom sound file and reports usable, or why not: the size is checked at once, the duration comes
    // from the media pipeline once the file opens — one the pipeline cannot measure is refused, not passed — and
    // any failure carries its own message. The answer arrives on the UI thread.
    internal void Probe(string path, Action<bool, string?> done)
    {
        if (_disposed) return;
        // The newest probe is the only one: a superseded probe answers once with "not usable" (the picker's own
        // serial check drops it) and then stops, so no caller is left waiting and no threads pile up.
        bool refused;
        lock (_lock)
        {
            var previous = _probe;
            _probe = null;
            previous?.Stop();
            refused = previous is not null && !previous.Join(300);
            if (refused) _probe = previous;
        }
        if (refused)
        {
            // The waiting caller is answered once, on the UI thread as promised; the picker's own serial check
            // drops a superseded answer anyway.
            _dispatcher.BeginInvoke(() => done(false, null));
            return;
        }
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { done(false, Loc.T("文件不存在。", "The file does not exist.")); return; }
            int megabytes = (int)(NotificationSound.MaximumCustomBytes / 1024 / 1024);
            if (info.Length > NotificationSound.MaximumCustomBytes)
            {
                done(false, Loc.T($"文件超过 {megabytes} MB 上限。", $"The file is larger than the {megabytes} MB limit."));
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { done(false, ex.Message); return; }
        var worker = new Worker(_dispatcher, failed: null, done: done);
        lock (_lock) _probe = worker;
        worker.Start(() =>
        {
            var media = Dispatcher.CurrentDispatcher;
            worker.SetMedia(media);
            if (worker.Cancelled) { media.InvokeShutdown(); return; }
            var player = new MediaPlayer();
            worker.SetPlayer(player);
            var openTimer = Deadline(media, 4000, () => worker.Answer(false, Loc.T("读取音频信息超时。", "Reading the audio's details timed out.")));
            player.MediaFailed += (_, e) => { openTimer.Stop(); worker.Answer(false, Loc.T("无法播放该文件：", "The file cannot be played: ") + e.ErrorException.Message); End(media, player); };
            player.MediaOpened += (_, _) =>
            {
                if (worker.Cancelled) return;
                openTimer.Stop();
                var duration = player.NaturalDuration;
                if (!duration.HasTimeSpan || duration.TimeSpan <= TimeSpan.Zero)
                {
                    worker.Answer(false, Loc.T("无法读取音频时长。", "The audio's duration cannot be read."));
                    End(media, player);
                    return;
                }
                int seconds = (int)NotificationSound.MaximumDuration.TotalSeconds;
                if (duration.TimeSpan > NotificationSound.MaximumDuration)
                {
                    worker.Answer(false, Loc.T($"音频时长超过 {seconds} 秒上限。", $"The audio is longer than the {seconds} second limit."));
                    End(media, player);
                    return;
                }
                worker.Answer(true, null);
                End(media, player);
            };
            try { player.Open(new Uri(path)); }
            catch (Exception exception) { openTimer.Stop(); worker.Answer(false, Loc.T("无法打开该文件：", "The file cannot be opened: ") + exception.Message); End(media, player); }
        });
    }

    // A capped timer on the worker's own dispatcher: it fires once and ends the task (reporting, when there is
    // something to report). It is stopped by the handlers when the real event comes first.
    private static DispatcherTimer Deadline(Dispatcher media, int milliseconds, Action? report)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); report?.Invoke(); media.InvokeShutdown(); };
        timer.Start();
        return timer;
    }

    private static void End(Dispatcher media, MediaPlayer player)
    {
        try { player.Close(); } catch { }
        media.InvokeShutdown();
    }

    // Whatever comes next — a new chime, a silence, a replaced probe, the switch going off — the task running now
    // stops first. The stop is posted to the worker's own dispatcher (Normal priority, ahead of the
    // Background-priority shutdown), so the media is stopped and the thread leaves; a task that never came up far
    // enough to have a dispatcher simply loses its voice through the cancelled flag. A worker that will not leave
    // within the bound keeps its slot, so it stays accounted for and the next Play or Probe replaces it instead of
    // a fresh thread piling up behind it.
    internal void Stop()
    {
        StopPlay();
        StopProbe();
    }

    private void StopPlay()
    {
        lock (_lock)
        {
            var worker = _play;
            _play = null;
            if (worker is null) return;
            worker.Stop();
            if (!worker.Join(300)) _play = worker;
        }
    }

    private void StopProbe()
    {
        lock (_lock)
        {
            var worker = _probe;
            _probe = null;
            if (worker is null) return;
            worker.Stop();
            if (!worker.Join(300)) _probe = worker;
        }
    }

    internal void Dispose()
    {
        _disposed = true;
        Stop();
    }

    private bool _disposed;

    // The file to play: a custom one that exists, or the built-in chime written into the data folder. An empty
    // result means there is nothing to play; a custom path that names no file says so when it can.
    private string Source(string? customPath, Action<string>? failed)
    {
        var path = (customPath ?? "").Trim();
        if (path.Length > 0)
        {
            if (File.Exists(path)) return path;
            failed?.Invoke(Loc.T("文件不存在。", "The file does not exist."));
            return "";
        }
        string builtIn = Path.Combine(_folder, "notification-chime.wav");
        try
        {
            Directory.CreateDirectory(_folder);
            byte[] wave = NotificationSound.ChimeWave();
            // Written once and kept; rewritten when it is missing or truncated, so a half-kept file heals itself.
            if (!File.Exists(builtIn) || new FileInfo(builtIn).Length != wave.Length) File.WriteAllBytes(builtIn, wave);
            return builtIn;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed?.Invoke(ex.Message);
            return "";
        }
    }

    // One media task: a short-lived STA thread that owns a MediaPlayer and a dispatcher of its own, reports once
    // through the UI dispatcher, and is stopped by posting a stop and a shutdown to that dispatcher.
    private sealed class Worker(Dispatcher ui, Action<string>? failed, Action<bool, string?>? done)
    {
        private Thread? _thread;
        private Dispatcher? _media;
        private MediaPlayer? _player;
        private int _answered;

        internal volatile bool Cancelled;

        // Setters for the backing fields, so a stop posted from the UI thread reaches exactly what the media
        // thread created and holds.
        internal void SetMedia(Dispatcher media) => _media = media;
        internal void SetPlayer(MediaPlayer player) => _player = player;

        internal void Start(Action body)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    body();
                    // The dispatcher pumps until something shuts it down: the media's events and the deadline
                    // timers are processed here, on this thread. Without the pump they would never be raised.
                    Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    // The task died before any handler could speak: the caller still gets an answer, so the
                    // preview and the picker never wait for an event that will never come.
                    try { Answer(false, ex.Message); } catch { }
                    try { Report(ex.Message); } catch { }
                }
                finally
                {
                    try { _media?.InvokeShutdown(); } catch { }
                    try { _player?.Close(); } catch { }
                }
            })
            { IsBackground = true, Name = "BrimDeck-NotificationSound" };
            thread.SetApartmentState(ApartmentState.STA);
            _thread = thread;
            thread.Start();
        }

        // Called from the UI thread: the cancelled flag takes the task's voice away at once, the stop and the
        // shutdown are posted to its own dispatcher, and the thread is waited for within the bound the caller gave.
        internal void Stop()
        {
            Cancelled = true;
            // A probe that never answered answers once here, so its caller is never left waiting.
            if (done is not null && Interlocked.Exchange(ref _answered, 1) == 0)
                ui.BeginInvoke(() => done(false, null));
            var media = _media;
            if (media is null || media.HasShutdownStarted || media.HasShutdownFinished) return;
            media.BeginInvoke(() =>
            {
                try { _player?.Stop(); } catch { }
                try { _player?.Close(); } catch { }
            });
            media.BeginInvokeShutdown(DispatcherPriority.Background);
        }

        internal bool Join(int milliseconds) => _thread?.Join(milliseconds) ?? true;

        // The play's failure reason, shown only where a caller can show it, always on the UI thread, once.
        internal void Report(string message)
        {
            if (failed is null || Cancelled) return;
            if (Interlocked.Exchange(ref _answered, 1) == 1) return;
            ui.BeginInvoke(() => { if (!Cancelled) failed?.Invoke(message); });
        }

        // The probe's answer, exactly once, always on the UI thread.
        internal void Answer(bool ok, string? error)
        {
            if (done is null || Cancelled) return;
            if (Interlocked.Exchange(ref _answered, 1) == 1) return;
            // The queued callback always runs: a stop that lands after this point turns the answer into a
            // settling "not usable" instead of skipping it, so the caller is never left waiting.
            ui.BeginInvoke(() => done(!Cancelled && ok, Cancelled ? null : error));
        }
    }
}
