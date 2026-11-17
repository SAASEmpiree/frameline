using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace FrameLine.Library;

// OWNER: agent "library". Public entry points; the implementations live in the subfolders.
// Every member must be called on the UI thread.
public static class CaptureThumbnail
{
    /// Floating quick preview after a capture (corner from Preferences, drag-and-drop out).
    public static void Show(string path) => QuickPreviewController.Shared.Present(path);

    /// Same, from the capture still in memory: the card appears at once, before the file is
    /// written (Copy and drag-out fall back to this image until the file exists).
    public static void Show(BitmapSource image, string path, bool isVideo = false)
    {
        if (!image.IsFrozen && image.CanFreeze) image.Freeze();
        QuickPreviewController.Shared.Present(image, image, path, isVideo);
    }

    /// Removes the card of a capture whose file could not be written.
    public static void DismissForFailedPath(string path) => QuickPreviewController.Shared.DismissForFailedPath(path);

    /// Takes the whole stack off screen during a capture / recording (re-entrant, paired with
    /// EndCaptureSession). True when this call hid cards: wait a frame before grabbing pixels.
    public static bool BeginCaptureSession() => QuickPreviewController.Shared.BeginCaptureSession();

    public static void EndCaptureSession() => QuickPreviewController.Shared.EndCaptureSession();
}

public static class CaptureHistoryPanel
{
    public static void Toggle() => HistoryWindow.Shared.Toggle();
    public static void Show() => HistoryWindow.Shared.ShowPanel();
    public static void Hide() => HistoryWindow.HideIfOpen();
}

public static class PinnedShotWindow
{
    public static void Show(string imagePath) => PinnedWindow.ShowFile(imagePath);

    public static void Show(BitmapSource image)
    {
        if (!image.IsFrozen && image.CanFreeze) image.Freeze();
        PinnedWindow.ShowImage(image, null);
    }
}

public static class MainLibraryWindow
{
    /// The library window: sidebar of captures + detail view (port of MainWindowController/SidebarView/DetailView).
    public static void ShowWindow() => LibraryWindow.ShowWindow(null);

    /// Opens the window with this capture selected (AppCoordinator.openInApp on macOS).
    public static void ShowWindow(string selectPath) => LibraryWindow.ShowWindow(selectPath);

    public static void Toggle() => LibraryWindow.Toggle();

    public static bool IsOpen => LibraryWindow.IsOpen;

    /// Optional embeddable editors for the detail panel (macOS embeds the annotation editor and
    /// the video trimmer there). Return null to keep the built-in viewer. If the returned element
    /// is IDisposable it is disposed when the selection moves away (save pending edits there).
    public static Func<string, FrameworkElement?>? ImageDetailFactory { get; set; }
    public static Func<string, FrameworkElement?>? VideoDetailFactory { get; set; }
}
