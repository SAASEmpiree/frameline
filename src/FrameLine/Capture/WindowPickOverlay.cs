using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FrameLine.Capture;

/// Window picking, the Windows stand-in for `screencapture -i -w` (visual spec from the macOS
/// CaptureWindowOverlay): the screen is dimmed except the window under the cursor, which gets an accent
/// border and a "Title — App" pill. Click captures it, Esc or right click cancels, a click on the
/// desktop is ignored.
internal sealed class WindowPickSession : OverlaySession
{
    private readonly IReadOnlyList<CaptureWindowInfo> candidates;
    private readonly Action<CaptureWindowInfo?> completion;
    private readonly Pen accentPen;
    private CaptureWindowInfo? hovered, pressed;

    public WindowPickSession(FrozenScreen frozen, IReadOnlyList<CaptureWindowInfo> candidates,
        Action<CaptureWindowInfo?> completion) : base(frozen)
    {
        this.candidates = candidates;
        this.completion = completion;
        accentPen = new Pen(AccentBrush(), 2);
        accentPen.Freeze();
    }

    protected override Cursor OverlayCursor => Cursors.Arrow;
    // Belt and braces: if a capture falls back to a live blit, the overlay must not be in it.
    protected override bool ExcludeFromCapture => true;

    protected override void OnCursorMoved()
    {
        CaptureWindowInfo? hit = null;
        foreach (var c in candidates)
        {
            var b = c.Bounds;
            if (CursorPos.X >= b.X && CursorPos.Y >= b.Y && CursorPos.X < b.X + b.Width && CursorPos.Y < b.Y + b.Height)
            {
                hit = c;
                break;
            }
        }
        if (hit?.Handle == hovered?.Handle) return;   // no change, no redraw
        hovered = hit;
        Redraw();
    }

    // Captured on release, so the button-up never reaches the window underneath once the overlay is gone.
    protected override void OnPrimaryDown()
    {
        OnCursorMoved();
        pressed = hovered;
    }

    protected override void OnPrimaryUp()
    {
        OnCursorMoved();
        var target = pressed;
        pressed = null;
        if (target == null || hovered?.Handle != target.Handle) return;   // click in the void: Esc stays the way out
        End();
        completion(target);
    }

    protected override void OnEnded(bool cancelled)
    {
        if (cancelled) completion(null);
    }

    protected override void Paint(int monitor, DrawingContext dc, double scale, Rect size)
    {
        if (hovered == null) { dc.DrawRectangle(OverlayText.WindowVeil, null, size); return; }
        var origin = Frozen.Monitors[monitor].Bounds;
        var b = hovered.Bounds;
        var hole = new Rect(D(b.X - origin.X, scale), D(b.Y - origin.Y, scale), D(b.Width, scale), D(b.Height, scale));
        FillAround(dc, OverlayText.WindowVeil, size, hole);

        var inner = new Rect(hole.X + 1, hole.Y + 1, Math.Max(0, hole.Width - 2), Math.Max(0, hole.Height - 2));
        dc.DrawRectangle(null, accentPen, inner);

        // The pill only on displays that show part of the hovered window.
        var visible = hole;
        visible.Intersect(size);
        if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return;
        var label = hovered.Label;
        if (label.Length > 0) DrawLabel(dc, scale, size, hole, label);
    }

    private static void DrawLabel(DrawingContext dc, double scale, Rect bounds, Rect hole, string label)
    {
        const double padX = 10, padY = 5;
        var text = OverlayText.Format(label, OverlayText.WindowLabel, OverlayText.WindowLabelSize, scale);
        text.MaxLineCount = 1;
        text.Trimming = TextTrimming.CharacterEllipsis;
        text.MaxTextWidth = Math.Max(1, bounds.Width - 16 - padX * 2);
        double pw = Math.Min(text.Width + padX * 2, bounds.Width - 16), ph = text.Height + padY * 2;
        double x = Math.Min(Math.Max(hole.X + hole.Width / 2 - pw / 2, 8), bounds.Width - pw - 8);
        double y = hole.Bottom + 8;                                            // below the frame…
        if (y + ph > bounds.Height - 8) y = Math.Max(hole.Bottom - 8 - ph, 8);  // …or inside it, at the bottom
        var pill = new Rect(Math.Round(x * scale) / scale, Math.Round(y * scale) / scale, pw, ph);
        dc.DrawRoundedRectangle(OverlayText.LabelBack, null, pill, 7, 7);
        dc.DrawText(text, new Point(pill.X + padX, pill.Y + padY));
    }

    /// The user's Windows accent colour (macOS used controlAccentColor), Windows blue if unavailable.
    private static Brush AccentBrush()
    {
        Color c = Color.FromRgb(0x00, 0x78, 0xD4);
        try
        {
            var ui = new global::Windows.UI.ViewManagement.UISettings();
            var a = ui.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Accent);
            c = Color.FromArgb(255, a.R, a.G, a.B);
        }
        catch { }
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}
