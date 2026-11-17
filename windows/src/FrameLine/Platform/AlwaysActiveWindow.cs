using System;
using System.Windows;
using System.Windows.Interop;

namespace FrameLine.Platform;

/// Port of AlwaysActiveWindow.swift: the window keeps its ACTIVE title bar look while a capture
/// overlay (or any other app) has the focus, so the caption does not fade to the dim inactive grey
/// on the dark theme during a capture.
///
/// Windows mechanism: WM_NCACTIVATE with wParam = FALSE is what makes DefWindowProc (and DWM) paint
/// the inactive caption. Answering TRUE without calling DefWindowProc lets the deactivation proceed
/// but skips that repaint. The macOS "double-click the top bar zooms" part is native on Windows.
public static class AlwaysActiveWindow
{
    public static void Attach(Window window)
    {
        void Hook(IntPtr hwnd)
        {
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        }
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) { Hook(handle); return; }
        void OnInit(object? s, EventArgs e)
        {
            window.SourceInitialized -= OnInit;
            Hook(new WindowInteropHelper(window).Handle);
        }
        window.SourceInitialized += OnInit;
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_NCACTIVATE && wParam == IntPtr.Zero)
        {
            handled = true;
            return new IntPtr(1);
        }
        return IntPtr.Zero;
    }
}
