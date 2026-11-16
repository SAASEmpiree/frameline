using System.Windows.Media;

namespace FrameLine.Editor;

/// Vector stand-ins for the SF Symbols of the macOS toolbar, drawn in a 16x16 box: an outline
/// part (stroked) and an optional solid part (filled). Frozen, shared by every editor.
internal sealed class EditorIcon
{
    public Geometry? Stroke { get; }
    public Geometry? Fill { get; }

    private EditorIcon(string? stroke, string? fill)
    {
        Stroke = Parse(stroke);
        Fill = Parse(fill);
    }

    private static Geometry? Parse(string? data)
    {
        if (data == null) return null;
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    public static readonly EditorIcon Cursor = new("M4.5,2 L4.5,13.5 L7.3,10.9 L9.2,15 L11,14.2 L9.1,10.2 L12.8,10.2 Z", null);
    public static readonly EditorIcon Rectangle = new("M2.5,4 L13.5,4 L13.5,12 L2.5,12 Z", null);
    public static readonly EditorIcon Arrow = new("M3.5,12.5 L12.5,3.5 M6,3.5 L12.5,3.5 L12.5,10", null);
    public static readonly EditorIcon Pencil = new("M1.5,10 C3,4 5.5,3.5 5.5,7.5 C5.5,11.5 8,12.5 9.5,8 C10.7,4.5 12.5,4 14.5,6.5", null);
    public static readonly EditorIcon Pixelate = new(
        "M2,3 L14,3 L14,13 L2,13 Z",
        "M2,3 h3 v3.33 h-3 Z M8,3 h3 v3.33 h-3 Z M5,6.33 h3 v3.33 h-3 Z M11,6.33 h3 v3.33 h-3 Z M2,9.67 h3 v3.33 h-3 Z M8,9.67 h3 v3.33 h-3 Z");
    public static readonly EditorIcon Crop = new("M4.5,1.5 L4.5,11.5 L14.5,11.5 M1.5,4.5 L11.5,4.5 L11.5,14.5", null);
    public static readonly EditorIcon Sparkles = new(null,
        "M7,2.5 Q7.6,6.9 12,8 Q7.6,9.1 7,13.5 Q6.4,9.1 2,8 Q6.4,6.9 7,2.5 Z M12.5,1 Q12.8,2.7 14.5,3 Q12.8,3.3 12.5,5 Q12.2,3.3 10.5,3 Q12.2,2.7 12.5,1 Z");
    public static readonly EditorIcon Undo = new("M5.5,3.5 L2.5,6.5 L5.5,9.5 M2.5,6.5 L10,6.5 A3.5,3.5 0 0 1 10,13.5 L7,13.5", null);
    public static readonly EditorIcon Redo = new("M10.5,3.5 L13.5,6.5 L10.5,9.5 M13.5,6.5 L6,6.5 A3.5,3.5 0 0 0 6,13.5 L9,13.5", null);
    public static readonly EditorIcon MagicWand = new("M2.5,13.5 L10,6",
        "M11.5,1.5 Q11.8,3.7 14,4 Q11.8,4.3 11.5,6.5 Q11.2,4.3 9,4 Q11.2,3.7 11.5,1.5 Z M5,2 Q5.2,3.3 6.5,3.5 Q5.2,3.7 5,5 Q4.8,3.7 3.5,3.5 Q4.8,3.3 5,2 Z M13,9 Q13.15,10.35 14.5,10.5 Q13.15,10.65 13,12 Q12.85,10.65 11.5,10.5 Q12.85,10.35 13,9 Z");
    public static readonly EditorIcon Ellipsis = new(null,
        "M2.5,8 A1.4,1.4 0 1 1 5.3,8 A1.4,1.4 0 1 1 2.5,8 Z M6.6,8 A1.4,1.4 0 1 1 9.4,8 A1.4,1.4 0 1 1 6.6,8 Z M10.7,8 A1.4,1.4 0 1 1 13.5,8 A1.4,1.4 0 1 1 10.7,8 Z");
    public static readonly EditorIcon CopyDoc = new("M5.5,5 L13.5,5 L13.5,14.5 L5.5,14.5 Z M3,11.5 L2.5,11.5 L2.5,1.5 L10.5,1.5 L10.5,2.5", null);
    public static readonly EditorIcon Checkmark = new("M3,8.5 L6.5,12 L13,4.5", null);
    public static readonly EditorIcon ZoomOut = new("M2,6.75 A4.75,4.75 0 1 1 11.5,6.75 A4.75,4.75 0 1 1 2,6.75 Z M10.2,10.2 L14,14 M4.5,6.75 L9,6.75", null);
    public static readonly EditorIcon ZoomIn = new("M2,6.75 A4.75,4.75 0 1 1 11.5,6.75 A4.75,4.75 0 1 1 2,6.75 Z M10.2,10.2 L14,14 M4.5,6.75 L9,6.75 M6.75,4.5 L6.75,9", null);
    public static readonly EditorIcon TextViewfinder = new("M2,5 L2,2 L5,2 M11,2 L14,2 L14,5 M14,11 L14,14 L11,14 M5,14 L2,14 L2,11 M5,6 L11,6 M8,6 L8,11", null);
    public static readonly EditorIcon SaveDown = new("M8,2 L8,10 M5,7 L8,10 L11,7 M5.5,5 L3,5 L3,14 L13,14 L13,5 L10.5,5", null);
    public static readonly EditorIcon Pin = new("M5.5,2 L10.5,2 M7,2 L7,6 L4.5,9 L11.5,9 L9,6 L9,2 M8,9 L8,14.5", null);
    public static readonly EditorIcon Trash = new("M3,4 L13,4 M6,4 L6,2.5 L10,2.5 L10,4 M4.5,4 L5.2,14 L10.8,14 L11.5,4 M7,6.5 L7,11.5 M9,6.5 L9,11.5", null);
    public static readonly EditorIcon Warning = new("M8,2 L14.5,13.5 L1.5,13.5 Z M8,6.5 L8,9.5", "M7.3,11.2 h1.4 v1.4 h-1.4 Z");
    public static readonly EditorIcon CheckCircle = new(null,
        "M8,1 A7,7 0 1 1 7.99,1 Z");

    /// Draws the icon inside `box` (square), scaled from the 16x16 design grid.
    public void Draw(DrawingContext dc, System.Windows.Rect box, Color color, double strokeWidth = 1.5)
    {
        double s = box.Width / 16.0;
        dc.PushTransform(new TranslateTransform(box.X, box.Y));
        dc.PushTransform(new ScaleTransform(s, s));
        if (Fill != null) dc.DrawGeometry(EditorPalette.Brush(color), null, Fill);
        if (Stroke != null) dc.DrawGeometry(null, EditorPalette.IconPen(color, strokeWidth), Stroke);
        dc.Pop();
        dc.Pop();
    }

    /// The same icon as a WPF element (menu items).
    public System.Windows.Shapes.Path ToShape(Color color, double size = 14)
    {
        return new System.Windows.Shapes.Path
        {
            Data = Stroke ?? Fill,
            Stroke = Stroke != null ? EditorPalette.Brush(color) : null,
            Fill = Stroke == null ? EditorPalette.Brush(color) : null,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 16, Height = 16,
            Stretch = Stretch.None,
            LayoutTransform = new ScaleTransform(size / 16.0, size / 16.0),
        };
    }
}
