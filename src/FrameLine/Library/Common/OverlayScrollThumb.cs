using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FrameLine.Library;

/// Floating scroll thumb of the macOS sidebar (ScrollThumbView): a translucent pill with no
/// track, ~2× its natural length, that reserves NO layout width (the native bar is hidden, this
/// sits over the content's right margin), fades in on scroll and out 1.1 s later, and can be
/// dragged. Only the pill is hit-testable; everything else falls through to the list.
internal sealed class OverlayScrollThumb : FrameworkElement
{
    private const double BarWidth = 14, PillWidth = 9, EdgeGap = 3, TrackInset = 4, MinPill = 40;
    private static readonly Brush PillBrush = Glyphs.White(0.32);

    private readonly ScrollViewer _sv;
    private readonly DispatcherTimer _fade;
    private bool _dragging, _flashedOnce;
    private double _grabOffset;

    public OverlayScrollThumb(ScrollViewer sv)
    {
        _sv = sv;
        Width = BarWidth;
        HorizontalAlignment = HorizontalAlignment.Right;
        Opacity = 0;
        _fade = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.1) };
        _fade.Tick += (_, _) =>
        {
            _fade.Stop();
            if (_dragging) return;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(0.4)));
        };
        sv.ScrollChanged += (_, e) =>
        {
            InvalidateVisual();
            if (e.VerticalChange != 0) Show();
            else if (!_flashedOnce && CanScroll) { _flashedOnce = true; Show(); }   // one discreet flash
        };
        SizeChanged += (_, _) => InvalidateVisual();
    }

    private bool CanScroll => _sv.ExtentHeight > _sv.ViewportHeight + 0.5;

    private double Fraction()
    {
        double max = _sv.ScrollableHeight;
        return max <= 0 ? 0 : Math.Clamp(_sv.VerticalOffset / max, 0, 1);
    }

    private Rect Track() => new(0, TrackInset, ActualWidth, Math.Max(0, ActualHeight - TrackInset * 2));

    private Rect Pill()
    {
        if (!CanScroll || ActualHeight <= 0) return Rect.Empty;
        var track = Track();
        double natural = track.Height * (_sv.ViewportHeight / _sv.ExtentHeight);
        double h = Math.Min(Math.Max(natural * 2, MinPill), track.Height);
        double y = track.Top + Fraction() * (track.Height - h);
        return new Rect(ActualWidth - PillWidth - EdgeGap, y, PillWidth, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var k = Pill();
        if (k.IsEmpty || k.Height <= 1) return;
        dc.DrawRoundedRectangle(PillBrush, null, k, PillWidth / 2, PillWidth / 2);
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters p)
    {
        var k = Pill();
        if (k.IsEmpty) return null;
        k.Inflate(6, 4);
        return k.Contains(p.HitPoint) ? new PointHitTestResult(this, p.HitPoint) : null;
    }

    private void Show()
    {
        _fade.Stop();
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.12)));
        _fade.Start();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // The wheel over the pill still scrolls the content.
        _sv.ScrollToVerticalOffset(_sv.VerticalOffset - e.Delta / 120.0 * 48);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!CanScroll) return;
        _dragging = true;
        var p = e.GetPosition(this);
        var k = Pill();
        _grabOffset = k.Contains(p) ? p.Y - k.Top : k.Height / 2;
        CaptureMouse();
        Show();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging || !CanScroll) return;
        var track = Track();
        double travel = track.Height - Pill().Height;
        if (travel <= 0) return;
        double top = Math.Clamp(e.GetPosition(this).Y - _grabOffset, track.Top, track.Top + travel);
        _sv.ScrollToVerticalOffset((top - track.Top) / travel * _sv.ScrollableHeight);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        _fade.Stop();
        _fade.Start();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _dragging = false;
        base.OnLostMouseCapture(e);
    }
}
