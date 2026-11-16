using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

namespace FrameLine.Library;

/// Clipboard writes for the library (port of the NSPasteboard code of PinnedShotWindow and of
/// the quick-preview "Copy" fallback). Images go out twice: as the registered "PNG" format
/// (lossless, what browsers / Discord / Office prefer) and as a classic bitmap.
internal static class LibClipboard
{
    public static bool CopyImageFile(string path)
    {
        BitmapSource? img = LibImages.LoadFull(path);
        if (img == null) return false;
        byte[]? png = null;
        if (string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
        {
            try { png = File.ReadAllBytes(path); } catch { }
        }
        return CopyImage(img, png);
    }

    public static bool CopyImage(BitmapSource img, byte[]? png = null)
    {
        var data = new DataObject();
        data.SetImage(img);
        try { data.SetData("PNG", new MemoryStream(png ?? LibImages.EncodePng(img)), false); } catch { }
        return Set(data);
    }

    /// OpenClipboard fails while another process holds it (clipboard managers, RDP): retry briefly.
    private static bool Set(DataObject data)
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetDataObject(data, true); return true; }
            catch (COMException) { Thread.Sleep(40); }
            catch (ExternalException) { Thread.Sleep(40); }
        }
        return false;
    }
}
