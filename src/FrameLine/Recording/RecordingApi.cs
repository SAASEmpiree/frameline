using System;
using System.Threading.Tasks;

namespace FrameLine.Recording;

// OWNER: agent "recording". Contract stubs; replace bodies, keep public signatures.
public sealed class RecordingManager
{
    public static RecordingManager Shared { get; } = new();
    public bool IsRecording => false;
    public TimeSpan Elapsed => TimeSpan.Zero;
    /// Raised on the UI thread when recording starts/stops (tray icon, menu text).
    public event Action? StateChanged;
    /// Raised on the UI thread with the final .mp4 path (and the GIF is exported after, if enabled).
    public event Action<string>? Finished;
    public void ToggleFullScreen() { }
    public Task<string?> StartAsync() => Task.FromResult<string?>(null);
    public Task<string?> StopAsync() => Task.FromResult<string?>(null);
}

public static class VideoTrimmer
{
    /// Opens the trim editor window for a recording (twin of the image editor).
    public static void Open(string videoPath) { }
}
