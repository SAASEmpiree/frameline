using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FrameLine.Library;

/// One-line text truncated in the MIDDLE ("FrameLine 2026-11…16.37.05.png"), the SwiftUI
/// .truncationMode(.middle) of the macOS rows: both the date and the extension stay readable.
internal sealed class MiddleEllipsisText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MiddleEllipsisText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(MiddleEllipsisText), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public double FontSize { get; init; } = 12;
    public FontWeight FontWeight { get; init; } = FontWeights.Normal;

    private string _shown = "";
    private Typeface? _typeface;
    private Typeface Face => _typeface ??= new Typeface(Glyphs.UiFont, FontStyles.Normal, FontWeight, FontStretches.Normal);

    private FormattedText Format(string s) => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face,
        FontSize, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override Size MeasureOverride(Size available)
    {
        var full = Text ?? "";
        var ft = Format(full);
        double max = double.IsInfinity(available.Width) ? ft.WidthIncludingTrailingWhitespace : available.Width;
        _shown = full;
        if (ft.WidthIncludingTrailingWhitespace > max && full.Length > 2)
        {
            // Binary search on the number of kept characters, split evenly around the ellipsis.
            int lo = 0, hi = full.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Format(Cut(full, mid)).WidthIncludingTrailingWhitespace <= max) lo = mid; else hi = mid - 1;
            }
            _shown = Cut(full, lo);
        }
        var shown = Format(_shown);
        return new Size(Math.Min(shown.WidthIncludingTrailingWhitespace, max), shown.Height);
    }

    private static string Cut(string s, int keep)
    {
        if (keep <= 0) return "…";
        int head = (keep + 1) / 2, tail = keep - head;
        return s.Substring(0, head) + "…" + s.Substring(s.Length - tail);
    }

    protected override void OnRender(DrawingContext dc) => dc.DrawText(Format(_shown), new Point(0, 0));
}

/// Gives its child a height of width / Aspect (SwiftUI .aspectRatio(_, contentMode: .fit) on a
/// full-width card) and clips it to a rounded rectangle.
internal sealed class AspectBox : Decorator
{
    public double Aspect { get; init; } = 2.6;
    public double Radius { get; init; } = 9;

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 260 : available.Width;
        var size = new Size(w, Math.Round(w / Aspect));
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size final)
    {
        Child?.Arrange(new Rect(final));
        var clip = new RectangleGeometry(new Rect(final), Radius, Radius);
        clip.Freeze();
        Clip = clip;
        return final;
    }
}

/// Video "play" badge: dark translucent disc + white triangle (VideoPlayBadge of SidebarView).
internal static class VideoBadge
{
    public static FrameworkElement Create(double size)
    {
        var g = new Grid { Width = size, Height = size, IsHitTestVisible = false };
        g.Children.Add(new Ellipse
        {
            Fill = Glyphs.Black(0.5),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = size * 0.18, ShadowDepth = 1, Opacity = 0.35 },
        });
        g.Children.Add(new Path
        {
            Data = Glyphs.PlayTriangle,
            Fill = Brushes.White,
            Stretch = Stretch.Uniform,
            Width = size * 0.34,
            Height = size * 0.40,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(size * 0.06, 0, 0, 0),   // optical centring: the triangle leans right
        });
        return g;
    }
}
