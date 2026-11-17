using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Editing;

namespace FrameLine.Recording;

/// The trim "filmstrip": thumbnails edge to edge, the kept range [in, out] lit while both excluded
/// ends are darkened with the image editor's crop mask (black 0.45 + 1 px white frame), white handles
/// like a selected tool, a thin playhead. Geometry is in DIPs; times convert through XFor / TimeFor.
internal sealed class TrimTrackView : FrameworkElement
{
    private static readonly Brush Ink = Frozen(Color.FromRgb(19, 19, 19));
    private static readonly Brush Mask = Frozen(Color.FromArgb(115, 0, 0, 0));          // black 0.45
    private static readonly Brush ExportWash = Frozen(Color.FromArgb(140, 255, 255, 255)); // white 0.55
    private static readonly Brush HandleFill = Frozen(Color.FromArgb(247, 255, 255, 255)); // white 0.97
    private static readonly Pen FramePen = FrozenPen(Brushes.White, 1);
    private static readonly Pen PlayheadPen = FrozenPen(Brushes.White, 2);
    private static readonly Pen GripPen = FrozenPen(Frozen(Color.FromRgb(26, 26, 26)), 1);

    private const double InsetX = 9, InsetY = 8, HandleHit = 8;

    private double duration, inTime, outTime, playhead;
    private bool isExporting;
    private double exportProgress;

    public double MinDur { get; set; } = 0.3;
    public double Duration { get => duration; set { duration = value; InvalidateVisual(); } }
    public double InTime { get => inTime; set { inTime = value; InvalidateVisual(); } }
    public double OutTime { get => outTime; set { outTime = value; InvalidateVisual(); } }
    public double Playhead { get => playhead; set { if (playhead != value) { playhead = value; InvalidateVisual(); } } }
    public bool IsExporting { get => isExporting; set { isExporting = value; InvalidateVisual(); } }
    public double ExportProgress { get => exportProgress; set { exportProgress = value; if (isExporting) InvalidateVisual(); } }

    // Interaction callbacks (the view model applies and seeks).
    public Action<double>? ScrubIn, ScrubOut, ScrubPlayhead;
    public Action? EditingBegan;
    public Action? TogglePlay, SetIn, SetOut, CommitDone;
    public Action<bool, bool>? Step;   // forward, coarse

    // Filmstrip
    public string? AssetPath { get; set; }
    public double AssetAspect { get; set; } = 1.6;
    private readonly Dictionary<int, BitmapSource> frames = new();
    private int frameCount;
    private int widthBucket = -1;
    private int generation;   // ignores thumbnails of a stale generation
    private CancellationTokenSource? stripCts;

