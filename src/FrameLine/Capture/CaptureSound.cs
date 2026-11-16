using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FrameLine.Core;

namespace FrameLine.Capture;

/// App sounds (port of CaptureSound.swift): the capture SHUTTER and the recording-START cue, each chosen
/// in Settings. The two lists are DISJOINT by id and by audio content (hashed at load), so taking a shot
/// never sounds like starting a recording. No sound that belongs to another Windows function (logon,
/// device insert, mail, reminders, recycle bin…). Recording cues are at most 1 s, because recording
/// starts only once the cue has finished.
public static class CaptureSound
{
    private const string Media = @"C:\Windows\Media\";
    private const double MaxRecordingCue = 1.0;

    private sealed record Def(string Id, string Literal, string? File, Func<byte[]>? Synth);

    // "default" = the bundled shutter. Then neutral alert tones only.
    private static readonly Def[] CaptureDefs =
    {
        new("default", "Default", null, null),
        new("ding", "Ding", Media + "Windows Ding.wav", null),
        new("chord", "Chord", Media + "chord.wav", null),
        new("chimes", "Chimes", Media + "chimes.wav", null),
        new("dingClassic", "Classic Ding", Media + "ding.wav", null),
        new("exclamation", "Exclamation", Media + "Windows Exclamation.wav", null),
        new("tada", "Tada", Media + "tada.wav", null),
    };

    // "default" = a short "tink" (macOS default was Tink), synthesised so it exists on every machine.
    private static readonly Def[] RecordDefs =
    {
        new("default", "Default", null, () => SoundSynth.Tink()),
        new("background", "Soft Tone", Media + "Windows Background.wav", null),
        new("foreground", "Bright Tone", Media + "Windows Foreground.wav", null),
        new("blip", "Blip", null, () => SoundSynth.Blip()),
        new("rise", "Rising Chime", null, () => SoundSynth.Rise()),
        new("click", "Click", null, () => SoundSynth.Click()),
    };

    private static readonly object Gate = new();
    private static Dictionary<string, byte[]>? captureSounds, recordSounds;
    private static List<string>? captureIds, recordIds;
    private static SoundPlayer? playing;   // keeps the playing buffer alive until the next sound

    public static IReadOnlyList<string> CaptureSoundIds { get { Load(); return captureIds!; } }
    public static IReadOnlyList<string> RecordSoundIds { get { Load(); return recordIds!; } }

    /// Settings label: "Default", else Loc "snd_<id>", else a literal English name.
    public static string MenuLabel(string id)
    {
        if (id == "default") return Tr("sound_default", "Default");
        var literal = CaptureDefs.Concat(RecordDefs).FirstOrDefault(d => d.Id == id)?.Literal ?? id;
        return Tr("snd_" + id, literal);
    }

    /// Plays the shutter if sounds are enabled. Restarts cleanly on two quick captures.
    public static void PlayCapture()
    {
        if (!Preferences.Shared.PlaySound) return;
        Play(Shutter(Preferences.Shared.CaptureSoundName), wait: false);
    }

    /// Plays the recording-start cue (if sounds are enabled); completes when the cue has ended.
    public static Task PlayRecordingStartAsync()
    {
        if (!Preferences.Shared.PlaySound) return Task.CompletedTask;
        var data = StartCue(Preferences.Shared.RecordSoundName);
        if (data == null) return Task.CompletedTask;
        var duration = TimeSpan.FromSeconds(Math.Min(WavDuration(data), MaxRecordingCue) + 0.3);
        var play = Task.Run(() => Play(data, wait: true));
        // Never hold a recording hostage to the audio device.
        return Task.WhenAny(play, Task.Delay(duration));
    }

    /// Settings preview: plays even when sounds are globally off.
    public static void PreviewCapture(string id) => Play(Shutter(id), wait: false);
    public static void PreviewRecording(string id) => Play(StartCue(id), wait: false);

    // ---- resolution ----

    private static byte[]? Shutter(string id)
    {
        Load();
        return captureSounds!.TryGetValue(id, out var d) ? d : captureSounds.GetValueOrDefault("default")
            ?? captureSounds.Values.FirstOrDefault();
    }

    private static byte[]? StartCue(string id)
    {
        Load();
        return recordSounds!.TryGetValue(id, out var d) ? d : recordSounds["default"];
    }

    private static void Load()
    {
        lock (Gate)
        {
            if (captureSounds != null) return;
            var seen = new HashSet<string>();
            captureSounds = new Dictionary<string, byte[]>();
            recordSounds = new Dictionary<string, byte[]>();
            captureIds = new List<string>();
            recordIds = new List<string>();

            foreach (var d in CaptureDefs)
            {
                var data = d.Id == "default" ? BundledShutter() : Read(d.File);
                if (data == null || !seen.Add(Hash(data))) continue;
                captureSounds[d.Id] = data;
                captureIds.Add(d.Id);
            }
            foreach (var d in RecordDefs)
            {
                var data = d.Synth != null ? d.Synth() : Read(d.File);
                if (data == null || WavDuration(data) > MaxRecordingCue || !seen.Add(Hash(data))) continue;
                recordSounds[d.Id] = data;
                recordIds.Add(d.Id);
            }
        }
    }

    /// Assets\CaptureSound.wav next to the exe (build output keeps the folder, a flat publish may not).
    private static byte[]? BundledShutter()
    {
        var dir = AppContext.BaseDirectory;
        return Read(Path.Combine(dir, "Assets", "CaptureSound.wav")) ?? Read(Path.Combine(dir, "CaptureSound.wav"))
            ?? Read(Media + "Windows Ding.wav");
    }

    private static byte[]? Read(string? path)
    {
        try { return path != null && File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch { return null; }
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA1.HashData(data));

    private static void Play(byte[]? data, bool wait)
    {
        if (data == null) return;
        try
        {
            var player = new SoundPlayer(new MemoryStream(data, writable: false));
            player.Load();
            lock (Gate) playing = player;
            // PlaySound is process-wide: a new sound stops the previous one, like NSSound.stop() + play().
            if (wait) player.PlaySync(); else player.Play();
        }
        catch { /* missing device or unreadable file: stay silent */ }
    }

    /// Duration in seconds from the RIFF header (data size / byte rate); +inf when unparseable.
    internal static double WavDuration(byte[] wav)
    {
        try
        {
            if (wav.Length < 44 || BitConverter.ToUInt32(wav, 0) != 0x46464952) return double.PositiveInfinity; // "RIFF"
            int pos = 12, byteRate = 0;
            while (pos + 8 <= wav.Length)
            {
                uint id = BitConverter.ToUInt32(wav, pos);
                int size = BitConverter.ToInt32(wav, pos + 4);
                if (size < 0) break;
                if (id == 0x20746D66 && pos + 16 <= wav.Length) byteRate = BitConverter.ToInt32(wav, pos + 16); // "fmt "
                if (id == 0x61746164) return byteRate > 0 ? Math.Min(size, wav.Length - pos - 8) / (double)byteRate : double.PositiveInfinity; // "data"
                pos += 8 + size + (size & 1);
            }
        }
        catch { }
        return double.PositiveInfinity;
    }

    internal static string Tr(string key, string fallback)
    {
        var s = Loc.L(key);
        return string.IsNullOrEmpty(s) || s == key ? fallback : s;
    }
}
