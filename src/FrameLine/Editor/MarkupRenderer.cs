using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FrameLine.Editor;

/// Draws annotations. The SAME code paints the on-screen canvas and the flattened bitmap, both in
/// image pixel coordinates, so what is seen is what is saved.
/// Pens, brushes and the typeface are cached for the process lifetime: rendering runs on every
/// mouse move while dragging (the macOS app crashed once for re-creating fonts per frame).
internal static class MarkupRenderer
{
    public static readonly Typeface TextTypeface =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static readonly Dictionary<Color, SolidColorBrush> Brushes = new();
    private static readonly Dictionary<(Color, double), Pen> Pens = new();
    private static readonly Brush CropShade = Freeze(new SolidColorBrush(Color.FromArgb(115, 0, 0, 0)));
    private static readonly Dictionary<double, Pen> CropBorders = new();

    public static SolidColorBrush BrushFor(Color c)
    {
        if (!Brushes.TryGetValue(c, out var b))
        {
            b = new SolidColorBrush(c);
            b.Freeze();
            Brushes[c] = b;
        }
        return b;
    }

    private static Pen PenFor(Color c, double width)
    {
        if (!Pens.TryGetValue((c, width), out var p))
        {
            p = new Pen(BrushFor(c), width)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            p.Freeze();
            Pens[(c, width)] = p;
        }
        return p;
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public static void Draw(DrawingContext dc, Markup m, Func<BitmapSource?> pixelated)
    {
        switch (m.Tool)
        {
            case MarkupTool.Rectangle:
            case MarkupTool.Arrow:
            case MarkupTool.Pencil:
                StrokeShape(dc, m);
                break;
            case MarkupTool.Pixelate:
                DrawPixelate(dc, m, pixelated());
                break;
            case MarkupTool.Text:
                DrawText(dc, m);
                break;
        }
    }

    private static void StrokeShape(DrawingContext dc, Markup m)
    {
        var pen = PenFor(m.Color, m.LineWidth);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            switch (m.Tool)
            {
                case MarkupTool.Rectangle:
                {
                    var r = m.NormalizedRect;
                    g.BeginFigure(r.TopLeft, false, true);
                    g.PolyLineTo(new[] { r.TopRight, r.BottomRight, r.BottomLeft }, true, true);
                    break;
                }
                case MarkupTool.Arrow:
                {
                    var s = m.Start; var e = m.End;
                    g.BeginFigure(s, false, false);
                    g.LineTo(e, true, true);
                    double ang = Math.Atan2(e.Y - s.Y, e.X - s.X);
                    double head = m.LineWidth * 3;
                    foreach (var a in new[] { ang + Math.PI * 0.82, ang - Math.PI * 0.82 })
                    {
                        g.BeginFigure(e, false, false);
                        g.LineTo(new Point(e.X + Math.Cos(a) * head, e.Y + Math.Sin(a) * head), true, true);
                    }
                    break;
                }
                case MarkupTool.Pencil:
                {
                    if (m.Points.Count == 0) return;
                    g.BeginFigure(m.Points[0], false, false);
                    if (m.Points.Count > 1)
                    {
                        var rest = new Point[m.Points.Count - 1];
                        for (int i = 1; i < m.Points.Count; i++) rest[i - 1] = m.Points[i];
                        g.PolyLineTo(rest, true, true);
                    }
                    break;
                }
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    public static FormattedText MakeText(string text, double fontSize, Color color) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextTypeface,
            Math.Max(1, Math.Round(fontSize)), BrushFor(color), 1.0);

    /// `Start` is the bottom-left of the text line (anchor kept from the macOS editor).
    private static void DrawText(DrawingContext dc, Markup m)
    {
        if (string.IsNullOrEmpty(m.Text)) return;
        var ft = MakeText(m.Text, m.FontSize, m.Color);
        dc.DrawText(ft, new Point(m.Start.X, m.Start.Y - ft.Height));
    }

    private static void DrawPixelate(DrawingContext dc, Markup m, BitmapSource? pix)
    {
        if (pix is null) return;
        var r = m.NormalizedRect;
        if (r.Width <= 0 || r.Height <= 0) return;
        dc.PushClip(new RectangleGeometry(r));
        dc.DrawImage(pix, new Rect(0, 0, pix.PixelWidth, pix.PixelHeight));
        dc.Pop();
    }

    /// Darkens everything but the kept area (`area` = whole image).
    public static void DrawCropMask(DrawingContext dc, Markup m, Rect area, double borderWidth)
    {
        var keep = m.NormalizedRect;
        keep.Intersect(area);
        if (keep.IsEmpty) keep = new Rect(m.Start, new Size(0, 0));
        var mask = new GeometryGroup { FillRule = FillRule.EvenOdd };
        mask.Children.Add(new RectangleGeometry(area));
        mask.Children.Add(new RectangleGeometry(keep));
        dc.DrawGeometry(CropShade, null, mask);
        if (!CropBorders.TryGetValue(borderWidth, out var pen))
        {
            pen = new Pen(System.Windows.Media.Brushes.White, borderWidth);
            pen.Freeze();
            if (CropBorders.Count > 32) CropBorders.Clear();
            CropBorders[borderWidth] = pen;
        }
        dc.DrawRectangle(null, pen, keep);
    }

    /// Full-frame mosaic at native resolution, like CIPixellate: block = max(8, min(w,h)/50) px,
    /// grid anchored on the image centre, each block takes the colour of the pixel at its centre.
    public static BitmapSource Pixelate(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var pixels = new byte[(long)stride * h];
        src.CopyPixels(pixels, stride, 0);
        double block = Math.Max(8, Math.Min(w, h) / 50.0);
        int[] sx = SampleIndices(w, block);
        int[] sy = SampleIndices(h, block);
        var outPx = new byte[pixels.LongLength];
        for (int y = 0; y < h; y++)
        {
            long srcRow = (long)sy[y] * stride, dstRow = (long)y * stride;
            for (int x = 0; x < w; x++)
            {
                long s = srcRow + sx[x] * 4L, d = dstRow + x * 4L;
                outPx[d] = pixels[s]; outPx[d + 1] = pixels[s + 1]; outPx[d + 2] = pixels[s + 2]; outPx[d + 3] = pixels[s + 3];
            }
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, outPx, stride);
        bmp.Freeze();
        return bmp;
    }

    /// For each pixel index along one axis, the index of the pixel at the centre of its block.
    private static int[] SampleIndices(int length, double block)
    {
        var result = new int[length];
        double centre = length / 2.0;
        for (int i = 0; i < length; i++)
        {
            double cell = Math.Floor((i + 0.5 - centre) / block);
            int sample = (int)Math.Floor(centre + (cell + 0.5) * block);
            result[i] = Math.Clamp(sample, 0, length - 1);
        }
        return result;
    }
}
