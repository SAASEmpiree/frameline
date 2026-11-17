using System;
using System.ComponentModel;
using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FrameLine.Library;

/// One capture as the library windows see it (UI thread only, except ViewRefs which the
/// thumbnail workers read). Identity is the path; Created changes when the file is rewritten in
/// place (annotation saved, crop), which drops the cached thumbnail and pixel size, the fix the
/// macOS code records for its ".task(id: url + date)".
internal sealed class CaptureEntry : INotifyPropertyChanged
{
    public const double CardAspect = 2.6;
    private const int CoverWidthPx = 640;   // ~card width at 200 % scale (sidebar max 400 DIP)
    private const int FitMaxPx = 168;       // history card is 78×52 DIP

    public CaptureEntry(string path, DateTime created, bool isVideo)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        Created = created;
        IsVideo = isVideo;
    }

    public string Path { get; }
    public string Name { get; }
    public DateTime Created { get; private set; }
    public bool IsVideo { get; private set; }

    public BitmapSource? Cover { get; private set; }
    public BitmapSource? Fit { get; private set; }
    public (int W, int H)? PixelSize { get; private set; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(nameof(IsSelected)); } }
    }

    /// Number of realized views showing this entry; workers skip jobs nobody looks at any more.
    private int _viewRefs;
    public void AddView() => Interlocked.Increment(ref _viewRefs);
    public void RemoveView() => Interlocked.Decrement(ref _viewRefs);
    public bool IsVisible => Volatile.Read(ref _viewRefs) > 0;

    public string Subtitle => Compose(LibText.RelativeTime(Created));
    public string SubtitleEnglish => Compose(LibText.RelativeTimeEnglish(Created));
    private string Compose(string rel) => PixelSize is { } px ? $"{rel} · {px.W}×{px.H}" : rel;

    private int _generation;
    private bool _coverPending, _fitPending, _coverFailed, _fitFailed;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// Re-synced from the folder scan. A new modification time means new pixels.
    public void Update(DateTime created, bool isVideo)
    {
        if (created == Created && isVideo == IsVideo) return;
        Created = created;
        IsVideo = isVideo;
        Invalidate();
        Raise(nameof(Created));
    }

    public void Invalidate()
    {
        _generation++;
        _coverPending = _fitPending = _coverFailed = _fitFailed = false;
        Cover = null;
        Fit = null;
        PixelSize = null;
        Raise(nameof(Cover));
        Raise(nameof(Fit));
        RaiseSubtitles();
    }

    /// A failed decode (file still being written) is retried after the next folder refresh only,
    /// like macOS, instead of on every row recycle.
    public void ClearFailure() { _coverFailed = _fitFailed = false; }

    public void RaiseSubtitles()
    {
        Raise(nameof(Subtitle));
        Raise(nameof(SubtitleEnglish));
    }

    public void DropCover()
    {
        if (Cover == null) return;
        Cover = null;
        Raise(nameof(Cover));
    }

    public void RequestCover(Dispatcher dispatcher)
    {
        if (Cover != null || _coverPending || _coverFailed) return;
        _coverPending = true;
        int gen = _generation;
        string path = Path;
        bool video = IsVideo;
        ThumbnailService.Shared.Request(() => IsVisible, () =>
        {
            if (video)
            {
                var v = LibImages.LoadVideoThumbnail(path, CoverWidthPx);
                return new ThumbResult(v == null ? null : LibImages.CoverCrop(v, CardAspect), null, false);
            }
            var px = LibImages.ReadPixelSize(path);
            var img = LibImages.LoadScaled(path, CoverWidthPx, CardAspect);
            return new ThumbResult(img == null ? null : LibImages.CoverCrop(img, CardAspect), px, false);
        }, r =>
        {
            if (gen != _generation) return;            // file rewritten meanwhile: stale result
            _coverPending = false;
            if (r.Abandoned) return;                    // scrolled away: asked again when realized
            if (r.Image == null) { _coverFailed = true; return; }
            Cover = r.Image;
            Raise(nameof(Cover));
            if (r.PixelSize != null && PixelSize == null) { PixelSize = r.PixelSize; RaiseSubtitles(); }
            LibraryModel.Shared.TouchCover(this);
        }, dispatcher);
    }

    public void RequestFit(Dispatcher dispatcher)
    {
        if (Fit != null || _fitPending || _fitFailed) return;
        _fitPending = true;
        int gen = _generation;
        string path = Path;
        bool video = IsVideo;
        ThumbnailService.Shared.Request(() => true, () =>
        {
            if (video) return new ThumbResult(LibImages.LoadVideoThumbnail(path, FitMaxPx * 2), null, false);
            return new ThumbResult(LibImages.LoadScaled(path, FitMaxPx), LibImages.ReadPixelSize(path), false);
        }, r =>
        {
            if (gen != _generation) return;
            _fitPending = false;
            if (r.Image == null) { _fitFailed = true; return; }
            Fit = r.Image;
            Raise(nameof(Fit));
            if (r.PixelSize != null && PixelSize == null) { PixelSize = r.PixelSize; RaiseSubtitles(); }
        }, dispatcher);
    }
}
