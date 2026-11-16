using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using static FrameLine.Capture.CaptureNative;

namespace FrameLine.Capture;

/// Region selection (port of RegionSelectOverlay.swift): dark veil over every display, cut out by the
/// selection with a white border and a "W × H" badge, and an 8× loupe with "#RRGGBB · x, y" that follows
/// the cursor while dragging. The background is the frozen screenshot, so what is cut equals what was shown.
public static class RegionSelectOverlay
{
    private static RegionSession? session;
    private static bool starting;

    /// True from Begin until the completion has run (guards re-entry and "reopen" while selecting).
    public static bool IsPresenting => session != null || starting;

    /// Lets the user pick a rectangle. `completion` gets it in PHYSICAL virtual-screen pixels, or null
    /// (Esc, right click, too small, display change, already presenting). Call and completion on the UI thread.
    public static void Begin(Action<Int32Rect?> completion) => BeginFrozen((rect, _) => completion(rect));

    /// Same, also handing over the frozen screenshot the rectangle was chosen on.
    internal static void BeginFrozen(Action<Int32Rect?, FrozenScreen?> completion)
    {
        if (IsPresenting) { completion(null, null); return; }
        starting = true;
        var dispatcher = Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        Task.Run(ScreenCapturer.Freeze).ContinueWith(t => dispatcher.BeginInvoke(() =>
        {
            starting = false;
            if (t.IsFaulted || t.IsCanceled) { completion(null, null); return; }
            Begin(t.Result, rect => completion(rect, t.Result));
        }));
    }

    /// Starts on an existing frozen screenshot (window mode switching to region keeps the same picture).
    internal static void Begin(FrozenScreen frozen, Action<Int32Rect?> completion)
    {
        if (session != null) { completion(null); return; }
        session = new RegionSession(frozen, rect =>
        {
            session = null;
            completion(rect);
        });
        session.Present();
    }
}

internal sealed class RegionSession : OverlaySession
{
    // Loupe geometry, in DIPs (same numbers as LoupeView in Swift).
    private const double Radius = 64, Diameter = Radius * 2, Zoom = 8, LabelGap = 8, ViewW = 168, ViewH = Diameter + LabelGap + 22;

    private readonly Action<Int32Rect?> completion;
    private POINT? dragStart;
    private Int32Rect selection = Int32Rect.Empty;
    private Pen? hairline, gridPen, crossPen;
    private double pensScale;
    private static readonly Pen RingPen = FrozenPen(OverlayText.White, 1.5);

    public RegionSession(FrozenScreen frozen, Action<Int32Rect?> completion) : base(frozen) => this.completion = completion;

    protected override void OnPrimaryDown()
    {
        dragStart = CursorPos;
        UpdateSelection();
    }

    protected override void OnCursorMoved()
    {
        if (dragStart != null) UpdateSelection();
    }

    protected override void OnPrimaryUp()
    {
        if (dragStart == null) return;
        UpdateSelection();
        double scale = CursorMonitor >= 0 ? Frozen.Monitors[CursorMonitor].Scale : 1;
        int min = (int)Math.Round(4 * scale);                // 4 points, as on macOS
        End();
        completion(selection.Width < min || selection.Height < min ? null : selection);
    }

    protected override void OnEnded(bool cancelled)
    {
        if (cancelled) completion(null);
    }

    private void UpdateSelection()
    {
        var s = dragStart!.Value;
        var c = CursorPos;
        var r = new Int32Rect(Math.Min(s.X, c.X), Math.Min(s.Y, c.Y), Math.Abs(c.X - s.X), Math.Abs(c.Y - s.Y));
        selection = Monitors.Intersect(r, Frozen.Bounds);
        Redraw();
    }

    protected override void Paint(int monitor, DrawingContext dc, double scale, Rect size)
    {
        EnsurePens(scale);
        var origin = Frozen.Monitors[monitor].Bounds;
        bool hasSel = dragStart != null && selection.Width >= 1 && selection.Height >= 1;
        if (!hasSel)
        {
            dc.DrawRectangle(OverlayText.Veil, null, size);   // before a selection: everything dimmed
        }
        else
        {
            var sel = new Rect(D(selection.X - origin.X, scale), D(selection.Y - origin.Y, scale),
                D(selection.Width, scale), D(selection.Height, scale));
            FillAround(dc, OverlayText.Veil, size, sel);
            var border = new Rect(sel.X + 0.5 / scale, sel.Y + 0.5 / scale,
                Math.Max(0, sel.Width - 1 / scale), Math.Max(0, sel.Height - 1 / scale));
            dc.DrawRectangle(null, hairline, border);
            if (monitor == CursorMonitor) DrawBadge(dc, scale, size, sel);
        }
        if (dragStart != null && monitor == CursorMonitor) DrawLoupe(dc, monitor, scale, size);
    }

