using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FrameLine.Library;

/// SF Symbols of the macOS app mapped to Segoe Fluent Icons (Windows 11) / Segoe MDL2 Assets
/// (Windows 10), which share these code points. The two symbols without a font equivalent
/// (sparkles, macwindow) and the play triangle are drawn as paths.
internal static class Glyphs
{
    public const string Edit = "";          // pencil.tip.crop.circle
    public const string Copy = "";          // doc.on.doc
    public const string Pin = "";           // pin
    public const string Folder = "";        // folder
    public const string Delete = "";        // trash
    public const string Close = "";         // xmark
    public const string Settings = "";      // gearshape
    public const string Refresh = "";       // arrow.clockwise
    public const string FullScreen = "";    // arrow.up.left.and.arrow.down.right
    public const string Share = "";         // square.and.arrow.up
    public const string History = "";       // clock.arrow.circlepath
    public const string Photo = "";         // photo / photo.on.rectangle
    public const string Monitor = "";       // display
    public const string Sidebar = "";       // sidebar.left
    public const string Region = "";        // viewfinder
    public const string Camera = "";        // camera.viewfinder
    public const string Text = "";          // text.viewfinder
    public const string Play = "";          // play.circle
    public const string Pause = "";
    public const string Cut = "";           // trim
    public const string ChromeMinimize = "";
    public const string ChromeMaximize = "";
    public const string ChromeRestore = "";
    public const string ChromeClose = "";

    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");

    private static readonly Geometry SparklesGeo = Frozen(Geometry.Parse(
        "M7,2 L8.6,6.9 L13.5,8.5 L8.6,10.1 L7,15 L5.4,10.1 L0.5,8.5 L5.4,6.9 Z " +
        "M13,0 L13.8,2.2 L16,3 L13.8,3.8 L13,6 L12.2,3.8 L10,3 L12.2,2.2 Z"));
    private static readonly Geometry WindowGeo = Frozen(Geometry.Parse(
        "M1.5,2.5 L14.5,2.5 L14.5,13.5 L1.5,13.5 Z M1.5,5.5 L14.5,5.5"));
    public static readonly Geometry PlayTriangle = Frozen(Geometry.Parse("M0,0 L10,6 L0,12 Z"));

    private static Geometry Frozen(Geometry g) { g.Freeze(); return g; }

    /// An icon element; "sparkles" and "window" are path icons, anything else a font glyph.
    public static FrameworkElement Icon(string glyph, double size, Brush brush, bool bold = false)
    {
        if (glyph == "sparkles") return PathIcon(SparklesGeo, size, brush, fill: true);
        if (glyph == "window") return PathIcon(WindowGeo, size, brush, fill: false);
        return new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = size,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = brush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
    }

    public static void SetBrush(FrameworkElement icon, Brush brush)
    {
        switch (icon)
        {
            case TextBlock t: t.Foreground = brush; break;
            case Path p when p.Fill != null: p.Fill = brush; break;
            case Path p: p.Stroke = brush; break;
        }
    }

    public static void SetResourceBrush(FrameworkElement icon, string key)
    {
        switch (icon)
        {
            case TextBlock t: t.SetResourceReference(TextBlock.ForegroundProperty, key); break;
            case Path p when p.Fill != null: p.SetResourceReference(Shape.FillProperty, key); break;
            case Path p: p.SetResourceReference(Shape.StrokeProperty, key); break;
        }
    }

    private static Path PathIcon(Geometry g, double size, Brush brush, bool fill) => new()
    {
        Data = g,
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        Fill = fill ? brush : null,
        Stroke = fill ? null : brush,
        StrokeThickness = fill ? 0 : 1.4,
        StrokeLineJoin = PenLineJoin.Round,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
        SnapsToDevicePixels = true,
    };

    public static SolidColorBrush White(double alpha) => Brush(255, 255, 255, alpha);
    public static SolidColorBrush Black(double alpha) => Brush(0, 0, 0, alpha);
    public static SolidColorBrush Gray(double white, double alpha = 1)
    {
        byte v = (byte)System.Math.Round(white * 255);
        return Brush(v, v, v, alpha);
    }

    public static SolidColorBrush Brush(byte r, byte g, byte b, double alpha)
    {
        var br = new SolidColorBrush(Color.FromArgb((byte)System.Math.Round(alpha * 255), r, g, b));
        br.Freeze();
        return br;
    }

    /// macOS systemRed (dark appearance).
    public static readonly SolidColorBrush SystemRed = Brush(0xFF, 0x45, 0x3A, 1);
}
