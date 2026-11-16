using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using FrameLine.Platform;

namespace FrameLine.Shell;

/// Builds the tray icons from the macOS menu-bar TEMPLATE images (black glyph + alpha). macOS tints
/// a template to the menu bar; Windows does not, so the glyph is recoloured here: white on a dark
/// taskbar, black on a light one, re-run when the taskbar colour changes.
internal sealed class TrayIcons : IDisposable
{
    public Icon Idle { get; }
    public Icon Recording { get; }
    public Icon RecordingDim { get; }
    private readonly IntPtr[] _handles;

    private TrayIcons(Icon idle, Icon rec, Icon dim, IntPtr[] handles)
    {
        Idle = idle; Recording = rec; RecordingDim = dim; _handles = handles;
    }

    public static TrayIcons Create()
    {
        bool lightTaskbar = ThemeManager.TaskbarIsLight;
        var glyph = lightTaskbar ? Color.Black : Color.White;
        // Tray icons follow the SYSTEM DPI (the notification area does not scale per monitor).
        uint dpi = 96;
        try { dpi = Native.GetDpiForSystem(); } catch (EntryPointNotFoundException) { }
        int size = 16;
        try { size = Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, dpi); } catch (EntryPointNotFoundException) { }
        if (size <= 0) size = 16;

        using var idleSrc = Load("framelineTemplate@2x.png");
        using var recSrc = Load("frameline-recording@2x.png");
        var h1 = MakeIcon(idleSrc, glyph, 1.0, size);
        var h2 = MakeIcon(recSrc ?? idleSrc, glyph, 1.0, size);
        var h3 = MakeIcon(recSrc ?? idleSrc, glyph, 0.4, size);
        return new TrayIcons(Icon.FromHandle(h1), Icon.FromHandle(h2), Icon.FromHandle(h3), new[] { h1, h2, h3 });
    }

    private static Bitmap? Load(string name)
    {
        foreach (var candidate in new[] { name, Uri.EscapeDataString(name) })
        {
            try
            {
                var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/" + candidate));
                if (info == null) continue;
                using var s = info.Stream;
                var ms = new MemoryStream();
                s.CopyTo(ms);
                ms.Position = 0;
                return new Bitmap(ms);
            }
            catch (IOException) { }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// Recolours, scales and converts to an HICON the caller must destroy.
    private static IntPtr MakeIcon(Bitmap? src, Color glyph, double alpha, int size)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        if (src != null)
        {
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(src, new Rectangle(0, 0, size, size));
        }
        var rect = new Rectangle(0, 0, size, size);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var px = new byte[data.Stride * size];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = glyph.B; px[i + 1] = glyph.G; px[i + 2] = glyph.R;
                px[i + 3] = (byte)Math.Round(px[i + 3] * alpha);
            }
            Marshal.Copy(px, 0, data.Scan0, px.Length);
        }
        finally { bmp.UnlockBits(data); }
        return bmp.GetHicon();
    }

    public void Dispose()
    {
        Idle.Dispose(); Recording.Dispose(); RecordingDim.Dispose();
        foreach (var h in _handles) if (h != IntPtr.Zero) Native.DestroyIcon(h);
    }
}
