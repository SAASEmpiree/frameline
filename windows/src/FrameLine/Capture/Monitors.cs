using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using static FrameLine.Capture.CaptureNative;

namespace FrameLine.Capture;

/// One display, in physical virtual-screen pixels. Scale = effective DPI / 96 (1.0, 1.25, 1.5, 2.0…).
public sealed record MonitorInfo(IntPtr Handle, Int32Rect Bounds, Int32Rect WorkArea, double Scale, bool IsPrimary)
{
    public bool Contains(int x, int y) =>
        x >= Bounds.X && y >= Bounds.Y && x < Bounds.X + Bounds.Width && y < Bounds.Y + Bounds.Height;
}

public static class Monitors
{
    /// All active displays: primary first (index 1 for MCP, like `screencapture -D 1`), then left to right,
    /// top to bottom. Never empty while a display is attached.
    public static IReadOnlyList<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        MonitorEnumProc proc = (h, _, _, _) =>
        {
            var info = Describe(h);
            if (info != null) list.Add(info);
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        return list
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Bounds.X)
            .ThenBy(m => m.Bounds.Y)
            .ToList();
    }

    internal static MonitorInfo? Describe(IntPtr hMonitor)
    {
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
        if (!GetMonitorInfo(hMonitor, ref mi)) return null;
        double scale = 1;
        if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var dx, out _) == 0 && dx > 0) scale = dx / 96.0;
        return new MonitorInfo(hMonitor, ToRect(mi.rcMonitor), ToRect(mi.rcWork), scale,
            (mi.dwFlags & MONITORINFOF_PRIMARY) != 0);
    }

    /// Display under the cursor (nearest one when the cursor is in a gap of an L-shaped layout).
    public static MonitorInfo UnderCursor()
    {
        GetCursorPos(out var p);
        return AtPoint(p.X, p.Y);
    }

    public static MonitorInfo AtPoint(int x, int y)
    {
        var all = All();
        var hit = all.FirstOrDefault(m => m.Contains(x, y));
        if (hit != null) return hit;
        var h = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        return all.FirstOrDefault(m => m.Handle == h) ?? Describe(h) ?? all.First();
    }

    public static MonitorInfo ForWindow(IntPtr hwnd)
    {
        var h = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        return All().FirstOrDefault(m => m.Handle == h) ?? Describe(h) ?? UnderCursor();
    }

    /// Bounding box of every display (the virtual screen).
    public static Int32Rect VirtualBounds(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0) return new Int32Rect(0, 0, 0, 0);
        int l = monitors.Min(m => m.Bounds.X), t = monitors.Min(m => m.Bounds.Y);
        int r = monitors.Max(m => m.Bounds.X + m.Bounds.Width), b = monitors.Max(m => m.Bounds.Y + m.Bounds.Height);
        return new Int32Rect(l, t, r - l, b - t);
    }

    /// A value that changes whenever displays are added, removed, moved, resized or rescaled.
    internal static string LayoutSignature(IReadOnlyList<MonitorInfo> monitors) =>
        string.Join(";", monitors.Select(m => $"{m.Bounds.X},{m.Bounds.Y},{m.Bounds.Width},{m.Bounds.Height}@{m.Scale}"));

    internal static Int32Rect ToRect(RECT r) => new(r.Left, r.Top, Math.Max(0, r.Width), Math.Max(0, r.Height));

    internal static Int32Rect Intersect(Int32Rect a, Int32Rect b)
    {
        int l = Math.Max(a.X, b.X), t = Math.Max(a.Y, b.Y);
        int r = Math.Min(a.X + a.Width, b.X + b.Width), btm = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return r > l && btm > t ? new Int32Rect(l, t, r - l, btm - t) : Int32Rect.Empty;
    }
}
