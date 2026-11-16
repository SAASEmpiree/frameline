using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FrameLine.Editor;

/// Document view of the editor: measures exactly the image's PIXEL size (1 unit = 1 image pixel)
/// and is zoomed by a LayoutTransform from outside, so the preview scale equals the flattening
/// scale and mouse positions arrive directly in image pixels.
internal sealed class MarkupCanvas : Canvas
{
    private static readonly Brush TextFieldBackground = MakeFrozen(Color.FromArgb(46, 255, 255, 255));
    /// Horizontal inset of the text inside a borderless, padding-less WPF TextBox.
    private const double TextBoxInsetX = 2;

    private sealed record EditorState(BitmapSource Image, Markup[] Markups);

    private readonly List<Markup> _markups = new();
    private readonly List<EditorState> _undo = new();
    private readonly List<EditorState> _redo = new();
    private Draft? _draft;
    private BitmapSource? _pixelatedCache;

    private TextBox? _activeText;
    private Point _textAnchor;                 // bottom-left of the field, image pixels
    private double _editingTextSize = 24;       // image pixels, fixed when the field opens

    private Color _strokeColor = EditorPalette.DefaultColor;

    public MarkupCanvas(BitmapSource image)
    {
        Image = image;
        Background = Brushes.Transparent;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        ApplySize();
    }

    public BitmapSource Image { get; private set; }
    public int PixelWidth => Image.PixelWidth;
    public int PixelHeight => Image.PixelHeight;
    public MarkupTool Tool { get; set; } = MarkupTool.Cursor;
    public double LineWidth { get; set; } = 6;   // image pixels

    /// Physical screen pixels per image pixel, set by the view; keeps the crop border 1 px wide.
    public double Zoom { get; set; } = 1;

    /// Element that takes keyboard focus once a text field is committed.
    public IInputElement? FocusTarget { get; set; }

    public Color StrokeColor
    {
        get => _strokeColor;
        set
        {
            _strokeColor = value;
            if (_activeText != null) _activeText.Foreground = MarkupRenderer.BrushFor(value);   // recolours text being typed
        }
    }

    public event Action? ImageChanged;
    public event Action? StackChanged;
    public event Action<bool>? TextEditingChanged;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool HasMarkups => _markups.Count > 0;
    public bool IsEditingText => _activeText != null;

    private Rect ImageRect => new(0, 0, PixelWidth, PixelHeight);

