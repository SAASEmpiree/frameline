using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FrameLine.Library;

/// Flat icon button of the sidebar bottom bar and the title bar (ToolButton of SidebarView):
/// secondary glyph, rounded hover fill, red hover for destructive ones, dimmed when disabled.
internal sealed class FlatIconButton : Border
{
    private readonly FrameworkElement _icon;
    private readonly bool _destructive;
    private bool _enabled = true;
    public event Action? Click;

    public FlatIconButton(string glyph, string tip, double iconSize = 14, double size = 24, bool destructive = false)
    {
        _destructive = destructive;
        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(6);
        Background = Brushes.Transparent;
        ToolTip = tip;
        _icon = Glyphs.Icon(glyph, iconSize, Brushes.Gray);
        Child = _icon;
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(this, true);
        Restyle(false);
        MouseEnter += (_, _) => Restyle(true);
        MouseLeave += (_, _) => Restyle(false);
        MouseLeftButtonDown += (_, e) => { e.Handled = true; CaptureMouse(); };
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            bool inside = Ui.Inside(this, e);
            ReleaseMouseCapture();
            if (inside && _enabled) Click?.Invoke();
        };
    }

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; Opacity = value ? 1 : 0.35; Restyle(IsMouseOver); }
    }

    public string Tip { set => ToolTip = value; }

    private void Restyle(bool hover)
    {
        bool show = hover && _enabled;
        if (show && _destructive)
        {
            Background = Glyphs.Brush(0xFF, 0x45, 0x3A, 0.16);
            Glyphs.SetBrush(_icon, Glyphs.SystemRed);
        }
        else if (show)
        {
            SetResourceReference(BackgroundProperty, "Lib.ToolHover");
            Glyphs.SetResourceBrush(_icon, "Lib.Text");
        }
        else
        {
            Background = Brushes.Transparent;
            Glyphs.SetResourceBrush(_icon, "Lib.TextSecondary");
        }
    }
}

/// Small helpers for code-built UI bound to the Lib.* resources.
internal static class Ui
{
    public static TextBlock Text(string text, double size, FontWeight weight, string brushKey = "Lib.Text")
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return t;
    }

    public static T Bg<T>(this T b, string key) where T : FrameworkElement
    {
        switch (b)
        {
            case Border br: br.SetResourceReference(Border.BackgroundProperty, key); break;
            case Panel p: p.SetResourceReference(Panel.BackgroundProperty, key); break;
        }
        return b;
    }

    public static Border Stroke(this Border b, string key)
    {
        b.SetResourceReference(Border.BorderBrushProperty, key);
        return b;
    }

    /// Pointer inside the element's bounds (IsMouseOver stays true while the element holds capture).
    public static bool Inside(FrameworkElement el, MouseEventArgs e)
    {
        var p = e.GetPosition(el);
        return p.X >= 0 && p.Y >= 0 && p.X <= el.ActualWidth && p.Y <= el.ActualHeight;
    }

    public static bool IsCtrl => (Keyboard.Modifiers & ModifierKeys.Control) != 0;
    public static bool IsShift => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
}
