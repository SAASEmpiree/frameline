using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;
using WinBitmapTransform = Windows.Graphics.Imaging.BitmapTransform;
using WinPixelFormat = Windows.Graphics.Imaging.BitmapPixelFormat;
using WinAlphaMode = Windows.Graphics.Imaging.BitmapAlphaMode;
using WinInterpolation = Windows.Graphics.Imaging.BitmapInterpolationMode;
using WinExif = Windows.Graphics.Imaging.ExifOrientationMode;
using WinColorMgmt = Windows.Graphics.Imaging.ColorManagementMode;

namespace FrameLine.Recording;

/// What the trimmer and the GIF export need to know about a video file.
internal sealed record VideoInfo(TimeSpan Duration, int Width, int Height, double FrameRate, bool HasVideo)
{
    public double Aspect => Width > 0 && Height > 0 ? (double)Width / Height : 1.6;
}

/// A decoded frame, BGRA 8-bit, top-down, stride = Width * 4.
internal sealed record BgraFrame(byte[] Pixels, int Width, int Height);

/// Frame extraction through Windows.Media.Editing (Media Foundation underneath). Everything here
/// may run off the UI thread; the WinRT objects it creates are agile.
internal static class VideoFrames
{
    public static async Task<(MediaComposition Composition, VideoInfo Info)> OpenAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var clip = await MediaClip.CreateFromFileAsync(file);
        var props = clip.GetVideoEncodingProperties();
        int w = (int)(props?.Width ?? 0), h = (int)(props?.Height ?? 0);
        double fps = 0;
        if (props?.FrameRate is { } fr && fr.Denominator != 0) fps = (double)fr.Numerator / fr.Denominator;
        var comp = new MediaComposition();
        comp.Clips.Add(clip);
        return (comp, new VideoInfo(clip.OriginalDuration, w, h, fps, w > 0 && h > 0));
    }

    /// Frames at the given times, scaled to exactly width x height. Requested in batches so a long
    /// GIF does not hold hundreds of encoded thumbnails in memory at once.
    public static async IAsyncEnumerable<BgraFrame> ThumbnailsAsync(
        MediaComposition comp, IReadOnlyList<TimeSpan> times, int width, int height,
        VideoFramePrecision precision, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        const int Batch = 24;
        for (int start = 0; start < times.Count; start += Batch)
        {
            ct.ThrowIfCancellationRequested();
            var slice = new List<TimeSpan>(Math.Min(Batch, times.Count - start));
            for (int i = start; i < Math.Min(times.Count, start + Batch); i++) slice.Add(times[i]);
            var streams = await comp.GetThumbnailsAsync(slice, width, height, precision).AsTask(ct);
            for (int i = 0; i < streams.Count; i++)
            {
                using var s = streams[i];
                ct.ThrowIfCancellationRequested();
                yield return await DecodeAsync(s, width, height);
            }
        }
    }

    public static async Task<BgraFrame> ThumbnailAsync(MediaComposition comp, TimeSpan at, int width, int height, CancellationToken ct = default)
    {
        using var s = await comp.GetThumbnailAsync(at, width, height, VideoFramePrecision.NearestFrame).AsTask(ct);
        return await DecodeAsync(s, width, height);
    }

    public static async Task<BgraFrame> DecodeAsync(IRandomAccessStream stream, int width, int height)
    {
        var dec = await WinBitmapDecoder.CreateAsync(stream);
        var tf = new WinBitmapTransform();
        int w = (int)dec.PixelWidth, h = (int)dec.PixelHeight;
        if (width > 0 && height > 0 && (w != width || h != height))
        {
            tf.ScaledWidth = (uint)width;
            tf.ScaledHeight = (uint)height;
            tf.InterpolationMode = WinInterpolation.Fant;
            w = width; h = height;
        }
        var data = await dec.GetPixelDataAsync(WinPixelFormat.Bgra8, WinAlphaMode.Ignore, tf,
            WinExif.IgnoreExifOrientation, WinColorMgmt.DoNotColorManage);
        return new BgraFrame(data.DetachPixelData(), w, h);
    }

    /// Frozen WPF bitmap (safe to hand to the UI thread).
    public static BitmapSource ToBitmapSource(BgraFrame f)
    {
        var bmp = BitmapSource.Create(f.Width, f.Height, 96, 96, PixelFormats.Bgr32, null, f.Pixels, f.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    /// Output size for a requested width, keeping the source aspect, never upscaling.
    public static (int W, int H) FitWidth(VideoInfo info, int maxWidth)
    {
        int w = Math.Max(16, Math.Min(maxWidth, info.Width));
        int h = Math.Max(1, (int)Math.Round(w * (double)info.Height / Math.Max(1, info.Width)));
        return (w, h);
    }
}
