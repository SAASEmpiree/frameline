using System;
using System.Windows;
using System.Windows.Interop;

namespace FrameLine.Platform;

/// Port of WindowCentering.swift: put a window on the monitor under the cursor ("our" active
/// screen), slightly above the centre, like Flow and Units.
///
/// Works in PHYSICAL pixels through SetWindowPos: WPF's Left/Top are DIPs of the window's current
/// monitor, which is the wrong scale as soon as the target monitor has another DPI or sits at
/// negative coordinates.
public static class WindowCentering
{
    public static void CenterOnActiveScreen(this Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var monitor = Native.MonitorUnderCursor();
        if (!Native.TryGetMonitorInfo(monitor, out var info))
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }
        // Twice: the first move may cross into a monitor with another DPI, and WPF then resizes the
        // window (WM_DPICHANGED); the second pass centres the final size.
        for (int pass = 0; pass < 2; pass++)
        {
            if (!Native.GetWindowRect(hwnd, out var r)) return;
            uint current = Native.GetDpiForWindow(hwnd);
            uint target = Native.MonitorDpi(monitor);
            double scale = current == 0 ? 1 : (double)target / current;
            int w = (int)Math.Round(r.Width * scale), h = (int)Math.Round(r.Height * scale);
            var work = info.rcWork;
            int x = work.Left + (work.Width - w) / 2;
            int y = work.Top + (work.Height - h) / 2 - (int)Math.Round(work.Height * 0.04);
            // Never above or left of the work area (tall window on a short screen).
            x = Math.Max(work.Left, x);
            y = Math.Max(work.Top, y);
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            if (scale == 1) break;
        }
    }

    /// Shows a window that sizes to its content, centred on the active screen. The size is only
    /// known after the first layout, so the window opens off-screen and is centred on Loaded,
    /// before its first frame is painted.
    public static void ShowCenteredOnActiveScreen(this Window window)
    {
        if (window.IsVisible) { window.CenterOnActiveScreen(); return; }
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        void OnLoaded(object? s, RoutedEventArgs e)
        {
            window.Loaded -= OnLoaded;
            window.CenterOnActiveScreen();
        }
        window.Loaded += OnLoaded;
        window.Show();
    }

    /// Moves a window to a physical-pixel origin (used by floating panels such as the toast).
    internal static void MoveToPhysical(Window window, int x, int y)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }
}
