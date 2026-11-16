using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameLine.Core;

namespace FrameLine.Shell;

/// NSPasteboard → WPF Clipboard. The clipboard is a shared resource another process can hold open
/// for a few milliseconds (CLIPBRD_E_CANT_OPEN), so every access retries briefly. UI thread only
/// (the clipboard needs an STA thread).
public static class ClipboardImage
{
    private const string PngFormat = "PNG";   // registered format used by browsers and Office (keeps alpha)

    public static bool HasImage()
    {
        return Retry(() =>
            Clipboard.ContainsImage() || Clipboard.ContainsData(PngFormat) || FirstImageFile() != null, false);
    }

    /// The clipboard image (PNG stream first for alpha, then the bitmap, then a copied image file).
    public static BitmapSource? ReadImage()
    {
        return Retry<BitmapSource?>(() =>
        {
            if (Clipboard.ContainsData(PngFormat) && Clipboard.GetData(PngFormat) is MemoryStream png)
            {
                var decoded = Decode(png);
                if (decoded != null) return decoded;
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource img)
            {
                img.Freeze();
                return img;
            }
            var file = FirstImageFile();
            return file != null ? LoadFile(file) : null;
        }, null);
    }

    /// Copies an image as image data (PNG bytes + bitmap) AND as a file, so it pastes into an image
    /// editor, a chat box or File Explorer alike.
    public static bool SetImageAndFile(string path)
    {
        var data = new DataObject();
        try
        {
            if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
                data.SetData(PngFormat, new MemoryStream(File.ReadAllBytes(path)));   // original bytes, perfect fidelity
            var bitmap = LoadFile(path);
            if (bitmap != null)
            {
                data.SetImage(Opaque(bitmap));
                if (!data.GetDataPresent(PngFormat)) data.SetData(PngFormat, EncodePng(bitmap));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (NotSupportedException) { }
        if (File.Exists(path)) data.SetFileDropList(new StringCollection { path });
        return Retry(() => { Clipboard.SetDataObject(data, true); return true; }, false);
    }

    /// Copies an in-memory image (no file yet), as PNG + bitmap.
    public static bool SetImage(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(Opaque(image));
        data.SetData(PngFormat, EncodePng(image));
        return Retry(() => { Clipboard.SetDataObject(data, true); return true; }, false);
    }

    /// Copies a file (videos: "copy" means the file itself, like the macOS app).
    public static bool SetFile(string path) =>
        Retry(() => { Clipboard.SetFileDropList(new StringCollection { path }); return true; }, false);

    public static bool SetText(string text) =>
        Retry(() => { Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true); return true; }, false);

    /// Text holding a secret (the MCP bearer token): kept out of Win+V history, cloud clipboard sync
    /// and clipboard monitors, a channel the macOS pasteboard does not have.
    public static bool SetSecretText(string text)
    {
        var data = new DataObject(DataFormats.UnicodeText, text);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 0 }));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));   // DWORD 0
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));     // DWORD 0
        return Retry(() => { Clipboard.SetDataObject(data, true); return true; }, false);
    }

    /// The image for CF_BITMAP / CF_DIB: most consumers of those formats ignore alpha, so a window
    /// shadow or a transparent margin pasted as solid black. Composited onto white like
    /// CaptureManager does; the "PNG" format keeps the original alpha. Other modules' copy paths
    /// should use this too.
    public static BitmapSource Opaque(BitmapSource image)
    {
        var f = image.Format;
        bool hasAlpha = f == PixelFormats.Bgra32 || f == PixelFormats.Pbgra32 || f == PixelFormats.Rgba64
            || f == PixelFormats.Prgba64 || f == PixelFormats.Rgba128Float || f == PixelFormats.Prgba128Float;
        if (!hasAlpha) return image;
        BitmapSource src = f == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        src.CopyPixels(px, stride, 0);
        bool translucent = false;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 255) { translucent = true; break; }
        if (!translucent) return image;
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a == 255) continue;
            for (int c = 0; c < 3; c++) px[i + c] = (byte)((px[i + c] * a + 255 * (255 - a) + 127) / 255);
            px[i + 3] = 255;
        }
        var result = BitmapSource.Create(w, h, image.DpiX, image.DpiY, PixelFormats.Bgr32, null, px, stride);
        result.Freeze();
        return result;
    }

    private static string? FirstImageFile()
    {
        if (!Clipboard.ContainsFileDropList()) return null;
        return Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(f =>
            CaptureLibrary.ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && File.Exists(f));
    }

    internal static BitmapSource? LoadFile(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;            // never keeps the file locked
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static BitmapSource? Decode(Stream s)
    {
        try
        {
            s.Position = 0;
            var dec = BitmapDecoder.Create(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = dec.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or ArgumentException or IOException)
        {
            return null;
        }
    }

    internal static MemoryStream EncodePng(BitmapSource image)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;
        return ms;
    }

    private static T Retry<T>(Func<T> action, T fallback)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try { return action(); }
            catch (COMException) { Thread.Sleep(30); }
            catch (ExternalException) { Thread.Sleep(30); }
        }
        return fallback;
    }
}
