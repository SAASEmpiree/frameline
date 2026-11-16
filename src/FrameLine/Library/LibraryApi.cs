using System;
using System.Windows.Media.Imaging;

namespace FrameLine.Library;

// OWNER: agent "library". Contract stubs; replace bodies, keep public signatures.
public static class CaptureThumbnail
{
    /// Floating quick preview after a capture (corner from Preferences, drag-and-drop out).
    public static void Show(string path) { }
}
public static class CaptureHistoryPanel
{
    public static void Toggle() { }
}
public static class PinnedShotWindow
{
    public static void Show(string imagePath) { }
    public static void Show(BitmapSource image) { }
}
public static class MainLibraryWindow
{
    /// The library window: sidebar of captures + detail view (port of MainWindowController/SidebarView/DetailView).
    public static void ShowWindow() { }
}
