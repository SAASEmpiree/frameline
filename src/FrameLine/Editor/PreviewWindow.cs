using System;
using System.Windows;
using System.Windows.Input;

namespace FrameLine.Editor;

/// Floating editor window opened after a capture (port of PreviewWindowController): hosts the
/// same MarkupEditorView as the library detail pane. Done saves then closes; closing otherwise
/// discards unsaved annotations without asking, as on macOS.
internal sealed class PreviewWindow : Window
{
    private const double MaxW = 1100, MaxH = 760, ChromeV = 120;   // ChromeV: room for the two capsules
    private readonly MarkupEditorView _editor;
    private readonly double _contentW, _contentH;

    public PreviewWindow(string path, System.Windows.Media.Imaging.BitmapSource image)
    {
        _editor = new MarkupEditorView(path, image) { DefaultDone = true };
        _editor.DoneRequested += Close;

        int pw = _editor.ImagePixelWidth, ph = _editor.ImagePixelHeight;
        double scale = Math.Min(1, Math.Min(MaxW / Math.Max(pw, 1), (MaxH - ChromeV) / Math.Max(ph, 1)));
        _contentW = Math.Max(760, pw * scale);
        _contentH = Math.Max(360, ph * scale) + ChromeV;

        Title = EditorText.WindowTitle(System.IO.Path.GetFileName(path));
        Content = _editor;
        Background = EditorPalette.Brush(EditorPalette.Ink);
        Topmost = true;                       // NSWindow.Level.floating
        ShowInTaskbar = true;
        MinWidth = 760;
        MinHeight = 380;
        Width = _contentW + 16;               // provisional; exact client size set in PlaceWindow
        Height = _contentH + 39;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ResizeMode = ResizeMode.CanResize;
        TrySetIcon();

        PreviewKeyDown += (_, e) =>
        {
            // Ctrl+W closes, as Cmd+W does through the macOS File menu.
            if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control) { Close(); e.Handled = true; }
        };
        Loaded += (_, _) => Keyboard.Focus(_editor);
    }

    private void TrySetIcon()
    {
        try
        {
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/FrameLine;component/Assets/FrameLine.ico", UriKind.Absolute));
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UriFormatException)
        {
            // the window falls back to the process icon
        }
    }

    /// Creates the HWND, sizes the CLIENT area to the computed content size in that monitor's
    /// physical pixels, centres it on the cursor's monitor, then shows and activates it.
    public void ShowCentered()
    {
        EditorNative.UseDarkTitleBar(this);
        EditorNative.PlaceClientOnCursorMonitor(this, _contentW, _contentH);
        Show();
        Activate();
    }
}