    private void ApplySize()
    {
        Width = PixelWidth;
        Height = PixelHeight;
    }

    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, PixelWidth), Math.Clamp(p.Y, 0, PixelHeight));

    // MARK: rendering

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawImage(Image, ImageRect);
        foreach (var m in _markups) MarkupRenderer.Draw(dc, m, PixelatedImage);
        if (_draft is { } d)
        {
            var m = d.ToMarkup();
            if (m.Tool == MarkupTool.Crop) MarkupRenderer.DrawCropMask(dc, m, ImageRect, 1 / Math.Max(0.01, Zoom));
            else MarkupRenderer.Draw(dc, m, PixelatedImage);
        }
    }

    private BitmapSource? PixelatedImage()
    {
        if (_pixelatedCache != null) return _pixelatedCache;
        try { _pixelatedCache = MarkupRenderer.Pixelate(Image); }
        catch (OutOfMemoryException) { return null; }
        return _pixelatedCache;
    }

    // MARK: mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CommitActiveText();                                   // any click elsewhere commits the text being typed
        if (FocusTarget != null && _activeText == null) Keyboard.Focus(FocusTarget);
        var p = e.GetPosition(this);
        switch (Tool)
        {
            case MarkupTool.Cursor:
                return;
            case MarkupTool.Text:
                BeginTextEditing(p);
                e.Handled = true;
                return;
            default:
                var ip = Clamp(p);
                _draft = new Draft(Tool, StrokeColor, LineWidth, ip);
                CaptureMouse();
                InvalidateVisual();
                e.Handled = true;
                return;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_draft == null) return;
        _draft.Extend(Clamp(e.GetPosition(this)));
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_draft == null) return;
        _draft.Extend(Clamp(e.GetPosition(this)));
        FinishDraft();
    }

    /// Capture lost mid-drag (Alt+Tab, another window popping up): keep what was drawn, as a
    /// mouse-up would.
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_draft != null) FinishDraft();
    }

    private void FinishDraft()
    {
        var d = _draft;
        _draft = null;                                        // before ReleaseMouseCapture: it re-enters OnLostMouseCapture
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
        if (d == null) return;
        var m = d.ToMarkup();
        if (m.Tool == MarkupTool.Crop)
        {
            var r = m.NormalizedRect;
            if (r.Width > 8 && r.Height > 8) ApplyCrop(r);   // ignores a micro-drag
            return;
        }
        if (!IsSignificant(m)) return;                        // ignores clicks without a drag
        PushUndo();
        _markups.Add(m);
    }

    private static bool IsSignificant(Markup m) => m.Tool switch
    {
        MarkupTool.Pencil => m.Points.Count > 1,
        MarkupTool.Rectangle or MarkupTool.Arrow or MarkupTool.Pixelate =>
            Math.Sqrt(Math.Pow(m.End.X - m.Start.X, 2) + Math.Pow(m.End.Y - m.Start.Y, 2)) > 3,
        _ => false,
    };

    // MARK: text entry

    private void BeginTextEditing(Point p)
    {
        _editingTextSize = Math.Max(14, Math.Round(PixelHeight * 0.035));   // readable whatever the image size
        _textAnchor = p;
        var tb = new TextBox
        {
            FontFamily = MarkupRenderer.TextTypeface.FontFamily,
            FontWeight = FontWeights.Bold,
            FontSize = Math.Max(1, Math.Round(_editingTextSize)),
            Foreground = MarkupRenderer.BrushFor(StrokeColor),
            CaretBrush = MarkupRenderer.BrushFor(StrokeColor),
            Background = TextFieldBackground,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 30,
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = false,
            AcceptsTab = false,
            FocusVisualStyle = null,
        };
        SetLeft(tb, p.X - TextBoxInsetX);
        SetTop(tb, p.Y - tb.FontSize * 1.33);                // corrected once measured (SizeChanged)
        tb.SizeChanged += (_, _) => SetTop(tb, _textAnchor.Y - tb.ActualHeight);   // grows with typing, bottom-left anchored
        tb.PreviewKeyDown += TextField_PreviewKeyDown;
        tb.LostKeyboardFocus += TextField_LostKeyboardFocus;
        Children.Add(tb);
        _activeText = tb;
        TextEditingChanged?.Invoke(true);
        Dispatcher.BeginInvoke(() => { if (_activeText == tb) Keyboard.Focus(tb); },
                               System.Windows.Threading.DispatcherPriority.Input);
    }

    private void TextField_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return or Key.Tab or Key.Escape)
        {
            CommitActiveText();
            e.Handled = true;                                  // Enter must not reach the Done button
        }
    }

    private void TextField_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Focus moving to another element of the app ends the edit; the window merely losing
        // activation (NewFocus == null) keeps it, as an NSTextField does.
        if (e.NewFocus != null && !ReferenceEquals(e.NewFocus, sender)) CommitActiveText();
    }

    /// Called before a tool change, undo/redo, copy or Done: freezes the typed text.
    public void EndTextEditing() => CommitActiveText();

    private void CommitActiveText()
    {
        var tb = _activeText;
        if (tb == null) return;
        _activeText = null;                                   // before removing: no re-entrance through focus events
        var text = tb.Text;
        var color = tb.Foreground is SolidColorBrush b ? b.Color : StrokeColor;
        tb.PreviewKeyDown -= TextField_PreviewKeyDown;
        tb.LostKeyboardFocus -= TextField_LostKeyboardFocus;
        bool hadFocus = tb.IsKeyboardFocusWithin;
        Children.Remove(tb);
        if (hadFocus && FocusTarget != null) Keyboard.Focus(FocusTarget);
        if (text.Length == 0) { InvalidateVisual(); TextEditingChanged?.Invoke(false); return; }
        PushUndo();
        var origin = Clamp(_textAnchor);
        _markups.Add(new Markup
        {
            Tool = MarkupTool.Text, Color = color, LineWidth = LineWidth,
            Start = origin, End = origin, Text = text, FontSize = _editingTextSize,
        });
        InvalidateVisual();
        TextEditingChanged?.Invoke(false);
    }

    // MARK: crop

    /// Flattens the current state then crops at native resolution; the result replaces the base
    /// image and the annotation list starts empty (they are baked in).
    private void ApplyCrop(Rect r)
    {
        var baked = Flatten();
        if (baked == null) return;
        int x0 = (int)Math.Round(r.Left), y0 = (int)Math.Round(r.Top);
        int x1 = (int)Math.Round(r.Right), y1 = (int)Math.Round(r.Bottom);
        x0 = Math.Clamp(x0, 0, PixelWidth); x1 = Math.Clamp(x1, 0, PixelWidth);
        y0 = Math.Clamp(y0, 0, PixelHeight); y1 = Math.Clamp(y1, 0, PixelHeight);
        if (x1 - x0 <= 0 || y1 - y0 <= 0) return;
        BitmapSource cropped;
        try { cropped = EditorImageIO.Normalize(new CroppedBitmap(baked, new Int32Rect(x0, y0, x1 - x0, y1 - y0))); }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException) { return; }
        PushUndo();
        _markups.Clear();
        SetImage(cropped);
    }

    private void SetImage(BitmapSource img)
    {
        Image = img;
        _pixelatedCache = null;
        ApplySize();
        InvalidateVisual();
        ImageChanged?.Invoke();
    }

    // MARK: undo / redo

    private EditorState Snapshot() => new(Image, _markups.ToArray());

    private void PushUndo()
    {
        _undo.Add(Snapshot());
        _redo.Clear();                                        // a new action invalidates the redo branch
        StackChanged?.Invoke();
    }

    public void Undo()
    {
        CommitActiveText();
        if (_undo.Count == 0) return;
        var s = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(Snapshot());
        Restore(s);
        StackChanged?.Invoke();
    }

    public void Redo()
    {
        CommitActiveText();
        if (_redo.Count == 0) return;
        var s = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(Snapshot());
        Restore(s);
        StackChanged?.Invoke();
    }

    /// Removes every annotation (the base image stays); undoable.
    public void ClearAnnotations()
    {
        CommitActiveText();
        if (_markups.Count == 0) return;
        PushUndo();
        _markups.Clear();
        InvalidateVisual();
    }

    private void Restore(EditorState s)
    {
        _markups.Clear();
        _markups.AddRange(s.Markups);
        if (!ReferenceEquals(s.Image, Image)) SetImage(s.Image);
        else InvalidateVisual();
    }

    // MARK: flattening

    /// Image + annotations at NATIVE pixel resolution, drawn by the same code as the preview.
    public BitmapSource? Flatten()
    {
        int pw = PixelWidth, ph = PixelHeight;
        if (pw <= 0 || ph <= 0) return null;
        try
        {
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(Image, ImageRect);
                foreach (var m in _markups) MarkupRenderer.Draw(dc, m, PixelatedImage);
            }
            var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static Brush MakeFrozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// Annotation being dragged; mutable until committed as an immutable Markup.
    private sealed class Draft
    {
        private readonly MarkupTool _tool;
        private readonly Color _color;
        private readonly double _width;
        private readonly Point _start;
        private Point _end;
        private readonly List<Point> _points;

        public Draft(MarkupTool tool, Color color, double width, Point start)
        {
            _tool = tool; _color = color; _width = width; _start = start; _end = start;
            _points = new List<Point> { start };
        }

        public void Extend(Point p)
        {
            _end = p;
            if (_points[^1] != p) _points.Add(p);
        }

        public Markup ToMarkup() => new()
        {
            Tool = _tool, Color = _color, LineWidth = _width, Start = _start, End = _end,
            Points = _tool == MarkupTool.Pencil ? _points.ToArray() : Array.Empty<Point>(),
        };
    }
}
