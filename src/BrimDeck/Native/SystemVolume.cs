using NAudio.CoreAudioApi;

namespace BrimDeck.Native;

// The default playback device's master volume, held open while a drag adjusts it (MainWindow.MusicGestures.cs). Setting the level
// directly changes it in any amount, unlike the volume keys' fixed steps, and shows no system flyout.
internal sealed class SystemVolume : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDevice _device;
    private SystemVolume(MMDeviceEnumerator enumerator, MMDevice device) { _enumerator = enumerator; _device = device; }

    // Null when there is no playback device or it cannot be read.
    public static SystemVolume? Open()
    {
        MMDeviceEnumerator? enumerator = null;
        MMDevice? device = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var volume = new SystemVolume(enumerator, device);
            _ = volume.Level;
            return volume;
        }
        catch
        {
            try { device?.Dispose(); } catch { }
            try { enumerator?.Dispose(); } catch { }
            return null;
        }
    }

    // 0 to 1. A failed write leaves the level as it was.
    public float Level
    {
        get => _device.AudioEndpointVolume.MasterVolumeLevelScalar;
        set { try { _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f); } catch { } }
    }

    public void Dispose() { try { _device.Dispose(); } catch { } try { _enumerator.Dispose(); } catch { } }
}
