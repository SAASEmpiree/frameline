using System;
using System.IO;
using System.Text;

namespace FrameLine.Capture;

/// Short, deterministic recording-start cues rendered to 16-bit mono WAV in memory. Windows ships no
/// neutral sub-second "tink" outside sounds tied to its own functions, so the default cue is made here.
internal static class SoundSynth
{
    private const int Rate = 44100;

    /// Bright metallic tick, ~0.18 s (stand-in for macOS "Tink").
    public static byte[] Tink() => Render(0.18, t =>
        Env(t, 0.002, 0.045) * (0.7 * Math.Sin(2 * Math.PI * 2093 * t) + 0.3 * Math.Sin(2 * Math.PI * 5274 * t)));

    /// Soft single blip, ~0.12 s.
    public static byte[] Blip() => Render(0.12, t => Env(t, 0.004, 0.03) * Math.Sin(2 * Math.PI * 1320 * t));

    /// Two rising notes, ~0.3 s.
    public static byte[] Rise() => Render(0.30, t =>
    {
        double f = t < 0.12 ? 880 : 1320;
        double local = t < 0.12 ? t : t - 0.12;
        return Env(local, 0.004, 0.05) * Math.Sin(2 * Math.PI * f * local);
    });

    /// Dry mechanical click, ~0.05 s.
    public static byte[] Click() => Render(0.05, t =>
        Env(t, 0.0005, 0.006) * (Math.Sin(2 * Math.PI * 3200 * t) + 0.5 * Math.Sin(2 * Math.PI * 7100 * t)) / 1.5);

    private static double Env(double t, double attack, double decay) =>
        t < attack ? t / attack : Math.Exp(-(t - attack) / decay);

    private static byte[] Render(double seconds, Func<double, double> wave)
    {
        int n = (int)(seconds * Rate);
        using var ms = new MemoryStream(44 + n * 2);
        using var w = new BinaryWriter(ms, Encoding.ASCII);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + n * 2);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)1);          // mono
        w.Write(Rate);
        w.Write(Rate * 2);          // byte rate
        w.Write((short)2);          // block align
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(n * 2);
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            double fadeOut = Math.Min(1, (n - i) / (Rate * 0.004));   // no click at the end
            w.Write((short)Math.Round(Math.Clamp(wave(t) * fadeOut * 0.6, -1, 1) * short.MaxValue));
        }
        w.Flush();
        return ms.ToArray();
    }
}
