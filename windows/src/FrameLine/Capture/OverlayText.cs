using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FrameLine.Capture;

/// Typefaces, brushes and pens for the capture overlays, built ONCE and frozen.
/// The loupe redraws on every mouse move; on macOS creating a font per frame raced CoreText's
/// teardown and crashed the app (7/7 crash reports, Oct 2026). Same rule here: nothing below is
/// allocated in a render path.
internal static class OverlayText
{
    // Monospaced like NSFont.monospacedSystemFont (Cascadia Mono on recent Windows, Consolas otherwise).
    public static readonly Typeface Loupe = new(new FontFamily("Cascadia Mono, Consolas"),
        FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
    // Segoe UI digits are tabular, like monospacedDigitSystemFont.
    public static readonly Typeface Badge = new(new FontFamily("Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    public static readonly Typeface WindowLabel = Badge;

    public const double LoupeSize = 11, BadgeSize = 11, WindowLabelSize = 12;

    public static readonly Brush White = Frozen(new SolidColorBrush(Colors.White));
    public static readonly Brush Black = Frozen(new SolidColorBrush(Colors.Black));
    public static readonly Brush Veil = Frozen(new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)));        // 0.5
    public static readonly Brush WindowVeil = Frozen(new SolidColorBrush(Color.FromArgb(71, 0, 0, 0)));   // 0.28
    public static readonly Brush BadgeBack = Frozen(new SolidColorBrush(Color.FromArgb(204, 0, 0, 0)));   // 0.8
    public static readonly Brush PillBack = Frozen(new SolidColorBrush(Color.FromArgb(199, 0, 0, 0)));    // 0.78
    public static readonly Brush LabelBack = Frozen(new SolidColorBrush(Color.FromArgb(209, 0, 0, 0)));   // 0.82
    public static readonly Brush CrossBrush = Frozen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)));
    public static readonly Brush GridBrush = Frozen(new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)));

    public static FormattedText Format(string text, Typeface face, double size, double pixelsPerDip) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, White, pixelsPerDip);

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
