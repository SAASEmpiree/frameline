using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static FrameLine.Capture.CaptureNative;

namespace FrameLine.Capture;

/// A top-level window that can be picked / captured. Bounds = DWM extended frame (visible frame,
/// without the invisible resize borders), physical pixels.
public sealed record CaptureWindowInfo(IntPtr Handle, string Title, string App, int ProcessId, Int32Rect Bounds)
{
    /// "Title  —  App", the label shown over the hovered window (parity with the macOS overlay).
    public string Label
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Title)) parts.Add(Title.Trim());
            if (!string.IsNullOrWhiteSpace(App)) parts.Add(App);
            return string.Join("  —  ", parts);
        }
    }
}

public static class WindowCapture
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    /// On-screen application windows, front to back (the first one containing a point is the top one).
    /// Same filter as the macOS overlay: visible, not ours, normal layer (no shell / tool / click-through
    /// windows), at least 40×40 points.
    public static IReadOnlyList<CaptureWindowInfo> List()
    {
        var result = new List<CaptureWindowInfo>();
        int myPid = Environment.ProcessId;
        var appNames = new Dictionary<uint, string>();
        EnumWindowsProc proc = (hwnd, _) =>
        {
            try
            {
                var info = Describe(hwnd, myPid, appNames);
                if (info != null) result.Add(info);
            }
            catch { /* a window vanishing mid-enumeration is not an error */ }
            return true;
        };
        EnumWindows(proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        return result;
    }

    private static CaptureWindowInfo? Describe(IntPtr hwnd, int myPid, Dictionary<uint, string> appNames)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return null;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return null;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOOLWINDOW) != 0 || (ex & WS_EX_TRANSPARENT) != 0) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)myPid) return null;
        var cls = new StringBuilder(256);
        GetClassName(hwnd, cls, cls.Capacity);
        if (ShellClasses.Contains(cls.ToString())) return null;

        var bounds = FrameBounds(hwnd);
        double scale = Monitors.ForWindow(hwnd).Scale;
        if (bounds.Width < 40 * scale || bounds.Height < 40 * scale) return null;

        int len = GetWindowTextLength(hwnd);
        var title = new StringBuilder(Math.Max(len + 1, 1));
        if (len > 0) GetWindowText(hwnd, title, title.Capacity);
        if (!appNames.TryGetValue(pid, out var app)) appNames[pid] = app = AppName(pid);
        return new CaptureWindowInfo(hwnd, title.ToString(), app, (int)pid, bounds);
    }

    /// Visible frame of a window: DWM extended frame bounds, falling back to GetWindowRect.
    internal static Int32Rect FrameBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0 && r.Width > 0)
            return Monitors.ToRect(r);
        GetWindowRect(hwnd, out r);
        return Monitors.ToRect(r);
    }

    private static string AppName(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (!QueryFullProcessImageName(h, 0, sb, ref size)) return "";
            var path = sb.ToString();
            try
            {
                var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
            }
            catch { }
            return Path.GetFileNameWithoutExtension(path);
        }
        finally { CloseHandle(h); }
    }

    /// Captures one window, isolated from what overlaps it (PrintWindow, PW_RENDERFULLCONTENT), like
    /// ScreenCaptureKit's desktopIndependentWindow. Falls back to the frozen screen (or a live blit) for
    /// windows PrintWindow cannot render: hung, DPI-virtualised, or returning a blank surface.
    /// Rounded Windows 11 corners become transparent; `shadow` adds the macOS-like drop shadow.
    /// Must not run on the UI thread (PrintWindow waits for the target's message loop).
    internal static BitmapSource? Capture(IntPtr hwnd, FrozenScreen? frozen, bool shadow)
    {
        if (!IsWindow(hwnd)) return null;
        var frame = FrameBounds(hwnd);
        if (frame.Width <= 0 || frame.Height <= 0) return null;
        var monitor = Monitors.ForWindow(hwnd);
        double scale = monitor.Scale;

        byte[]? px = null;
        // A DPI-unaware window renders at its logical size: PrintWindow would return a shrunken image.
        bool dpiMatches = GetDpiForWindow(hwnd) == (uint)Math.Round(96 * scale);
        if (dpiMatches && Responds(hwnd) && TryClaim(hwnd))
        {
            // The probe can race a window that hangs right after it: keep the 2 s cap, and keep the
            // hwnd claimed until PrintWindow really returns, so retries never stack blocked calls.
            var job = Task.Run(() =>
            {
                try { return PrintWindowPixels(hwnd, frame); }
                finally { Release(hwnd); }
            });
            if (job.Wait(TimeSpan.FromSeconds(2))) px = job.Result;
        }
        if (px == null)
        {
            var screen = frozen?.Bounds ?? Monitors.VirtualBounds(Monitors.All());
            var visible = Monitors.Intersect(frame, screen);
            if (visible.IsEmpty) return null;
            frame = visible;
            if (frozen != null)
            {
                px = new byte[checked(frame.Width * frame.Height * 4)];
                frozen.CropInto(frame, px);
            }
            else
            {
                try { px = ScreenCapturer.GrabPixels(frame, false); } catch { return null; }
            }
        }

        int w = frame.Width, h = frame.Height;
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        bool alpha = false;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && !IsZoomed(hwnd)
            && (GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & WS_CAPTION) == WS_CAPTION)
        {
            WindowShadow.RoundCorners(px, w, h, 8 * scale);
            alpha = true;
        }
        if (shadow)
        {
            px = WindowShadow.Apply(px, ref w, ref h, scale);
            alpha = true;
        }
        return ScreenCapturer.ToBitmap(px, w, h, alpha ? PixelFormats.Bgra32 : PixelFormats.Bgr32, 96 * scale);
    }

    private static readonly HashSet<IntPtr> printing = new();

    private static bool TryClaim(IntPtr hwnd) { lock (printing) return printing.Add(hwnd); }

    private static void Release(IntPtr hwnd) { lock (printing) printing.Remove(hwnd); }

    /// PrintWindow waits on the target's message loop: a hung window would pin a pool thread and a
    /// window-size DIB until it recovers. Ask first, with a short timeout that aborts when it is hung.
    private static bool Responds(IntPtr hwnd)
    {
        if (IsHungAppWindow(hwnd)) return false;
        return SendMessageTimeout(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero,
            SMTO_ABORTIFHUNG | SMTO_BLOCK, 500, out _) != IntPtr.Zero;
    }

    /// PrintWindow into a DIB the size of the window rect, cropped to the visible frame. Null when it
    /// fails or yields an all-black surface (some GPU / protected content).
    private static byte[]? PrintWindowPixels(IntPtr hwnd, Int32Rect frame)
    {
        if (!GetWindowRect(hwnd, out var wr) || wr.Width <= 0 || wr.Height <= 0) return null;
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        var bmi = ScreenCapturer.DibHeader(wr.Width, wr.Height);
        IntPtr dib = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        IntPtr old = IntPtr.Zero;
        try
        {
            if (memDc == IntPtr.Zero || dib == IntPtr.Zero) return null;
            old = SelectObject(memDc, dib);
            if (!PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT)) return null;
            GdiFlush();

            int ox = frame.X - wr.Left, oy = frame.Y - wr.Top;
            int w = Math.Min(frame.Width, wr.Width - ox), h = Math.Min(frame.Height, wr.Height - oy);
            if (ox < 0 || oy < 0 || w <= 0 || h <= 0 || w != frame.Width || h != frame.Height) return null;

            var px = new byte[checked(w * h * 4)];
            int srcStride = wr.Width * 4;
            for (int row = 0; row < h; row++)
                Marshal.Copy(bits + (oy + row) * srcStride + ox * 4, px, row * w * 4, w * 4);

            for (int i = 0; i < px.Length; i += 4)
                if (px[i] != 0 || px[i + 1] != 0 || px[i + 2] != 0) return px;
            return null;
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
