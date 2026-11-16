using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FrameLine.Core;

namespace FrameLine.Library;

/// Stack of post-capture cards (CaptureThumbnailController of CaptureThumbnail.swift). Index 0 is
/// the newest, AT the chosen corner; older ones stack inwards (upwards for a bottom corner,
/// downwards for a top one). Up to 6, the oldest leaves beyond that. No auto-dismiss: the macOS
/// timer is disabled, a card stays until its cross, the trash, or eviction.
internal sealed class QuickPreviewController
{
    private static QuickPreviewController? _shared;
    public static QuickPreviewController Shared => _shared ??= new QuickPreviewController();

    private const int MaxCount = 6;
    private const double Margin = 19, Gap = 12;

    private readonly List<ThumbnailPanel> _panels = new();
    private IntPtr _anchor;
    private DispatcherTimer? _follow;
    private int _captureDepth;
    private bool IsSuspended => _captureDepth > 0;

    private QuickPreviewController()
    {
        Preferences.Shared.Changed += name => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (name == nameof(Preferences.PreviewFollowsCursor)) SyncFollowing();
            else if (name == nameof(Preferences.PreviewCorner)) Relayout(animated: true);
        });
    }

    /// Path only: decodes off the UI thread, then presents (videos get their shell thumbnail).
    public void Present(string path)
    {
        bool video = LibImages.IsVideoPath(path);
        var dispatcher = Application.Current.Dispatcher;
        Task.Run(() => video ? LibImages.LoadVideoThumbnail(path, 720) : LibImages.LoadScaled(path, 720))
            .ContinueWith(t => dispatcher.BeginInvoke(() =>
            {
                var img = t.IsCompletedSuccessfully ? t.Result : null;
                if (img == null && !video) return;     // unreadable image: nothing to preview
                Present(img, null, path, video);
            }));
    }

    /// Adds a card to the stack without replacing the previous ones. `full` is the capture still
    /// in memory (the file may not be written yet): used by "Copy" and drag-out until it is.
    public void Present(BitmapSource? image, BitmapSource? full, string path, bool isVideo)
    {
        _anchor = MonitorRef.UnderCursor().Handle;
        var p = new ThumbnailPanel(image ?? full, full, path, isVideo);
        p.ClosedCallback = () =>
        {
            _panels.Remove(p);
            Relayout(animated: true);          // the others close up towards the corner
            SyncFollowing();
        };
        _panels.Insert(0, p);
        // Taken DURING a session (screenshot while recording): placed, kept off screen until the
        // session ends, like the rest of the stack.
        if (IsSuspended) p.SetSuspended(true);
        if (_panels.Count > MaxCount)
        {
            var oldest = _panels[^1];
            _panels.RemoveAt(_panels.Count - 1);
            oldest.ClosedCallback = null;
            oldest.DismissImmediately();
        }
        Relayout(animated: true, appearing: p);
        SyncFollowing();
    }

    /// Removes the card of a capture whose file could not be written (orphan card).
    public void DismissForFailedPath(string path)
    {
        var p = _panels.Find(x => string.Equals(x.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (p == null) return;
        _panels.Remove(p);
        p.ClosedCallback = null;
        p.DismissImmediately();
        Relayout(animated: true);
        SyncFollowing();
    }

    /// While a card is visible, a 0.2 s timer watches the cursor's monitor; when it changes the
    /// whole stack glides to the same corner of the new monitor.
    private void SyncFollowing()
    {
        if (_panels.Count == 0 || !Preferences.Shared.PreviewFollowsCursor)
        {
            _follow?.Stop();
        }
        else
        {
            _follow ??= new DispatcherTimer(TimeSpan.FromSeconds(0.2), DispatcherPriority.Normal,
                (_, _) => FollowMouse(), Application.Current.Dispatcher);
            if (!_follow.IsEnabled) _follow.Start();
        }
    }

    private void FollowMouse()
    {
        if (_panels.Count == 0 || !Preferences.Shared.PreviewFollowsCursor) { SyncFollowing(); return; }
        // A drag from a card is in progress: the cursor crossing to another monitor is not a
        // "monitor change". Without this guard the stack fled to the target monitor's corner and
        // could land UNDER the drop point.
        if (_panels.Exists(x => x.IsDraggingFile)) return;
        var m = MonitorRef.UnderCursor();
        if (m.Handle != _anchor)
        {
            _anchor = m.Handle;
            Relayout(animated: true);
        }
    }

    private void Relayout(bool animated, ThumbnailPanel? appearing = null)
    {
        var m = MonitorRef.Refresh(_anchor);
        var work = m.Work;
        string corner = Preferences.Shared.PreviewCorner ?? "bottomLeft";
        bool isRight = corner is "bottomRight" or "topRight";
        bool isTop = corner is "topLeft" or "topRight";
        int margin = m.Px(Margin), pad = m.Px(ThumbnailPanel.Pad);
        int cardW = m.Px(ThumbnailPanel.CardWidth), cardH = m.Px(ThumbnailPanel.CardHeight), gap = m.Px(Gap);
        int x = isRight ? work.Right - margin - cardW - pad : work.Left + margin - pad;
        for (int i = 0; i < _panels.Count; i++)
        {
            int stack = i * (cardH + gap);
            int y = isTop ? work.Top + margin - pad + stack : work.Bottom - margin - cardH - pad - stack;
            var p = _panels[i];
            if (ReferenceEquals(p, appearing)) p.Appear(x, y, isRight, m);
            else p.Relocate(x, y, animated);
        }
    }

    // MARK: - Capture session (the stack must NEVER appear in the next capture)

    /// Takes the stack off screen (without forgetting it) while a capture / recording happens.
    /// Re-entrant; returns true when THIS call hid cards, so the caller can let DWM recompose the
    /// desktop before grabbing pixels.
    public bool BeginCaptureSession()
    {
        _captureDepth++;
        if (_captureDepth != 1 || _panels.Count == 0) return false;
        foreach (var p in _panels) p.SetSuspended(true);
        return true;
    }

    public void EndCaptureSession()
    {
        if (_captureDepth == 0) return;
        if (--_captureDepth != 0) return;
        foreach (var p in _panels) p.SetSuspended(false);
    }
}
