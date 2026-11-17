using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FrameLine.Core.Mcp;
using FrameLine.Platform;

namespace FrameLine.Shell;

/// Win32 queries the MCP tools need (displays, windows, foreground process). All coordinates are
/// physical virtual-screen pixels (the process is PerMonitorV2 aware). Thread-agnostic.
internal static class McpNative
{
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint MONITORINFOF_PRIMARY = 1;

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
    };

    // MARK: - Displays

    /// Primary first (tool index 1), then the others left to right, top to bottom.
    public static IReadOnlyList<McpRect> Displays()
    {
        var list = new List<(McpRect Rect, bool Primary)>();
        MonitorEnumProc proc = (IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data) =>
        {
            if (Native.TryGetMonitorInfo(hMon, out var info))
            {
                var r = info.rcMonitor;
                list.Add((new McpRect(r.Left, r.Top, r.Width, r.Height), (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        list.Sort((a, b) =>
        {
            if (a.Primary != b.Primary) return a.Primary ? -1 : 1;
            int c = a.Rect.X.CompareTo(b.Rect.X);
            return c != 0 ? c : a.Rect.Y.CompareTo(b.Rect.Y);
        });
        return list.ConvertAll(x => x.Rect);
    }

    public static int? DisplayIndexUnderCursor()
    {
        if (!Native.GetCursorPos(out var p)) return null;
        var displays = Displays();
        for (int i = 0; i < displays.Count; i++)
        {
            var d = displays[i];
            if (p.X >= d.X && p.X < d.Right && p.Y >= d.Y && p.Y < d.Bottom) return i + 1;
        }
        return null;
    }

    // MARK: - Windows

    public static IReadOnlyList<McpWindowInfo> ListWindows()
    {
        var result = new List<McpWindowInfo>();
        var names = new Dictionary<int, (string App, string Process)>();
        EnumWindowsProc proc = (hwnd, _) =>
        {
            var info = Describe(hwnd, names);
            if (info != null) result.Add(info);
            return true;
        };
        EnumWindows(proc, IntPtr.Zero);   // Z order: front to back
        GC.KeepAlive(proc);
        return result;
    }

    private static McpWindowInfo? Describe(IntPtr hwnd, Dictionary<int, (string App, string Process)> names)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return null;
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOOLWINDOW) != 0) return null;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return null;
        var cls = new StringBuilder(256);
        GetClassName(hwnd, cls, cls.Capacity);
        if (ShellClasses.Contains(cls.ToString())) return null;
        if (!TryFrameBounds(hwnd, out var r)) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (!names.TryGetValue((int)pid, out var name))
        {
            name = AppName((int)pid);
            names[(int)pid] = name;
        }
        return new McpWindowInfo(hwnd.ToInt64(), name.App, (int)pid, WindowTitle(hwnd),
            new McpRect(r.Left, r.Top, r.Width, r.Height), name.Process);
    }

    /// The visible frame (DWM extended frame bounds, without the invisible resize borders).
    public static bool TryFrameBounds(IntPtr hwnd, out Native.RECT r)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<Native.RECT>()) == 0
            && r.Width > 0 && r.Height > 0) return true;
        return Native.GetWindowRect(hwnd, out r) && r.Width > 0 && r.Height > 0;
    }

    public static bool IsLiveWindow(IntPtr hwnd) => IsWindow(hwnd) && IsWindowVisible(hwnd) && !IsIconic(hwnd);

    public static int? ForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == 0 ? null : (int)pid;
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// FileDescription of the exe ("Google Chrome"), else the process name; plus the exe name.
    private static (string App, string Process) AppName(int pid)
    {
        string? path = null;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
            }
            finally { CloseHandle(h); }
        }
        string process = path != null ? Path.GetFileNameWithoutExtension(path) : "";
        if (process.Length == 0)
        {
            try { using var p = Process.GetProcessById(pid); process = p.ProcessName; }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        string app = process;
        if (path != null)
        {
            try
            {
                var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) app = desc.Trim();
            }
            catch (FileNotFoundException) { }
        }
        return (app, process);
    }

    // MARK: - P/Invoke

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out Native.RECT pvAttribute, int cbAttribute);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
