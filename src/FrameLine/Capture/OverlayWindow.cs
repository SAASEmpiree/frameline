using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static FrameLine.Capture.CaptureNative;

namespace FrameLine.Capture;

/// Borderless topmost window pinned to an exact PHYSICAL rectangle.
/// WPF positions windows in DIPs and rescales them on WM_DPICHANGED, which drifts when monitors have
/// different scales; coercing every WM_WINDOWPOSCHANGING to the target rect keeps one window = one
/// display, pixel for pixel, whatever WPF computes. Content is drawn by an <see cref="OverlaySurface"/>.
internal sealed class OverlayWindow : Window
{
    private Int32Rect target;
    private IntPtr hwnd;

    public event Action? DisplaysChanged;
    public OverlaySurface Surface { get; }

    public OverlayWindow(Int32Rect physicalRect, double scale, bool transparent = false, bool excludeFromCapture = false)
    {
        target = physicalRect;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = transparent;
        Background = transparent ? Brushes.Transparent : Brushes.Black;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = true;
        // Rough DIP placement; the hook below sets the real physical bounds.
        Left = physicalRect.X / scale;
        Top = physicalRect.Y / scale;
        Width = Math.Max(1, physicalRect.Width / scale);
        Height = Math.Max(1, physicalRect.Height / scale);

        Surface = new OverlaySurface(physicalRect.Width, physicalRect.Height);
        Content = Surface;

        hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW;   // never in Alt+Tab
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        int on = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref on, sizeof(int));   // no fade: appear/vanish instantly
        int square = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref square, sizeof(int)); // fails harmlessly on Windows 10
        if (excludeFromCapture) SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        ApplyBounds();
    }

    public IntPtr Handle => hwnd;

    public void MoveTo(Int32Rect physicalRect)
    {
        target = physicalRect;
        ApplyBounds();
    }

    private void ApplyBounds() =>
        SetWindowPos(hwnd, HWND_TOPMOST, target.X, target.Y, target.Width, target.Height, SWP_NOACTIVATE);

    private IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_WINDOWPOSCHANGING:
                unsafe
                {
                    var wp = (WINDOWPOS*)lParam;
                    wp->x = target.X;
                    wp->y = target.Y;
                    wp->cx = target.Width;
                    wp->cy = target.Height;
                    wp->flags &= ~(SWP_NOMOVE | SWP_NOSIZE);
                }
                break;
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                // Our own SetWindowPos onto another display also raises WM_DPICHANGED: listeners compare
                // the display layout before reacting.
                Dispatcher.BeginInvoke(new Action(() => DisplaysChanged?.Invoke()),
                    System.Windows.Threading.DispatcherPriority.Background);
                break;
        }
        return IntPtr.Zero;
    }

    /// Brings the window to the foreground so Esc reaches it (the overlays also poll the keyboard).
    public void TakeFocus()
    {
        Activate();
        SetForegroundWindow(hwnd);
        Focus();
        Keyboard.Focus(this);
    }
}

/// Custom-drawn content. Coordinates given to <see cref="Paint"/> are PHYSICAL pixels of the window;
/// the surface maps them to DIPs using the real ratio between its physical and DIP size, so drawing stays
/// 1:1 with the screen pixels even while WPF is between two DPI notifications.
internal sealed class OverlaySurface : FrameworkElement
{
    private readonly int physW, physH;
    public Action<DrawingContext, double>? Paint;   // (dc, scale): draw in DIPs, scale = physical px per DIP

    public OverlaySurface(int physicalWidth, int physicalHeight)
    {
        physW = physicalWidth;
        physH = physicalHeight;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
        SnapsToDevicePixels = true;
    }

    public double Scale => ActualWidth > 0 ? physW / ActualWidth : VisualTreeHelper.GetDpi(this).DpiScaleX;
    public int PhysicalWidth => physW;
    public int PhysicalHeight => physH;

    protected override void OnRender(DrawingContext dc) => Paint?.Invoke(dc, Scale);
}
