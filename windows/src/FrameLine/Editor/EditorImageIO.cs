using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FrameLine.Editor;

/// Decoding / encoding / clipboard for the editor. Everything here keeps the image at its real
/// pixel size: DPI metadata is ignored, drawing always uses explicit pixel rectangles.
internal static class EditorImageIO
{
    /// Decodes a file fully into memory (the file is not kept open, so saving in place works)
    /// as frozen Pbgra32. Returns null if the file is unreadable or not an image.
    public static BitmapSource? Load(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            BitmapSource frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0) return null;
            return Normalize(frame);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or FileFormatException or ArgumentException or COMException
                                       or InvalidOperationException)
        {
            return null;
        }
    }

    /// Pbgra32 at 96 DPI, frozen (1 pixel = 1 DIP when drawn without an explicit rect).
    public static BitmapSource Normalize(BitmapSource src)
    {
        BitmapSource conv = src.Format == PixelFormats.Pbgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
        var pixels = new byte[(long)stride * h];
        conv.CopyPixels(pixels, stride, 0);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    public static byte[] EncodePng(BitmapSource image)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// Same quality as the capture writer (0.92 on macOS).
    public static byte[] EncodeJpeg(BitmapSource image)
    {
        var enc = new JpegBitmapEncoder { QualityLevel = 92 };
        enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// Replaces `target` atomically: a half-written capture is never visible to the library.
    public static void WriteAtomic(string target, byte[] data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(target)) ?? ".";
        var tmp = Path.Combine(dir, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            File.WriteAllBytes(tmp, data);
            // The target may be held open for a moment (thumbnail reader, antivirus scan).
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, target, overwrite: true); break; }
                catch (IOException) when (attempt < 4) { Thread.Sleep(60); }
                catch (UnauthorizedAccessException) when (attempt < 4) { Thread.Sleep(60); }
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// Puts the image on the clipboard as PNG (keeps alpha, what macOS put) plus a regular bitmap
    /// for apps that only read CF_DIB. Retries: the clipboard is often briefly held by another app.
    public static bool CopyImage(BitmapSource image)
    {
        byte[] png;
        try { png = EncodePng(image); } catch (Exception) { return false; }
        return WithClipboardRetry(() =>
        {
            var data = new DataObject();
            data.SetImage(FrameLine.Shell.ClipboardImage.Opaque(image));
            data.SetData("PNG", new MemoryStream(png), false);
            Clipboard.SetDataObject(data, true);
        });
    }

    public static bool CopyText(string text) => WithClipboardRetry(() => Clipboard.SetText(text));

    private static bool WithClipboardRetry(Action action)
    {
        for (int i = 0; i < 5; i++)
        {
            try { action(); return true; }
            catch (COMException) { Thread.Sleep(40); }
            catch (ExternalException) { Thread.Sleep(40); }
        }
        return false;
    }
}
