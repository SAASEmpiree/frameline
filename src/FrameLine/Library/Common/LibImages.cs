using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FrameLine.Library;

/// Image decoding for the library, safe on any thread: every result is frozen, files are opened
/// with full sharing (a capture being written, recycled or renamed is never locked by us) and
/// fully read before the stream closes (BitmapCacheOption.OnLoad).
internal static class LibImages
{
    private const FileShare Share = FileShare.ReadWrite | FileShare.Delete;

    public static bool IsVideoPath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return Array.IndexOf(Core.CaptureLibrary.VideoExtensions, ext) >= 0;
    }

    /// Pixel size read from the header only (no full decode), EXIF orientation applied.
    public static (int W, int H)? ReadPixelSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, Share);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                                           BitmapCacheOption.None);
            var f = dec.Frames[0];
            int w = f.PixelWidth, h = f.PixelHeight;
            return SwapsAxes(Orientation(f)) ? (h, w) : (w, h);
        }
        catch { return null; }
    }

    /// Decoded image whose LONGER side is at most maxDim pixels (never upscaled). With
    /// coverAspect, the decode targets the side that survives a centre cover-crop instead.
    public static BitmapSource? LoadScaled(string path, int maxDim, double? coverAspect = null)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, Share);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                                           BitmapCacheOption.None);
            var f0 = dec.Frames[0];
            int orient = Orientation(f0);
            int w = f0.PixelWidth, h = f0.PixelHeight;
            if (w <= 0 || h <= 0) return null;
            bool swap = SwapsAxes(orient);
            // Work in DISPLAYED axes, then translate back to the stored (decoder) axes.
            int dw = swap ? h : w, dh = swap ? w : h;
            int targetW, targetH;
            if (coverAspect is double a && a > 0)
            {
                if ((double)dw / dh > a) { targetH = Math.Min(dh, (int)Math.Round(maxDim / a)); targetW = 0; }
                else { targetW = Math.Min(dw, maxDim); targetH = 0; }
            }
            else if (dw >= dh) { targetW = Math.Min(dw, maxDim); targetH = 0; }
            else { targetH = Math.Min(dh, maxDim); targetW = 0; }

            fs.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.StreamSource = fs;
            if (swap) { if (targetW > 0) bi.DecodePixelHeight = targetW; else bi.DecodePixelWidth = targetH; }
            else { if (targetW > 0) bi.DecodePixelWidth = targetW; else bi.DecodePixelHeight = targetH; }
            bi.EndInit();
            bi.Freeze();
            return ApplyOrientation(bi, orient);
        }
        catch { return null; }
    }

    /// Full-resolution decode (pinned shot, detail view, drag-out image).
    public static BitmapSource? LoadFull(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, Share);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                                           BitmapCacheOption.None);
            int orient = Orientation(dec.Frames[0]);
            fs.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.StreamSource = fs;
            bi.EndInit();
            bi.Freeze();
            return ApplyOrientation(bi, orient);
        }
        catch { return null; }
    }

    /// First-frame-like thumbnail of a video from the Windows shell thumbnail cache (the same
    /// picture Explorer shows). Null while the file is still being written or has no codec.
    public static BitmapSource? LoadVideoThumbnail(string path, int maxDim)
    {
        try
        {
            var file = Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            using var thumb = file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem,
                                                     (uint)maxDim,
                                                     Windows.Storage.FileProperties.ThumbnailOptions.ResizeThumbnail)
                                  .AsTask().GetAwaiter().GetResult();
            if (thumb == null || thumb.Type != Windows.Storage.FileProperties.ThumbnailType.Image) return null;
            using var s = thumb.AsStreamForRead();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    /// Centre crop to the given width/height ratio, copied into a NEW bitmap so the (larger)
    /// source can be collected. Port of SidebarView.coverCropped: an image already at the card
    /// ratio fills the card with a plain Stretch.Fill, no letterbox possible.
    public static BitmapSource CoverCrop(BitmapSource src, double aspect)
    {
        int W = src.PixelWidth, H = src.PixelHeight;
        if (W <= 0 || H <= 0) return src;
        Int32Rect rect;
        if ((double)W / H > aspect)
        {
            int nw = Math.Max(1, (int)Math.Round(H * aspect));
            rect = new Int32Rect((int)Math.Round((W - nw) / 2.0), 0, nw, H);
        }
        else
        {
            int nh = Math.Max(1, (int)Math.Round(W / aspect));
            rect = new Int32Rect(0, (int)Math.Round((H - nh) / 2.0), W, nh);
        }
        BitmapSource s = src;
        if (s.Format != PixelFormats.Bgra32 && s.Format != PixelFormats.Pbgra32 && s.Format != PixelFormats.Bgr32)
        {
            s = new FormatConvertedBitmap(s, PixelFormats.Pbgra32, null, 0);
            s.Freeze();
        }
        var cropped = new CroppedBitmap(s, rect);
        var copy = new WriteableBitmap(cropped);
        copy.Freeze();
        return copy;
    }

    /// PNG bytes of a bitmap (clipboard "PNG" format, temp files).
    public static byte[] EncodePng(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static int Orientation(BitmapFrame f)
    {
        try
        {
            if (f.Metadata is BitmapMetadata md && md.ContainsQuery("System.Photo.Orientation")
                && md.GetQuery("System.Photo.Orientation") is ushort o)
                return o;
        }
        catch { }
        return 1;
    }

    private static bool SwapsAxes(int orientation) => orientation is >= 5 and <= 8;

    private static BitmapSource ApplyOrientation(BitmapSource src, int orientation)
    {
        if (orientation <= 1 || orientation > 8) return src;
        Transform t = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
            _ => new RotateTransform(270),
        };
        var tb = new TransformedBitmap(src, t);
        tb.Freeze();
        return tb;
    }
}
