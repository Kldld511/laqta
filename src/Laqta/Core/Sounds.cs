using System.Media;

namespace Laqta.Core;

/// <summary>
/// Short feedback sounds, synthesised at startup (no audio files to ship).
/// They play from Laqta's own process, so they never land in the game / Discord / browser tracks.
/// </summary>
public static class Sounds
{
    private const int Rate = 44100;

    private static readonly SoundPlayer Shutter = Make(ShutterSamples());
    private static readonly SoundPlayer Start = Make(Tones((660, 0.09), (990, 0.14)));
    private static readonly SoundPlayer Stop = Make(Tones((990, 0.09), (660, 0.14)));
    private static readonly SoundPlayer Saved = Make(Tones((784, 0.07), (1047, 0.07), (1319, 0.14)));

    public static bool Enabled { get; set; } = true;

    public static void PlayShutter() => Play(Shutter);
    public static void PlayStart() => Play(Start);
    public static void PlayStop() => Play(Stop);
    public static void PlaySaved() => Play(Saved);

    private static void Play(SoundPlayer player)
    {
        if (!Enabled) return;
        try { player.Play(); } catch { }
    }

    /// <summary>A sequence of soft sine notes, each with a quick attack and a smooth fade.</summary>
    private static short[] Tones(params (double Freq, double Seconds)[] notes)
    {
        var samples = new List<short>();
        foreach (var (freq, seconds) in notes)
        {
            int n = (int)(seconds * Rate);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                double attack = Math.Min(1, i / (0.004 * Rate));
                double release = Math.Pow(1 - (double)i / n, 1.6);
                double v = Math.Sin(2 * Math.PI * freq * t) * 0.8 + Math.Sin(4 * Math.PI * freq * t) * 0.2;
                samples.Add((short)(v * attack * release * 0.32 * short.MaxValue));
            }
        }
        return samples.ToArray();
    }

    /// <summary>Camera-style "click-clack": two short filtered noise bursts.</summary>
    private static short[] ShutterSamples()
    {
        var rng = new Random(7);
        int total = (int)(0.16 * Rate);
        var samples = new short[total];
        double low = 0;
        void Burst(int start, int length, double gain)
        {
            for (int i = 0; i < length && start + i < total; i++)
            {
                double noise = rng.NextDouble() * 2 - 1;
                low += (noise - low) * 0.45; // gentle low-pass so it clicks rather than hisses
                double env = Math.Exp(-i / (0.012 * Rate));
                samples[start + i] = (short)(low * env * gain * short.MaxValue);
            }
        }
        Burst(0, (int)(0.06 * Rate), 0.55);
        Burst((int)(0.075 * Rate), (int)(0.08 * Rate), 0.7);
        return samples;
    }

    private static SoundPlayer Make(short[] samples)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int dataBytes = samples.Length * 2;
            w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(dataBytes);
            foreach (var s in samples) w.Write(s);
        }
        ms.Position = 0;
        var player = new SoundPlayer(ms);
        try { player.Load(); } catch { }
        return player;
    }
}
