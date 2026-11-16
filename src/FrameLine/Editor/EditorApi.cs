using System;
using System.Threading.Tasks;

namespace FrameLine.Editor;

// OWNER: agent "editor". Contract stubs; replace bodies, keep public signatures.
public static class AnnotationEditor
{
    /// Opens the annotation editor (rect, arrow, pencil, text, pixelate, crop) on an image; saves in place.
    public static void Open(string imagePath) { }
}

public static class TextRecognizer
{
    /// Windows.Media.Ocr; returns recognised text ("" if none).
    public static Task<string> RecognizeAsync(string imagePath) => Task.FromResult("");
}
