using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace FrameLine.Capture;

// OWNER: agent "capture". Contract stubs; replace bodies, keep public signatures.
public enum CaptureMode { Region, Window, FullScreen }

public static class ScreenCapturer
{
    /// Captures a rectangle in PHYSICAL virtual-screen pixels (multi-monitor, per-monitor DPI).
    public static BitmapSource CaptureRect(Int32Rect physicalRect, bool includeCursor) => throw new NotImplementedException();
    public static BitmapSource CaptureAllScreens(bool includeCursor) => throw new NotImplementedException();
}

public sealed class CaptureManager
{
    public static CaptureManager Shared { get; } = new();
    /// Runs the interactive capture, saves the file (Preferences format/name), plays the sound,
    /// copies to clipboard if enabled. Callback gets the saved path, or null if cancelled. UI thread.
    public void Capture(CaptureMode mode, Action<string?> done) => throw new NotImplementedException();
    public void CaptureFullDelayed(int seconds, Action<string?> done) => throw new NotImplementedException();
    public Task<string?> CaptureAsync(CaptureMode mode, int delaySeconds = 0) => throw new NotImplementedException();
    public bool IsBusy => false;
}

public static class CaptureSound
{
    public static System.Collections.Generic.IReadOnlyList<string> CaptureSoundIds => Array.Empty<string>();
    public static System.Collections.Generic.IReadOnlyList<string> RecordSoundIds => Array.Empty<string>();
    public static string MenuLabel(string id) => id;
    public static void PlayCapture() { }
    public static Task PlayRecordingStartAsync() => Task.CompletedTask; // completes when the cue ends
    public static void PreviewCapture(string id) { }
    public static void PreviewRecording(string id) { }
}