    private void DrawBadge(DrawingContext dc, double scale, Rect bounds, Rect sel)
    {
        var text = OverlayText.Format($"{selection.Width} × {selection.Height}", OverlayText.Badge, OverlayText.BadgeSize, scale);
        const double padX = 6, padY = 3;
        double bw = text.Width + padX * 2, bh = text.Height + padY * 2;
        double x = Math.Min(Math.Max(sel.X + sel.Width / 2 - bw / 2, 2), bounds.Width - bw - 2);
        double y = sel.Bottom + 6;                                   // under the selection…
        if (y + bh > bounds.Height - 2) y = sel.Bottom - 6 - bh;      // …or inside it, at the bottom
        var badge = new Rect(Snap(x, scale), Snap(y, scale), bw, bh);
        dc.DrawRoundedRectangle(OverlayText.BadgeBack, null, badge, 4, 4);
        dc.DrawText(text, new Point(badge.X + padX, badge.Y + padY));
    }

    private void DrawLoupe(DrawingContext dc, int monitor, double scale, Rect bounds)
    {
        var origin = Frozen.Monitors[monitor].Bounds;
        int lx = CursorPos.X - origin.X, ly = CursorPos.Y - origin.Y;
        var p = new Point(lx / scale, ly / scale);

        const double gap = 16, inset = (ViewW - Diameter) / 2;
        double x = p.X + gap - inset;
        double y = p.Y + gap;                                                    // below-right of the cursor
        if (p.X + gap + Diameter > bounds.Width - 4) x = p.X - gap - Diameter - inset;
        if (y + ViewH > bounds.Height - 4) y = p.Y - gap - ViewH;
        x = Snap(Math.Min(Math.Max(x, 4), bounds.Width - ViewW - 4), scale);
        y = Snap(Math.Min(Math.Max(y, 4), bounds.Height - ViewH - 4), scale);

        var center = new Point(x + ViewW / 2, y + Radius);
        var circle = new Rect(center.X - Radius, center.Y - Radius, Diameter, Diameter);
        dc.PushClip(new EllipseGeometry(center, Radius, Radius));
        dc.DrawRectangle(OverlayText.Black, null, circle);          // outside the display stays black
        // Source pixel (lx, ly) centred in the disc, each source pixel = Zoom physical pixels.
        double cell = Zoom / scale;
        var shot = Frozen.Shots[monitor];
        dc.DrawImage(shot, new Rect(center.X - (lx + 0.5) * cell, center.Y - (ly + 0.5) * cell,
            shot.PixelWidth * cell, shot.PixelHeight * cell));
        // Pixel grid.
        int lines = (int)Math.Ceiling(Radius / cell) + 1;
        for (int k = -lines; k <= lines; k++)
        {
            double off = (k + 0.5) * cell;
            dc.DrawLine(gridPen, new Point(center.X + off, circle.Top), new Point(center.X + off, circle.Bottom));
            dc.DrawLine(gridPen, new Point(circle.Left, center.Y + off), new Point(circle.Right, center.Y + off));
        }
        dc.DrawLine(crossPen, new Point(center.X - 8, center.Y), new Point(center.X + 8, center.Y));
        dc.DrawLine(crossPen, new Point(center.X, center.Y - 8), new Point(center.X, center.Y + 8));
        dc.Pop();
        dc.DrawEllipse(null, RingPen, center, Radius, Radius);

        uint rgb = Frozen.ColorAt(CursorPos.X, CursorPos.Y) ?? 0;
        string label = string.Create(CultureInfo.InvariantCulture,
            $"#{rgb:X6} · {CursorPos.X}, {CursorPos.Y}");
        var text = OverlayText.Format(label, OverlayText.Loupe, OverlayText.LoupeSize, scale);
        const double padX = 7, padY = 3;
        double pw = text.Width + padX * 2, ph = text.Height + padY * 2;
        var pill = new Rect(Snap(center.X - pw / 2, scale), Snap(circle.Bottom + LabelGap, scale), pw, ph);
        dc.DrawRoundedRectangle(OverlayText.PillBack, null, pill, 5, 5);
        dc.DrawText(text, new Point(pill.X + padX, pill.Y + padY));
    }

    private void EnsurePens(double scale)
    {
        if (hairline != null && pensScale == scale) return;
        pensScale = scale;
        hairline = FrozenPen(OverlayText.White, 1 / scale);
        gridPen = FrozenPen(OverlayText.GridBrush, 1 / scale);
        crossPen = FrozenPen(OverlayText.CrossBrush, 1);
    }

    private static Pen FrozenPen(Brush b, double thickness)
    {
        var p = new Pen(b, thickness);
        p.Freeze();
        return p;
    }

    private static double Snap(double dip, double scale) => Math.Round(dip * scale) / scale;
}
