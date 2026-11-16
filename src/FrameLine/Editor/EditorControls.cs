using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FrameLine.Editor;

/// Base for the self-drawn toolbar buttons (the macOS ones draw themselves too): no template,
/// never takes keyboard focus (focus stays on the canvas / text field), repaints on hover.
internal abstract class DrawnButton : ButtonBase
{
    protected DrawnButton()
    {
        Focusable = false;
        Template = null;
        Background = Brushes.Transparent;
        Cursor = Cursors.Arrow;
        IsEnabledChanged += (_, _) => InvalidateVisual();
    }

    protected bool Hovering => IsMouseOver;

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); InvalidateVisual(); }

    // Hit testing over the whole bounds, not only over painted pixels.
    protected override HitTestResult? HitTestCore(PointHitTestParameters p) =>
        new Rect(RenderSize).Contains(p.HitPoint) ? new PointHitTestResult(this, p.HitPoint) : null;

    protected static FormattedText Text(string s, double size, FontWeight weight, Color color) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, weight, FontStretches.Normal),
            size, EditorPalette.Brush(color), 1.0);
}

/// Toolbar button: rounded white square when the tool is ACTIVE, lighter on hover, greyed when
/// disabled (undo/redo). Icon variant, or text variant for "Aa", "Fit", "100%".
internal sealed class EditorToolButton : DrawnButton
{
    private readonly EditorIcon? _icon;
    private readonly double _iconSize;
    private readonly string? _text;
    private readonly double _fontSize;
    private readonly FontWeight _weight;
    private readonly bool _flexibleWidth;
    private bool _isSelectedTool;
    private FormattedText? _textCache;
    private Color _textCacheColor;

    public EditorToolButton(EditorIcon icon, double iconSize = 15, double side = 26)
    {
        _icon = icon;
        _iconSize = iconSize;
        Width = side;
        Height = side;
    }

    public EditorToolButton(string text, double fontSize = 12, FontWeight? weight = null, double? fixedWidth = 26, double side = 26)
    {
        _text = text;
        _fontSize = fontSize;
        _weight = weight ?? FontWeights.SemiBold;
        _flexibleWidth = fixedWidth == null;
        if (fixedWidth is { } w) Width = w;
        Height = side;
    }

    public bool IsSelectedTool
    {
        get => _isSelectedTool;
        set { _isSelectedTool = value; InvalidateVisual(); }
    }

    private Color Foreground4State =>
        !IsEnabled ? EditorPalette.White(0.30)
        : _isSelectedTool ? Color.FromRgb(0x1A, 0x1A, 0x1A)
        : Hovering ? Colors.White
        : EditorPalette.White(0.95);

    private FormattedText TextFor(Color c)
    {
        if (_textCache == null || _textCacheColor != c)
        {
            _textCache = Text(_text!, _fontSize, _weight, c);
            _textCacheColor = c;
        }
        return _textCache;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (_flexibleWidth && _text != null) return new Size(Math.Ceiling(TextFor(Foreground4State).Width) + 18, Height);
        return base.MeasureOverride(constraint);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        // Selection square is always square (side = min of the dimensions), whatever the width.
        double side = Math.Min(w, h);
        var sq = new Rect((w - side) / 2, (h - side) / 2, side, side);
        if (IsEnabled && _isSelectedTool) dc.DrawRoundedRectangle(EditorPalette.Brush(EditorPalette.White(0.97)), null, sq, 6, 6);
        else if (IsEnabled && Hovering) dc.DrawRoundedRectangle(EditorPalette.Brush(EditorPalette.White(0.13)), null, sq, 6, 6);

        var fg = Foreground4State;
        if (_icon != null)
        {
            var box = new Rect((w - _iconSize) / 2, (h - _iconSize) / 2, _iconSize, _iconSize);
            _icon.Draw(dc, box, fg, 1.5 * 16 / _iconSize);
        }
        else if (_text != null)
        {
            var ft = TextFor(fg);
            dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
        }
    }
}

