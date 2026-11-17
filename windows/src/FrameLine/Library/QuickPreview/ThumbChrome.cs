using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace FrameLine.Library;

/// Action icon of the quick-preview capsule (IconChip of CaptureThumbnail.swift): white icon,
/// a translucent disc fades in BEHIND it on hover (red for the trash, which also turns red).
/// Swallows the mouse-down so a click never falls through to "open" on the card.
internal sealed class IconChip : Grid
{
    private readonly Ellipse _circle;
    private readonly FrameworkElement _icon;
    private readonly Brush _hoverTint;
    public event Action? Click;

    public IconChip(string glyph, string tip, Brush circle, Brush hoverTint, double iconSize, double width, double height)
    {
        _hoverTint = hoverTint;
        Width = width;
        Height = height;
        Background = Brushes.Transparent;              // the whole cell is hit-testable
        ToolTip = tip;
        double d = Math.Min(width, height) + 2;
        _circle = new Ellipse { Width = d, Height = d, Fill = circle, Opacity = 0, IsHitTestVisible = false };
        _icon = Glyphs.Icon(glyph, iconSize, Brushes.White);
        Children.Add(_circle);
        Children.Add(_icon);
        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) => SetHover(false);
        MouseLeftButtonDown += (_, e) => e.Handled = true;
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var p = e.GetPosition(this);
            if (p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight) Click?.Invoke();
        };
    }

    private void SetHover(bool on)
    {
        Glyphs.SetBrush(_icon, on ? _hoverTint : Brushes.White);
        _circle.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromSeconds(0.16))
        {
            EasingFunction = CubicBezier.EaseOut,
        });
    }

    public void ResetHover()
    {
        Glyphs.SetBrush(_icon, Brushes.White);
        _circle.BeginAnimation(OpacityProperty, null);
        _circle.Opacity = 0;
    }
}

/// Round dark button whose white rim brightens on hover (IconButton of CaptureThumbnail.swift);
/// the clickable area is exactly the visible disc.
internal sealed class RimButton : Border
{
    private readonly Color _restBg, _hoverBg, _restBorder, _hoverBorder;
    private readonly SolidColorBrush _bg, _border;
    public event Action? Click;

    public RimButton(string glyph, string tip, double size, double iconSize, Color restBg, Color hoverBg,
                     Color restBorder, Color hoverBorder, bool bold = false)
    {
        _restBg = restBg; _hoverBg = hoverBg; _restBorder = restBorder; _hoverBorder = hoverBorder;
        _bg = new SolidColorBrush(restBg);
        _border = new SolidColorBrush(restBorder);
        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(size / 2);
        Background = _bg;
        BorderBrush = _border;
        BorderThickness = new Thickness(1);
        ToolTip = tip;
        Cursor = Cursors.Arrow;
        Child = Glyphs.Icon(glyph, iconSize, Brushes.White, bold);
        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) => SetHover(false);
        MouseLeftButtonDown += (_, e) => e.Handled = true;
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (IsMouseOver) Click?.Invoke();
        };
    }

    private void SetHover(bool on)
    {
        var d = TimeSpan.FromSeconds(0.16);
        _bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(on ? _hoverBg : _restBg, d) { EasingFunction = CubicBezier.EaseOut });
        _border.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(on ? _hoverBorder : _restBorder, d) { EasingFunction = CubicBezier.EaseOut });
    }

    public static Color C(byte r, byte g, byte b, double a) => Color.FromArgb((byte)Math.Round(a * 255), r, g, b);
}
