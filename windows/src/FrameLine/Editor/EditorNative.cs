using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FrameLine.Editor;

/// Win32 helpers private to the editor: monitor placement in physical pixels (PerMonitorV2)
/// and the dark title bar.
internal static class EditorNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x20;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// Work area (physical pixels) of the monitor under the mouse cursor.
    public static RECT CursorMonitorWorkArea()
    {
        GetCursorPos(out var pt);
        var mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon != IntPtr.Zero && GetMonitorInfoW(mon, ref info)) return info.rcWork;
        return new RECT { Left = 0, Top = 0, Right = 1280, Bottom = 800 };
    }

    /// Places a not-yet-shown window on the cursor's monitor. `position` receives the work area and
    /// the window's physical size and returns its physical top-left.
    /// First hop: moving the HWND onto the target monitor makes WPF apply that monitor's DPI
    /// (WM_DPICHANGED is sent synchronously), so the size read afterwards is the right one.
    public static void PlaceOnCursorMonitor(Window window, Func<RECT, int, int, POINT> position)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var work = CursorMonitorWorkArea();
        const uint flags = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE;
        SetWindowPos(hwnd, IntPtr.Zero, work.Left, work.Top, 0, 0, flags);
        if (!GetWindowRect(hwnd, out var r)) return;
        var p = position(work, r.Width, r.Height);
        // Never let the title bar leave the work area.
        int x = Math.Max(work.Left, Math.Min(p.X, work.Right - r.Width));
        int y = Math.Max(work.Top, Math.Min(p.Y, work.Bottom - r.Height));
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, flags);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    /// Gives the window a client area of `clientW` x `clientH` DIPs at the DPI of the cursor's
    /// monitor (frame thickness measured, not guessed) and centres it in that monitor's work area.
    public static void PlaceClientOnCursorMonitor(Window window, double clientW, double clientH)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var work = CursorMonitorWorkArea();
        const uint noSize = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE;
        SetWindowPos(hwnd, IntPtr.Zero, work.Left, work.Top, 0, 0, noSize);   // adopt the target monitor's DPI first
        if (!GetWindowRect(hwnd, out var wr) || !GetClientRect(hwnd, out var cr)) return;
        double scale = 1;
        try { var dpi = GetDpiForWindow(hwnd); if (dpi > 0) scale = dpi / 96.0; }
        catch (EntryPointNotFoundException) { }
        int frameW = wr.Width - cr.Width, frameH = wr.Height - cr.Height;
        int w = Math.Min(work.Width, (int)Math.Round(clientW * scale) + frameW);
        int h = Math.Min(work.Height, (int)Math.Round(clientH * scale) + frameH);
        int x = work.Left + (work.Width - w) / 2;
        int y = work.Top + (work.Height - h) / 2;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public static void CenterOnCursorMonitor(Window window) =>
        PlaceOnCursorMonitor(window, (work, w, h) => new POINT
        {
            X = work.Left + (work.Width - w) / 2,
            Y = work.Top + (work.Height - h) / 2,
        });

    /// Dark caption (attribute 20 on Windows 10 20H1+/11, 19 on older builds). Best effort.
    public static void UseDarkTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
    }

    /// Toast-style window: never activates, no taskbar/Alt-Tab entry, clicks fall through.
    public static void MakeNonActivatingOverlay(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT));
    }
}