/// Round swatch of the current colour plus a small chevron; opens the palette.
internal sealed class EditorColorButton : DrawnButton
{
    private Color _color;

    public EditorColorButton(Color color)
    {
        _color = color;
        Width = 26;
        Height = 26;
    }

    public Color Color
    {
        get => _color;
        set { _color = value; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, bounds);
        if (Hovering) dc.DrawRoundedRectangle(EditorPalette.Brush(EditorPalette.White(0.09)), null, bounds, 6, 6);
        const double d = 15;
        // Disc nudged up-left to make room for the chevron.
        var circle = new Rect((bounds.Width - d) / 2 - 1, (bounds.Height - d) / 2 - 1, d, d);
        var centre = new Point(circle.X + d / 2, circle.Y + d / 2);
        dc.DrawEllipse(EditorPalette.Brush(_color), null, centre, d / 2, d / 2);
        dc.DrawEllipse(null, EditorPalette.IconPen(EditorPalette.White(0.25), 1), centre, d / 2 - 0.5, d / 2 - 0.5);
        double cx = circle.Right - 1, cy = circle.Bottom + 1;
        var chev = new StreamGeometry();
        using (var g = chev.Open())
        {
            g.BeginFigure(new Point(cx - 3.2, cy - 2.2), false, false);
            g.PolyLineTo(new[] { new Point(cx, cy + 1.0), new Point(cx + 3.2, cy - 2.2) }, true, true);
        }
        chev.Freeze();
        dc.DrawGeometry(null, EditorPalette.IconPen(EditorPalette.White(0.9), 1.6), chev);
    }
}

/// "Copy" / "Done" pill: icon + label; primary = light fill with dark text.
internal sealed class EditorPillButton : DrawnButton
{
    private const double IconSize = 12, Gap = 5, PadX = 12;
    private readonly EditorIcon _icon;
    private readonly bool _primary;
    private readonly FormattedText _label;

    public EditorPillButton(string title, EditorIcon icon, bool primary)
    {
        _icon = icon;
        _primary = primary;
        _label = Text(title, 12, FontWeights.SemiBold, Fg);
        Height = 27;
        ToolTip = null;
    }

    private Color Fg => _primary ? Colors.Black : EditorPalette.White(0.92);

    protected override Size MeasureOverride(Size constraint) =>
        new(Math.Ceiling(PadX + IconSize + Gap + _label.Width + PadX), 27);

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        var bg = _primary ? EditorPalette.White(Hovering ? 1.0 : 0.95) : EditorPalette.White(Hovering ? 0.16 : 0.10);
        dc.DrawRoundedRectangle(EditorPalette.Brush(bg), null, bounds, 13.5, 13.5);
        double contentW = IconSize + Gap + _label.Width;
        double x = (bounds.Width - contentW) / 2;
        _icon.Draw(dc, new Rect(x, (bounds.Height - IconSize) / 2, IconSize, IconSize), Fg, 2.0);
        dc.DrawText(_label, new Point(x + IconSize + Gap, (bounds.Height - _label.Height) / 2));
    }
}

/// Preset colour disc of the palette, with a selection ring.
internal sealed class RoundSwatchButton : DrawnButton
{
    public Color SwatchColor { get; }
    private readonly bool _selected;

    public RoundSwatchButton(Color color, bool selected)
    {
        SwatchColor = color;
        _selected = selected;
        Width = 26;
        Height = 26;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var b = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, b);
        double inset = _selected ? 3 : 1;
        var c = new Point(b.Width / 2, b.Height / 2);
        double r = b.Width / 2 - inset;
        dc.DrawEllipse(EditorPalette.Brush(SwatchColor), null, c, r, r);
        dc.DrawEllipse(null, EditorPalette.IconPen(Color.FromArgb(64, 0, 0, 0), 1), c, r - 0.5, r - 0.5);
        if (_selected) dc.DrawEllipse(null, EditorPalette.IconPen(Colors.White, 1.5), c, b.Width / 2 - 0.75, b.Height / 2 - 0.75);
    }
}
