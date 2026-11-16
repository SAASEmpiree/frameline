using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static FrameLine.Capture.CaptureNative;

namespace FrameLine.Capture;

/// GDI screen grabber (port of ScreenCapturer.swift). Thread-agnostic: every BitmapSource it returns
/// is frozen, so it can be produced on a worker and consumed on the dispatcher.
public static class ScreenCapturer
{
    /// Captures a rectangle in PHYSICAL virtual-screen pixels (multi-monitor, per-monitor DPI).
    public static BitmapSource CaptureRect(Int32Rect physicalRect, bool includeCursor)
    {
        var px = GrabPixels(physicalRect, includeCursor);
        double dpi = 96 * Monitors.AtPoint(physicalRect.X + physicalRect.Width / 2, physicalRect.Y + physicalRect.Height / 2).Scale;
        return ToBitmap(px, physicalRect.Width, physicalRect.Height, PixelFormats.Bgr32, dpi);
    }

    public static BitmapSource CaptureAllScreens(bool includeCursor)
    {
        var monitors = Monitors.All();
        var bounds = Monitors.VirtualBounds(monitors);
        // One blit per display: gaps of a non-rectangular layout stay black instead of being read.
        var buffer = new byte[checked(bounds.Width * bounds.Height * 4)];
        int stride = bounds.Width * 4;
        foreach (var m in monitors)
        {
            var px = GrabPixels(m.Bounds, false);
            int dx = m.Bounds.X - bounds.X, dy = m.Bounds.Y - bounds.Y;
            for (int row = 0; row < m.Bounds.Height; row++)
                Buffer.BlockCopy(px, row * m.Bounds.Width * 4, buffer, (dy + row) * stride + dx * 4, m.Bounds.Width * 4);
        }
        if (includeCursor) DrawCursorInto(buffer, bounds);
        double dpi = 96 * (monitors.FirstOrDefault(m => m.IsPrimary)?.Scale ?? 1);
        return ToBitmap(buffer, bounds.Width, bounds.Height, PixelFormats.Bgr32, dpi);
    }

    /// Snapshot of every display for the selection overlays (no cursor: the overlay shows its own).
    internal static FrozenScreen Freeze()
    {
        var monitors = Monitors.All();
        var shots = monitors.Select(m =>
            ToBitmap(GrabPixels(m.Bounds, false), m.Bounds.Width, m.Bounds.Height, PixelFormats.Bgr32, 96 * m.Scale)).ToList();
        return new FrozenScreen(monitors, shots);
    }