    public TrimTrackView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Height = 44;
        ClipToBounds = false;
        SizeChanged += (_, _) => RebuildFilmstripIfNeeded();
        Unloaded += (_, _) => CancelFilmstrip();
    }

    private double UsableW => Math.Max(1, ActualWidth - InsetX * 2);
    private Rect TrackRect => new(InsetX, InsetY, Math.Max(0, ActualWidth - InsetX * 2), Math.Max(0, ActualHeight - InsetY * 2));

    private double XFor(double t)
    {
        double d = Math.Max(duration, 0.0001);
        return InsetX + Math.Clamp(t, 0, d) / d * UsableW;
    }

    private double TimeFor(double x) => Math.Clamp((x - InsetX) / UsableW, 0, 1) * duration;

    // MARK: filmstrip

    /// Forces regeneration (after an in-place trim the duration changed but the width did not).
    public void ReloadFilmstrip()
    {
        widthBucket = -1;
        frames.Clear();
        RebuildFilmstripIfNeeded();
    }

    public void CancelFilmstrip()
    {
        stripCts?.Cancel();
        stripCts = null;
    }

    /// Regenerates only when the width changes by a ~40 px bucket. Decoding happens off the UI thread.
    public void RebuildFilmstripIfNeeded()
    {
        if (AssetPath == null || duration <= 0.001) return;
        var r = TrackRect;
        if (r.Width <= 20 || r.Height <= 8) return;
        int bucket = (int)(r.Width / 40);
        if (bucket == widthBucket) return;
        widthBucket = bucket;

        CancelFilmstrip();
        frames.Clear();
        double thumbW = Math.Max(24, r.Height * AssetAspect);
        int n = Math.Max(6, Math.Min(40, (int)Math.Round(r.Width / thumbW)));
        frameCount = n;

        // Thumbnails at twice the cell size in physical pixels: sharp on high-DPI monitors.
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int pw = Math.Max(2, (int)Math.Round(thumbW * 2 * scale)) & ~1;
        int ph = Math.Max(2, (int)Math.Round(r.Height * 2 * scale)) & ~1;

        int gen = ++generation;
        double dur = duration;
        var path = AssetPath;
        var cts = new CancellationTokenSource();
        stripCts = cts;
        var times = new List<TimeSpan>(n);
        for (int i = 0; i < n; i++) times.Add(TimeSpan.FromSeconds(dur * (i + 0.5) / n));

        _ = Task.Run(async () =>
        {
            try
            {
                var (comp, _) = await VideoFrames.OpenAsync(path);
                int i = 0;
                await foreach (var f in VideoFrames.ThumbnailsAsync(comp, times, pw, ph, VideoFramePrecision.NearestKeyFrame, cts.Token))
                {
                    var bmp = VideoFrames.ToBitmapSource(f);
                    int idx = i++;
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (generation != gen) return;   // a stale strip must not leak into a new one
                        frames[idx] = bmp;
                        InvalidateVisual();
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"FrameLine filmstrip: {ex.Message}"); }
        }, cts.Token);
    }

    // MARK: rendering

    protected override void OnRender(DrawingContext dc)
    {
        var r = TrackRect;
        if (r.Width <= 0 || r.Height <= 0) return;
        var clip = new RectangleGeometry(r, 6, 6);
        if (duration <= 0) { dc.DrawGeometry(Ink, null, clip); return; }

        dc.PushClip(clip);
        dc.DrawRectangle(Ink, null, r);

        if (frameCount > 0)
        {
            double cw = r.Width / frameCount;
            for (int i = 0; i < frameCount; i++)
            {
                if (!frames.TryGetValue(i, out var img)) continue;
                DrawCover(dc, img, new Rect(r.Left + i * cw, r.Top, Math.Ceiling(cw) + 1, r.Height));
            }
        }

        double li = XFor(inTime), lo = XFor(outTime);
        var keep = new Rect(li, r.Top, Math.Max(0, lo - li), r.Height);
        // Crop-mask language: everything outside the kept range darkened, white frame around it.
        dc.DrawRectangle(Mask, null, new Rect(r.Left, r.Top, Math.Max(0, li - r.Left), r.Height));
        dc.DrawRectangle(Mask, null, new Rect(lo, r.Top, Math.Max(0, r.Right - lo), r.Height));
        dc.DrawRectangle(null, FramePen, new Rect(keep.X + 0.5, keep.Y + 0.5, Math.Max(0, keep.Width - 1), Math.Max(0, keep.Height - 1)));

        if (isExporting)
            dc.DrawRectangle(ExportWash, null, new Rect(keep.X, keep.Y, keep.Width * Math.Clamp(exportProgress, 0, 1), keep.Height));
        else
        {
            double px = XFor(playhead);
            dc.DrawLine(PlayheadPen, new Point(px, r.Top), new Point(px, r.Bottom));
            dc.DrawEllipse(HandleFill, null, new Point(px, r.Top), 3.5, 3.5);
        }
        dc.Pop();

        if (!isExporting)
        {
            DrawHandle(dc, li, r);
            DrawHandle(dc, lo, r);
        }
    }

    private static void DrawHandle(DrawingContext dc, double cx, Rect r)
    {
        const double w = 11;
        var cap = new Rect(cx - w / 2, r.Top - 3, w, r.Height + 6);
        dc.DrawRoundedRectangle(HandleFill, null, cap, 5.5, 5.5);
        double mid = cap.Top + cap.Height / 2;
        foreach (var dx in new[] { -2.0, 2.0 })
            dc.DrawLine(GripPen, new Point(cx + dx + 0.5, mid - 5), new Point(cx + dx + 0.5, mid + 5));
    }

    /// Fills the cell, centre-cropped.
    private static void DrawCover(DrawingContext dc, BitmapSource img, Rect cell)
    {
        double iw = img.PixelWidth, ih = img.PixelHeight;
        if (iw <= 0 || ih <= 0 || cell.Height <= 0) return;
        double s = Math.Max(cell.Width / iw, cell.Height / ih);
        double w = iw * s, h = ih * s;
        dc.PushClip(new RectangleGeometry(cell));
        dc.DrawImage(img, new Rect(cell.Left + (cell.Width - w) / 2, cell.Top + (cell.Height - h) / 2, w, h));
        dc.Pop();
    }

    // MARK: mouse

    private enum Drag { None, In, Out, Scrub }
    private Drag drag = Drag.None;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (duration <= 0 || isExporting) return;
        Focus();
        double px = e.GetPosition(this).X;
        double li = XFor(inTime), lo = XFor(outTime);
        // When the handles overlap, pick the one on the side of the click.
        if (Math.Abs(px - li) <= HandleHit && (Math.Abs(px - lo) > HandleHit || px <= (li + lo) / 2)) drag = Drag.In;
        else if (Math.Abs(px - lo) <= HandleHit) drag = Drag.Out;
        else drag = Drag.Scrub;
        CaptureMouse();
        EditingBegan?.Invoke();
        HandleDrag(px);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        double px = e.GetPosition(this).X;
        if (drag != Drag.None) { HandleDrag(px); return; }
        bool overHandle = duration > 0 && !isExporting &&
            (Math.Abs(px - XFor(inTime)) <= HandleHit || Math.Abs(px - XFor(outTime)) <= HandleHit);
        Cursor = overHandle ? Cursors.SizeWE : null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndDrag();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (drag == Drag.None) return;
        drag = Drag.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void HandleDrag(double px)
    {
        double t = TimeFor(px);
        switch (drag)
        {
            case Drag.In: ScrubIn?.Invoke(Math.Clamp(t, 0, Math.Max(0, outTime - MinDur))); break;
            case Drag.Out: ScrubOut?.Invoke(Math.Clamp(t, Math.Min(duration, inTime + MinDur), duration)); break;
            case Drag.Scrub: ScrubPlayhead?.Invoke(t); break;
        }
    }

    // MARK: keyboard (once focused)

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Locked during an export, like the mouse: Space would restart a player with no media and
        // I/O would move the handles mid-export.
        if (isExporting) { base.OnKeyDown(e); return; }
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Space: TogglePlay?.Invoke(); break;
            case Key.Left: Step?.Invoke(false, shift); break;
            case Key.Right: Step?.Invoke(true, shift); break;
            case Key.Enter: CommitDone?.Invoke(); break;
            case Key.I: SetIn?.Invoke(); break;     // letter VKs follow the layout: works on AZERTY
            case Key.O: SetOut?.Invoke(); break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Brush b, double w) { var p = new Pen(b, w); p.Freeze(); return p; }
}
