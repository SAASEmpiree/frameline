using System;
using System.Windows;

namespace FrameLine.Editor;

// OWNER: agent "editor". Contract stubs; replace bodies, keep public signatures.
public static class AnnotationEditor
{
    /// Opens the annotation editor (rect, arrow, pencil, text, pixelate, crop) on an image; saves in place.
    public static void Open(string imagePath)
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(() => Open(imagePath)); return; }
        // Unreadable or missing image: nothing to edit (same guard as the macOS controller).
        if (string.IsNullOrEmpty(imagePath) || !System.IO.File.Exists(imagePath)) return;
        var image = EditorImageIO.Load(imagePath);
        if (image == null) return;
        new PreviewWindow(imagePath, image).ShowCentered();
    }
}
