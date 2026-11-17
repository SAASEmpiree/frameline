using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FrameLine.Core;
using Windows.Media.Editing;

namespace FrameLine.Recording;

/// GIF companion of a finished recording (Preferences.ExportGif): frames sampled at GifFps, scaled
/// to GifWidth keeping the aspect ratio, written next to the video. Runs entirely off the UI thread.
internal static class GifExporter
{
    /// Returns the GIF path, or null on failure.
    public static Task<string?> ExportAsync(string videoPath, int fps, int maxWidth, CancellationToken ct = default) =>
        Task.Run(() => ExportCoreAsync(videoPath, Math.Clamp(fps, 1, 50), Math.Max(16, maxWidth), ct), ct);

    private static async Task<string?> ExportCoreAsync(string videoPath, int fps, int maxWidth, CancellationToken ct)
    {
        string? tmp = null;
        try
        {
            var (comp, info) = await VideoFrames.OpenAsync(videoPath);
            if (!info.HasVideo || info.Duration <= TimeSpan.Zero) return null;
            var (w, h) = VideoFrames.FitWidth(info, maxWidth);

            double seconds = info.Duration.TotalSeconds;
            int n = Math.Max(1, (int)Math.Floor(seconds * fps + 1e-6));
            var times = new List<TimeSpan>(n);
            for (int i = 0; i < n; i++)
                times.Add(TimeSpan.FromSeconds(Math.Min(i / (double)fps, Math.Max(0, seconds - 0.001))));

            var dir = Path.GetDirectoryName(videoPath) ?? Preferences.Shared.EnsureSaveDirectory();
            // Written under a non-capture extension, then renamed: the library watcher never sees a
            // half-written GIF.
            tmp = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + ".gif.tmp");
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var enc = new GifEncoder(fs, w, h);
                int i = 0;
                await foreach (var frame in VideoFrames.ThumbnailsAsync(comp, times, w, h, VideoFramePrecision.NearestFrame, ct))
                {
                    // Delays rounded on the running total so 15 fps does not drift (7,7,6,7,7,6…).
                    int delay = (int)Math.Round((i + 1) * 100.0 / fps) - (int)Math.Round(i * 100.0 / fps);
                    enc.AddFrame(frame.Pixels, delay);
                    i++;
                }
                enc.Finish();
            }

            var dest = Path.ChangeExtension(videoPath, ".gif");
            if (File.Exists(dest)) dest = Preferences.Shared.NewCapturePath("gif");
            File.Move(tmp, dest);
            tmp = null;
            return dest;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FrameLine GIF export failed: {ex}");
            return null;
        }
        finally
        {
            if (tmp != null) { try { File.Delete(tmp); } catch { /* best effort */ } }
        }
    }
}
