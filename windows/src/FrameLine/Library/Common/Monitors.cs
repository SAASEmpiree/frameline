using System;
using static FrameLine.Library.LibNative;

namespace FrameLine.Library;

/// A monitor snapshot in PHYSICAL pixels (virtual-screen coordinates, may be negative) plus its
/// effective DPI scale. All floating library windows are placed through this, never through WPF's
/// Left/Top, which are DIPs of the window's CURRENT monitor and wrong across mixed-DPI setups.
internal readonly record struct MonitorRef(IntPtr Handle, RECT Work, RECT Bounds, double Scale)
{
    public static MonitorRef UnderCursor()
    {
        if (!GetCursorPos(out var p)) return Primary();
        return From(MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST));
    }

    public static MonitorRef Primary() => From(MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY));

    /// Monitor of the foreground window: the closest Windows notion of macOS's NSScreen.main
    /// (the screen with the key window).
    public static MonitorRef OfForegroundWindow()
    {
        var fg = GetForegroundWindow();
        return fg == IntPtr.Zero ? UnderCursor() : From(MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST));
    }

    public static MonitorRef OfWindow(IntPtr hwnd) => From(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));

    /// Re-reads a monitor by handle; a handle that died with a display change falls back to primary.
    public static MonitorRef Refresh(IntPtr handle)
    {
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        return handle != IntPtr.Zero && GetMonitorInfo(handle, ref info) ? Build(handle, info) : Primary();
    }

    public static MonitorRef From(IntPtr handle)
    {
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (handle == IntPtr.Zero || !GetMonitorInfo(handle, ref info))
        {
            // Last resort: a plausible 1080p primary.
            var r = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            return new MonitorRef(IntPtr.Zero, r, r, 1);
        }
        return Build(handle, info);
    }

    private static MonitorRef Build(IntPtr handle, MONITORINFO info)
    {
        double scale = 1;
        try
        {
            if (GetDpiForMonitor(handle, MDT_EFFECTIVE_DPI, out var dx, out _) == 0 && dx > 0) scale = dx / 96.0;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return new MonitorRef(handle, info.rcWork, info.rcMonitor, scale);
    }

    public int Px(double dip) => (int)Math.Round(dip * Scale);
}
