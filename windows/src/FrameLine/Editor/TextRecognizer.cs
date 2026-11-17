using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FrameLine.Editor;

/// "Copy Text" OCR (port of the Vision version) on Windows.Media.Ocr. Decoding and recognition
/// run off the UI thread; only the clipboard write comes back to it.
public static class TextRecognizer
{
    /// Windows.Media.Ocr; returns recognised text ("" if none).
    public static Task<string> RecognizeAsync(string imagePath) => Task.Run(() => RecognizeCoreAsync(imagePath));

    /// Recognises the file's text and puts it on the clipboard. False (clipboard untouched) when
    /// the image is unreadable or holds no text, so the caller can show the right feedback.
    public static async Task<bool> CopyTextAsync(string imagePath)
    {
        var text = await RecognizeAsync(imagePath).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return false;
        return await dispatcher.InvokeAsync(() => EditorImageIO.CopyText(text));
    }

    private static async Task<string> RecognizeCoreAsync(string imagePath)
    {
        try
        {
            var engine = CreateEngine();
            if (engine == null) return "";
            using var bitmap = await LoadBitmapAsync(imagePath).ConfigureAwait(false);
            if (bitmap == null) return "";
            var result = await engine.RecognizeAsync(bitmap);
            return JoinInReadingOrder(result);
        }
        catch (Exception)
        {
            // Unreadable file, codec missing, OCR unavailable: empty rather than a crash.
            return "";
        }
    }

    /// The user's profile languages first (Windows' equivalent of the preferred-language list the
    /// macOS code walked), then English, then any installed recogniser.
    private static OcrEngine? CreateEngine()
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine != null) return engine;
        foreach (var tag in new[] { "en-US", "en-GB", "en" })
        {
            try
            {
                var lang = new Language(tag);
                if (OcrEngine.IsLanguageSupported(lang))
                {
                    engine = OcrEngine.TryCreateFromLanguage(lang);
                    if (engine != null) return engine;
                }
            }
            catch (ArgumentException) { }
        }
        var any = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
        return any != null ? OcrEngine.TryCreateFromLanguage(any) : null;
    }

    /// Decodes to Bgra8 premultiplied (the format OcrEngine expects), downscaled when a side
    /// exceeds OcrEngine.MaxImageDimension (RecognizeAsync rejects larger bitmaps).
    private static async Task<SoftwareBitmap?> LoadBitmapAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(System.IO.Path.GetFullPath(path));
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        uint w = decoder.OrientedPixelWidth, h = decoder.OrientedPixelHeight;
        if (w == 0 || h == 0) return null;
        uint max = OcrEngine.MaxImageDimension;
        var transform = new BitmapTransform();
        if (w > max || h > max)
        {
            double scale = Math.Min((double)max / w, (double)max / h);
            // Scaled sizes apply to the stored (pre-orientation) frame.
            bool swapped = decoder.OrientedPixelWidth != decoder.PixelWidth;
            uint sw = Math.Max(1, (uint)Math.Floor((swapped ? h : w) * scale));
            uint sh = Math.Max(1, (uint)Math.Floor((swapped ? w : h) * scale));
            transform.ScaledWidth = Math.Min(sw, max);
            transform.ScaledHeight = Math.Min(sh, max);
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                                                    ExifOrientationMode.RespectExifOrientation,
                                                    ColorManagementMode.DoNotColorManage);
    }

    /// One output line per recognised line, top to bottom; lines sharing a visual row (their
    /// vertical extents overlap by more than half) go left to right, as Vision returns them.
    private static string JoinInReadingOrder(OcrResult result)
    {
        var lines = new List<(double Top, double Bottom, double Left, string Text)>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0 || string.IsNullOrWhiteSpace(line.Text)) continue;
            double top = double.MaxValue, bottom = double.MinValue, left = double.MaxValue;
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                top = Math.Min(top, r.Y);
                bottom = Math.Max(bottom, r.Y + r.Height);
                left = Math.Min(left, r.X);
            }
            lines.Add((top, bottom, left, line.Text));
        }
        if (lines.Count == 0) return "";

        lines.Sort((a, b) => a.Top.CompareTo(b.Top));
        var rows = new List<List<(double Top, double Bottom, double Left, string Text)>>();
        foreach (var l in lines)
        {
            var row = rows.Count > 0 ? rows[^1] : null;
            if (row != null && SameRow(row[0], l)) row.Add(l);
            else rows.Add(new() { l });
        }
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            foreach (var l in row.OrderBy(x => x.Left))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(l.Text);
            }
        }
        return sb.ToString();
    }

    private static bool SameRow((double Top, double Bottom, double Left, string Text) a,
                                (double Top, double Bottom, double Left, string Text) b)
    {
        double overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        double minH = Math.Min(a.Bottom - a.Top, b.Bottom - b.Top);
        return minH > 0 && overlap > minH / 2;
    }
}