    /// BGRA (alpha undefined) pixels of a physical rect read from the desktop DC.
    internal static byte[] GrabPixels(Int32Rect r, bool includeCursor)
    {
        if (r.Width <= 0 || r.Height <= 0) throw new ArgumentException("Empty capture rectangle.");
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new InvalidOperationException("GetDC failed.");
        IntPtr memDc = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            var bmi = DibHeader(r.Width, r.Height);
            dib = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (memDc == IntPtr.Zero || dib == IntPtr.Zero) throw new InvalidOperationException("Out of GDI resources.");
            old = SelectObject(memDc, dib);
            // CAPTUREBLT: include layered windows (menus, tooltips, translucent apps).
            if (!BitBlt(memDc, 0, 0, r.Width, r.Height, screenDc, r.X, r.Y, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException($"BitBlt failed ({Marshal.GetLastWin32Error()}).");
            if (includeCursor) DrawCursor(memDc, r.X, r.Y);
            GdiFlush();
            var px = new byte[checked(r.Width * r.Height * 4)];
            Marshal.Copy(bits, px, 0, px.Length);
            return px;
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    internal static BITMAPINFOHEADER DibHeader(int w, int h) => new()
    {
        biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
        biWidth = w,
        biHeight = -h,                // top-down rows
        biPlanes = 1,
        biBitCount = 32,
        biCompression = BI_RGB,
    };

    /// Draws the current cursor (with its hotspot) into a DC whose origin is the physical point (originX, originY).
    private static void DrawCursor(IntPtr dc, int originX, int originY)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;
        int hx = 0, hy = 0;
        if (GetIconInfo(ci.hCursor, out var ii))
        {
            hx = ii.xHotspot; hy = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
        DrawIconEx(dc, ci.ptScreenPos.X - hx - originX, ci.ptScreenPos.Y - hy - originY, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
    }

    /// Cursor onto an already-assembled multi-display buffer (round trip through a DIB so GDI can draw it).
    private static void DrawCursorInto(byte[] buffer, Int32Rect bounds)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        var bmi = DibHeader(bounds.Width, bounds.Height);
        IntPtr dib = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        try
        {
            if (memDc == IntPtr.Zero || dib == IntPtr.Zero) return;
            var old = SelectObject(memDc, dib);
            Marshal.Copy(buffer, 0, bits, buffer.Length);
            DrawCursor(memDc, bounds.X, bounds.Y);
            GdiFlush();
            Marshal.Copy(bits, buffer, 0, buffer.Length);
            SelectObject(memDc, old);
        }
        finally
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    internal static BitmapSource ToBitmap(byte[] px, int w, int h, PixelFormat format, double dpi)
    {
        var bmp = BitmapSource.Create(w, h, dpi, dpi, format, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// Encodes PNG or JPEG (quality 0.92 like the macOS app). JPEG has no alpha: transparent pixels
    /// (window shadow, rounded corners) are flattened on white.
    internal static void Encode(BitmapSource image, Stream output, bool jpg)
    {
        BitmapEncoder enc;
        if (jpg)
        {
            enc = new JpegBitmapEncoder { QualityLevel = 92 };
            enc.Frames.Add(BitmapFrame.Create(FlattenIfAlpha(image)));
        }
        else
        {
            enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(image));
        }
        enc.Save(output);
    }

    internal static BitmapSource FlattenIfAlpha(BitmapSource image)
    {
        if (image.Format != PixelFormats.Bgra32) return image;
        int w = image.PixelWidth, h = image.PixelHeight;
        var px = new byte[w * h * 4];
        image.CopyPixels(px, w * 4, 0);
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a == 255) continue;
            for (int c = 0; c < 3; c++) px[i + c] = (byte)((px[i + c] * a + 255 * (255 - a) + 127) / 255);
            px[i + 3] = 255;
        }
        return ToBitmap(px, w, h, PixelFormats.Bgr32, image.DpiX);
    }

    internal static byte[] EncodePng(BitmapSource image)
    {
        using var ms = new MemoryStream();
        Encode(image, ms, false);
        return ms.ToArray();
    }
}

/// The screens as they were when an overlay opened: the overlay shows exactly these pixels and
/// the capture is cut from them, so the saved image equals what the user saw.
internal sealed class FrozenScreen
{
    public IReadOnlyList<MonitorInfo> Monitors { get; }
    public IReadOnlyList<BitmapSource> Shots { get; }
    public Int32Rect Bounds { get; }

    public FrozenScreen(IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<BitmapSource> shots)
    {
        Monitors = monitors;
        Shots = shots;
        Bounds = Capture.Monitors.VirtualBounds(monitors);
    }

    public int IndexAt(int x, int y)
    {
        for (int i = 0; i < Monitors.Count; i++) if (Monitors[i].Contains(x, y)) return i;
        return -1;
    }

    /// sRGB colour of one physical pixel, as 0xRRGGBB; null outside every display.
    public uint? ColorAt(int x, int y)
    {
        int i = IndexAt(x, y);
        if (i < 0) return null;
        var m = Monitors[i].Bounds;
        var px = new byte[4];
        Shots[i].CopyPixels(new Int32Rect(x - m.X, y - m.Y, 1, 1), px, 4, 0);
        return (uint)(px[2] << 16 | px[1] << 8 | px[0]);
    }

    /// Cuts a physical rect out of the frozen displays (spanning several if needed).
    public BitmapSource Crop(Int32Rect r, double dpi)
    {
        var buffer = new byte[checked(r.Width * r.Height * 4)];
        CropInto(r, buffer);
        return ScreenCapturer.ToBitmap(buffer, r.Width, r.Height, PixelFormats.Bgr32, dpi);
    }

    public void CropInto(Int32Rect r, byte[] buffer)
    {
        int stride = r.Width * 4;
        for (int i = 0; i < Monitors.Count; i++)
        {
            var m = Monitors[i].Bounds;
            var part = Capture.Monitors.Intersect(r, m);
            if (part.IsEmpty) continue;
            // Copy through a tight temp buffer: CopyPixels' size check with a non-zero offset is stricter
            // than the rows actually written.
            int partStride = part.Width * 4;
            var tmp = new byte[checked(partStride * part.Height)];
            Shots[i].CopyPixels(new Int32Rect(part.X - m.X, part.Y - m.Y, part.Width, part.Height), tmp, partStride, 0);
            int offset = (part.Y - r.Y) * stride + (part.X - r.X) * 4;
            for (int row = 0; row < part.Height; row++)
                Buffer.BlockCopy(tmp, row * partStride, buffer, offset + row * stride, partStride);
        }
    }
}
