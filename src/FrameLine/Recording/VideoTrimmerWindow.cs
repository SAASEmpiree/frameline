using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FrameLine.Platform;

namespace FrameLine.Recording;

/// Floating trim window, framed like the image editor's ("Preview — name", 960 x 640 content,
/// minimum 760 x 380, kept above other windows). Done closes after saving; closing the window or
/// Esc is Cancel: the export in flight is cancelled and nothing is written.
internal sealed class VideoTrimmerWindow : Window
{
    private static readonly Dictionary<string, VideoTrimmerWindow> open = new(StringComparer.OrdinalIgnoreCase);

    private readonly VideoTrimmerView view;
    private readonly string key;

    public static void Open(string videoPath)
    {
        var app = Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(() => Open(videoPath)); return; }
        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
        {
            RecBadge.Show(RecStrings.Unreadable, RecBadge.GlyphWarning);
            return;
        }
        var full = Path.GetFullPath(videoPath);
        if (open.TryGetValue(full, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var w = new VideoTrimmerWindow(full);
        w.Show();
        w.Activate();
    }

    private VideoTrimmerWindow(string path)
    {
        key = path;
        Title = RecStrings.WindowTitle(Path.GetFileName(path));
        Background = new SolidColorBrush(Color.FromRgb(19, 19, 19));
        MinWidth = 760;
        MinHeight = 380;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;   // monitor under the cursor
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;

        view = new VideoTrimmerView(path) { DefaultDone = true, Width = 960, Height = 640 };
        view.OnDone = Close;
        Content = view;
        open[key] = this;

        // Size to the 960 x 640 content once, then let the user resize freely.
        ContentRendered += (_, _) =>
        {
            SizeToContent = SizeToContent.Manual;
            view.Width = double.NaN;
            view.Height = double.NaN;
        };
        Glass.SetDarkTitleBar(this, true);
        Loaded += (_, _) => FocusTrack();
        Activated += (_, _) => FocusTrack();
    }

    private void FocusTrack()
    {
        if (view.FindName("Track") is IInputElement track) Keyboard.Focus(track);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); return; }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (open.TryGetValue(key, out var w) && w == this) open.Remove(key);
        view.Dispose();
        base.OnClosed(e);
    }
}
