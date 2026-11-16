using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
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
                data.SetImage(bitmap);
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
        data.SetImage(image);
        data.SetData(PngFormat, EncodePng(image));
        return Retry(() => { Clipboard.SetDataObject(data, true); return true; }, false);
    }

    /// Copies a file (videos: "copy" means the file itself, like the macOS app).
    public static bool SetFile(string path) =>
        Retry(() => { Clipboard.SetFileDropList(new StringCollection { path }); return true; }, false);

    public static bool SetText(string text) =>
        Retry(() => { Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true); return true; }, false);

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
