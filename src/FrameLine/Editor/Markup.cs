using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace FrameLine.Editor;

/// Annotation tools. The toolbar maps button index -> tool through MarkupEditorView.ToolOrder.
internal enum MarkupTool { Cursor, Rectangle, Arrow, Pencil, Text, Pixelate, Crop }

/// One committed annotation. Geometry is ALWAYS in image pixel coordinates (top-left origin), so it
/// is stable across zoom and identical between the on-screen preview and the flattened file.
/// Instances are never mutated after being committed: undo snapshots share them safely.
internal sealed class Markup
{
    public MarkupTool Tool { get; init; }
    public Color Color { get; init; }
    public double LineWidth { get; init; }          // image pixels
    public Point Start { get; init; }               // text: bottom-left corner of the text line
    public Point End { get; init; }
    public IReadOnlyList<Point> Points { get; init; } = System.Array.Empty<Point>();
    public string Text { get; init; } = "";
    public double FontSize { get; init; }            // image pixels

    public Rect NormalizedRect => new(
        new Point(System.Math.Min(Start.X, End.X), System.Math.Min(Start.Y, End.Y)),
        new Size(System.Math.Abs(End.X - Start.X), System.Math.Abs(End.Y - Start.Y)));
}
