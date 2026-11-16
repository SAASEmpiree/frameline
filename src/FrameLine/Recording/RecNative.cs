using System;
using System.Runtime.InteropServices;

namespace FrameLine.Recording;

internal static class RecNative
{
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    /// Physical rect and GDI device name ("\\.\DISPLAY1") of the monitor under the cursor.
    /// The process is per-monitor v2 aware, so both GetCursorPos and rcMonitor are physical pixels.
    public static bool TryGetMonitorUnderCursor(out string deviceName, out RECT bounds, out RECT work)
    {
        deviceName = ""; bounds = default; work = default;
        return GetCursorPos(out var pt) && TryGetMonitorAt(pt, out deviceName, out bounds, out work);
    }

    /// The primary monitor (taskbar with the clock; its origin is always 0,0), the Windows
    /// counterpart of CGMainDisplayID.
    public static bool TryGetPrimaryMonitor(out string deviceName, out RECT bounds, out RECT work) =>
        TryGetMonitorAt(new POINT(), out deviceName, out bounds, out work, MONITOR_DEFAULTTOPRIMARY);

    /// Same, for the monitor containing (or nearest to) a physical virtual-screen point.
    public static bool TryGetMonitorAt(POINT pt, out string deviceName, out RECT bounds, out RECT work,
        uint fallback = MONITOR_DEFAULTTONEAREST)
    {
        deviceName = ""; bounds = default; work = default;
        var mon = MonitorFromPoint(pt, fallback);
        if (mon == IntPtr.Zero) return false;
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfoW(mon, ref info)) return false;
        deviceName = info.szDevice ?? "";
        bounds = info.rcMonitor;
        work = info.rcWork;
        return true;
    }

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    public static void MakeNonActivatingTool(IntPtr hwnd, bool clickThrough)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (clickThrough) ex |= WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }
}
