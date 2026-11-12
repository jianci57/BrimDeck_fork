namespace BrimDeck.Core;

// The notification chime's limits and its built-in sound. The chime is generated here, sample by sample, so the
// app carries no sampled ringtone and none is copied from anywhere: two short sine tones (E5 then B5) with a
// quick attack and a soft decay, written out as a small WAV file the sound player keeps in the data folder.
// Everything is plain arithmetic, so the bytes are checked in tests without any audio device.
public static class NotificationSound
{
    // A custom sound file may be at most 2 MB and 5 seconds long; larger ones are refused where they are picked.
    public const long MaximumCustomBytes = 2 * 1024 * 1024;
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(5);
    public const int DefaultVolume = 10;
    public const int MaximumVolume = 100;

    private const int Rate = 44100;   // samples per second, 16-bit mono PCM
    private static readonly (double Frequency, double Seconds)[] Notes = [(659.25, 0.16), (987.77, 0.24)];

    // The built-in chime as a WAV file's bytes: about half a second, quiet by itself so the volume percent has room.
    public static byte[] ChimeWave()
    {
        int samples = (int)Math.Ceiling(Notes.Sum(note => note.Seconds * Rate)) + Rate / 10;
        var wave = new byte[44 + samples * 2];
        Text(wave, 0, "RIFF"); Number(wave, 4, 36 + samples * 2); Text(wave, 8, "WAVE");
        Text(wave, 12, "fmt "); Number(wave, 16, 16); wave[20] = 1; wave[22] = 1;   // PCM, one channel
        Number(wave, 24, Rate); Number(wave, 28, Rate * 2); wave[32] = 2; wave[34] = 16;
        Text(wave, 36, "data"); Number(wave, 40, samples * 2);
        int at = 44;
        foreach (var (frequency, seconds) in Notes)
        {
            int length = (int)(seconds * Rate);
            for (int i = 0; i < length; i++)
            {
                double envelope = Math.Min(1.0, i / (Rate * 0.008)) * Math.Exp(-4.0 * i / length);
                double value = Math.Sin(2 * Math.PI * frequency * i / Rate) * 0.5 * envelope;
                var sample = (short)Math.Clamp(value * short.MaxValue, short.MinValue, short.MaxValue);
                wave[at] = (byte)sample;
                wave[at + 1] = (byte)((uint)sample >> 8);
                at += 2;
            }
        }
        return wave;
    }

    private static void Text(byte[] wave, int at, string text)
    {
        for (int i = 0; i < text.Length; i++) wave[at + i] = (byte)text[i];
    }
    private static void Number(byte[] wave, int at, int value)
    {
        wave[at] = (byte)value; wave[at + 1] = (byte)(value >> 8); wave[at + 2] = (byte)(value >> 16); wave[at + 3] = (byte)(value >> 24);
    }
}
