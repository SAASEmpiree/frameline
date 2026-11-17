using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FrameLine.Platform;

/// Port of GlassConfirmBadge (GlassUI.swift): a small dark toast ("Copied", "Saved"...) at the
/// bottom centre of the monitor under the cursor, gone by itself after ~1.1 s. It never takes the
/// focus (WS_EX_NOACTIVATE), stays above full-screen windows (topmost) and only one exists at a time.
/// Safe to call from any thread.
public static class GlassConfirmBadge
{
    private static Window? _window;
    private static DispatcherTimer? _hideTimer;

    // Cached once: the macOS app crashed for creating fonts per draw (see ARCHITECTURE rules).
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly Brush Fill = FrozenBrush(Color.FromArgb(0xE0, 0x13, 0x13, 0x13));
    private static readonly Brush Stroke = FrozenBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));

    /// SF Symbol names used by the macOS call sites, mapped to Segoe icon glyphs, so ported code can
    /// keep passing the same names.
    private static readonly Dictionary<string, string> Glyphs = new()
    {
        ["checkmark.circle.fill"] = "\uE73E",
        ["exclamationmark.triangle.fill"] = "\uE7BA",
        ["timer"] = "\uE916",
        ["text.viewfinder"] = "\uE946",
        ["trash"] = "\uE74D",
        ["key.fill"] = "\uE8D7",
        ["doc.on.doc"] = "\uE8C8",
        ["pin"] = "\uE718",
        ["record.circle"] = "\uE7C8",
    };

    public static void Show(string text, string symbol = "checkmark.circle.fill")
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (!dispatcher.CheckAccess()) { dispatcher.BeginInvoke(() => Show(text, symbol)); return; }
        Present(text, Glyphs.TryGetValue(symbol, out var g) ? g : "\uE73E");
    }

    /// Same toast with a raw Segoe Fluent / MDL2 glyph, for module code that already speaks glyphs.
    public static void ShowGlyph(string text, string glyph)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (!dispatcher.CheckAccess()) { dispatcher.BeginInvoke(() => ShowGlyph(text, glyph)); return; }
        Present(text, string.IsNullOrEmpty(glyph) ? "\uE73E" : glyph);
    }

    private static void Present(string text, string glyph)
    {
        _hideTimer?.Stop();
        _window?.Close();

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = IconFont, FontSize = 14, Foreground = Brushes.White,
            Width = 16, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = text, FontFamily = TextFont, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        var card = new Border
        {
            Height = 36, CornerRadius = new CornerRadius(11), Background = Fill, BorderBrush = Stroke,
            BorderThickness = new Thickness(1), Padding = new Thickness(13, 0, 14, 0), Child = row,
            Margin = new Thickness(8),   // room for the shadow
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.4 },
        };

        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Focusable = false, IsHitTestVisible = false, SizeToContent = SizeToContent.WidthAndHeight,
            Left = -32000, Top = -32000, Opacity = 0, Content = card,
        };
        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            ex |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        };
        w.Show();
        PlaceBottomCenter(w);
        _window = w;

        w.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromSeconds(0.16)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.1) };
        timer.Tick += (_, _) => { timer.Stop(); Dismiss(w); };
        _hideTimer = timer;
        timer.Start();
    }

    private static void Dismiss(Window w)
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.28)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) =>
        {
            w.Close();
            if (ReferenceEquals(_window, w)) _window = null;
        };
        w.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// Bottom centre of the work area (above the taskbar) of the cursor's monitor, 64 DIP up.
    private static void PlaceBottomCenter(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        var monitor = Native.MonitorUnderCursor();
        if (!Native.TryGetMonitorInfo(monitor, out var info)) return;
        for (int pass = 0; pass < 2; pass++)
        {
            if (!Native.GetWindowRect(hwnd, out var r)) return;
            uint current = Native.GetDpiForWindow(hwnd);
            uint target = Native.MonitorDpi(monitor);
            double scale = current == 0 ? 1 : (double)target / current;
            int width = (int)Math.Round(r.Width * scale), height = (int)Math.Round(r.Height * scale);
            int lift = (int)Math.Round((64 - 8) * target / 96.0);   // 64 DIP minus the shadow margin
            int x = info.rcWork.Left + (info.rcWork.Width - width) / 2;
            int y = info.rcWork.Bottom - lift - height;
            WindowCentering.MoveToPhysical(w, x, y);
            if (scale == 1) break;
        }
    }

    private static Brush FrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
