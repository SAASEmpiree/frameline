using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FrameLine.Platform;

public enum Backdrop { None, Mica, Acrylic, Tabbed }

/// The Windows side of GlassUI.swift's "Liquid Glass": DWM title bar colour, rounded corners and
/// the Windows 11 system backdrop (Mica / Acrylic). On Windows 10 every call degrades to the solid
/// brush the window already has, so callers never branch on the OS themselves.
public static class Glass
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;
    /// DWMWA_SYSTEMBACKDROP_TYPE is documented from Windows 11 22H2 (build 22621).
    public static bool SupportsBackdrop => Environment.OSVersion.Version.Build >= 22621;

    /// Dark or light non-client area (caption buttons, title text), Windows 10 2004+.
    public static void SetDarkTitleBar(Window window, bool dark) =>
        WhenHandle(window, hwnd =>
        {
            int v = dark ? 1 : 0;
            Native.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
        });

    /// Caption + border colour (Windows 11 only; ignored by Windows 10).
    public static void SetCaptionColor(Window window, Color caption, Color? border = null) =>
        WhenHandle(window, hwnd =>
        {
            if (!IsWindows11) return;
            int c = ToColorRef(caption);
            Native.DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref c, sizeof(int));
            if (border is Color b)
            {
                int bc = ToColorRef(b);
                Native.DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref bc, sizeof(int));
            }
        });

    /// Rounded corners for borderless popups (Windows 11; Windows 10 keeps square corners).
    public static void RoundCorners(Window window) =>
        WhenHandle(window, hwnd =>
        {
            if (!IsWindows11) return;
            int v = DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, sizeof(int));
        });

    /// Mica / Acrylic behind a framed window. Returns false (and changes nothing) when the OS cannot
    /// do it: the caller's solid Background stays, which is the Windows 10 look.
    public static bool TryApplyBackdrop(Window window, Backdrop kind)
    {
        if (!SupportsBackdrop || kind == Backdrop.None) return false;
        var solid = window.Background;
        window.Background = Brushes.Transparent;
        WhenHandle(window, hwnd =>
        {
            var src = HwndSource.FromHwnd(hwnd);
            if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
            var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            int v = kind switch { Backdrop.Mica => 2, Backdrop.Acrylic => 3, _ => 4 };
            if (Native.DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref v, sizeof(int)) != 0)
                window.Background = solid;               // refused: fall back to the solid look
        });
        return true;
    }

    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    /// Runs now when the HWND exists, otherwise once it is created.
    private static void WhenHandle(Window window, Action<IntPtr> action)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero) { action(hwnd); return; }
        void Handler(object? s, EventArgs e)
        {
            window.SourceInitialized -= Handler;
            action(new WindowInteropHelper(window).Handle);
        }
        window.SourceInitialized += Handler;
    }
}
