using System.Collections.Generic;
using System.Windows.Media;

namespace FrameLine.Editor;

/// Colours of the editor chrome (BridgeShot charcoal palette) and the annotation presets.
/// Brushes and pens are frozen and cached: controls repaint on every hover change.
internal static class EditorPalette
{
    public static readonly Color Ink = Color.FromRgb(0x13, 0x13, 0x13);           // canvas surround #131313
    public static readonly Color Capsule = Color.FromArgb(247, 41, 41, 41);       // white 0.16, alpha 0.97

    /// macOS system colours (dark appearance, the app's default theme): red, orange, yellow,
    /// green, blue, purple, pink, white, black.
    public static readonly Color[] Presets =
    {
        Color.FromRgb(0xFF, 0x45, 0x3A), Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0xFF, 0xD6, 0x0A),
        Color.FromRgb(0x32, 0xD7, 0x4B), Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0xBF, 0x5A, 0xF2),
        Color.FromRgb(0xFF, 0x37, 0x5F), Colors.White, Colors.Black,
    };

    public static Color DefaultColor => Presets[0];

    public static Color White(double alpha) => Color.FromArgb((byte)System.Math.Round(alpha * 255), 255, 255, 255);

    private static readonly Dictionary<Color, SolidColorBrush> Brushes = new();
    private static readonly Dictionary<(Color, double), Pen> Pens = new();

    public static SolidColorBrush Brush(Color c)
    {
        if (!Brushes.TryGetValue(c, out var b))
        {
            b = new SolidColorBrush(c);
            b.Freeze();
            Brushes[c] = b;
        }
        return b;
    }

    public static Pen IconPen(Color c, double width)
    {
        if (!Pens.TryGetValue((c, width), out var p))
        {
            p = new Pen(Brush(c), width)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
            };
            p.Freeze();
            Pens[(c, width)] = p;
        }
        return p;
    }

    /// Tolerant colour equality (about 0.02 per channel), marks the active preset.
    public static bool SameColor(Color a, Color b) =>
        System.Math.Abs(a.R - b.R) <= 5 && System.Math.Abs(a.G - b.G) <= 5 && System.Math.Abs(a.B - b.B) <= 5;
}
