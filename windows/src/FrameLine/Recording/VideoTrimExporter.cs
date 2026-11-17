using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace FrameLine.Recording;

/// Trims [start, end] of a video into a new MP4 through MediaComposition (precise trimming).
/// Unlike AVFoundation's passthrough there is no lossless path here: the excerpt is re-encoded with
/// the source size and frame rate at a bitrate scaled to the pixel count. One export at a time;
/// progress (0..1) is reported on the caller's synchronisation context.
internal sealed class VideoTrimExporter
{
    private CancellationTokenSource? cts;

    public bool IsExporting => cts != null;

    /// Exports to dest (which must not exist). Throws on failure or cancellation; dest is removed then.
    public async Task ExportAsync(string source, TimeSpan start, TimeSpan end, string dest, IProgress<double>? progress)
    {
        if (cts != null) throw new InvalidOperationException("export already running");
        var my = new CancellationTokenSource();
        cts = my;
        bool created = false;
        try
        {
            var srcFile = await StorageFile.GetFileFromPathAsync(source);
            var clip = await MediaClip.CreateFromFileAsync(srcFile);
            var original = clip.OriginalDuration;
            clip.TrimTimeFromStart = Clamp(start, original);
            clip.TrimTimeFromEnd = Clamp(original - end, original);
            var comp = new MediaComposition();
            comp.Clips.Add(clip);

            var profile = comp.CreateDefaultEncodingProfile();
            var vp = clip.GetVideoEncodingProperties();
            if (profile.Video != null && vp != null && vp.Width > 0 && vp.Height > 0)
            {
                // H.264 needs even dimensions.
                profile.Video.Width = vp.Width & ~1u;
                profile.Video.Height = vp.Height & ~1u;
                if (vp.FrameRate.Numerator > 0 && vp.FrameRate.Denominator > 0)
                {
                    profile.Video.FrameRate.Numerator = vp.FrameRate.Numerator;
                    profile.Video.FrameRate.Denominator = vp.FrameRate.Denominator;
                }
                double fps = vp.FrameRate.Denominator > 0 ? (double)vp.FrameRate.Numerator / vp.FrameRate.Denominator : 30;
                fps = Math.Clamp(double.IsFinite(fps) && fps > 0 ? fps : 30, 10, 120);
                double bits = profile.Video.Width * (double)profile.Video.Height * fps * 0.12;
                profile.Video.Bitrate = (uint)Math.Clamp(bits, 2_000_000, 60_000_000);
            }

            var dir = Path.GetDirectoryName(dest)!;
            var folder = await StorageFolder.GetFolderFromPathAsync(dir);
            var outFile = await folder.CreateFileAsync(Path.GetFileName(dest), CreationCollisionOption.FailIfExists);
            created = true;

            var op = comp.RenderToFileAsync(outFile, MediaTrimmingPreference.Precise, profile);
            var reporter = progress == null ? null : new PercentProgress(progress);
            var reason = await op.AsTask(my.Token, reporter);
            if (reason != TranscodeFailureReason.None) throw new IOException($"render failed: {reason}");
        }
        catch
        {
            if (created) TryDelete(dest);
            throw;
        }
        finally
        {
            if (cts == my) cts = null;
            my.Dispose();
        }
    }

    public void Cancel()
    {
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// RenderToFileAsync reports 0..100; the caller's IProgress already marshals to its thread.
    private sealed class PercentProgress : IProgress<double>
    {
        private readonly IProgress<double> inner;
        public PercentProgress(IProgress<double> inner) { this.inner = inner; }
        public void Report(double value) => inner.Report(Math.Clamp(value / 100.0, 0, 1));
    }

    private static TimeSpan Clamp(TimeSpan t, TimeSpan max) => t < TimeSpan.Zero ? TimeSpan.Zero : t > max ? max : t;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
