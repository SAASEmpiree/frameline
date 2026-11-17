using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using static FrameLine.Capture.CaptureNative;

namespace FrameLine.Capture;

/// Shared mechanics of the selection overlays: one frozen-screenshot window per display, input read by
/// POLLING the hardware state every frame (cursor, buttons, Esc) rather than from window events —
/// the macOS overlay switched to polling after event delivery to a non-activated panel dropped drags,
/// and polling also keeps Esc working when Windows refuses to give the overlay the foreground.
/// Esc or a right click cancels; a display change cancels; focus goes back to the previous window.
internal abstract class OverlaySession
{
    protected readonly FrozenScreen Frozen;
    protected readonly List<OverlayWindow> Windows = new();
    private readonly string layout;
    private readonly IntPtr previousForeground;
    private bool armed, escWasDown, secondaryWasDown, primaryWasDown;
    private POINT lastCursor = new() { X = int.MinValue };

    protected bool Finished { get; private set; }
    protected POINT CursorPos { get; private set; }
    /// Index (in Frozen.Monitors) of the display under the cursor, -1 in a layout gap.
    protected int CursorMonitor { get; private set; } = -1;

    protected OverlaySession(FrozenScreen frozen)
    {
        Frozen = frozen;
        layout = Monitors.LayoutSignature(frozen.Monitors);
        previousForeground = GetForegroundWindow();
    }

    protected virtual Cursor OverlayCursor => Cursors.Cross;
    protected virtual bool ExcludeFromCapture => false;

    public void Present()
    {
        if (Frozen.Monitors.Count == 0) { End(cancelled: true); return; }
        for (int i = 0; i < Frozen.Monitors.Count; i++)
        {
            var m = Frozen.Monitors[i];
            int index = i;
            var w = new OverlayWindow(m.Bounds, m.Scale, excludeFromCapture: ExcludeFromCapture) { Cursor = OverlayCursor };
            w.Surface.Paint = (dc, s) =>
            {
                var size = new Rect(0, 0, w.Surface.PhysicalWidth / s, w.Surface.PhysicalHeight / s);
                dc.DrawImage(Frozen.Shots[index], size);   // exactly 1:1 with the screen pixels
                Paint(index, dc, s, size);
            };
            w.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { e.Handled = true; Cancel(); }
            };
            w.DisplaysChanged += OnDisplaysChanged;
            Windows.Add(w);
        }

        GetCursorPos(out var p);
        escWasDown = IsKeyDown(VK_ESCAPE);
        secondaryWasDown = IsKeyDown(SecondaryButtonVk);
        // A press still held when the overlay opens (menu click, hotkey with the mouse down) is not a selection.
        armed = !IsKeyDown(PrimaryButtonVk);
        UpdateCursor(p);
        OnCursorMoved();

        int keyIndex = Math.Max(0, CursorMonitor);
        foreach (var w in Windows) w.Show();
        Windows[keyIndex].TakeFocus();
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (Finished) return;
        GetCursorPos(out var p);
        bool esc = IsKeyDown(VK_ESCAPE);
        bool secondary = IsKeyDown(SecondaryButtonVk);
        bool primary = IsKeyDown(PrimaryButtonVk);

        if (esc && !escWasDown) { Cancel(); return; }
        escWasDown = esc;
        if (secondary && !secondaryWasDown) { Cancel(); return; }
        secondaryWasDown = secondary;

        bool moved = p.X != lastCursor.X || p.Y != lastCursor.Y;
        if (moved) UpdateCursor(p);
        if (!armed)
        {
            if (!primary) armed = true;
            else { if (moved) OnCursorMoved(); return; }
        }
        if (primary && !primaryWasDown) OnPrimaryDown();
        else if (!primary && primaryWasDown) OnPrimaryUp();
        else if (moved) OnCursorMoved();
        primaryWasDown = primary;
        if (!Finished) OnFrameEnd(moved);
    }

    private void UpdateCursor(POINT p)
    {
        lastCursor = p;
        CursorPos = p;
        CursorMonitor = Frozen.IndexAt(p.X, p.Y);
    }

    private void OnDisplaysChanged()
    {
        if (Finished) return;
        if (Monitors.LayoutSignature(Monitors.All()) != layout) Cancel();   // drawn on the wrong display otherwise
    }

    protected abstract void Paint(int monitor, DrawingContext dc, double scale, Rect size);
    protected abstract void OnPrimaryDown();
    protected abstract void OnPrimaryUp();
    protected abstract void OnCursorMoved();
    protected virtual void OnFrameEnd(bool moved) { }
    protected abstract void OnEnded(bool cancelled);

    protected void Cancel() => End(cancelled: true);

    protected void Redraw()
    {
        foreach (var w in Windows) w.Surface.InvalidateVisual();
    }

    /// Removes the overlays (synchronously, before any capture runs) and hands focus back.
    protected void End(bool cancelled = false)
    {
        if (Finished) return;
        Finished = true;
        CompositionTarget.Rendering -= OnFrame;
        foreach (var w in Windows)
        {
            w.DisplaysChanged -= OnDisplaysChanged;
            w.Close();
        }
        Windows.Clear();
        var fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out uint fgPid);
        if (previousForeground != IntPtr.Zero && IsWindow(previousForeground)
            && (fg == IntPtr.Zero || fgPid == (uint)Environment.ProcessId))
            SetForegroundWindow(previousForeground);
        OnEnded(cancelled);
    }

    // ---- small drawing helpers shared by the overlays ----

    /// Physical px → DIP, snapped to the physical pixel grid.
    protected static double D(double physical, double scale) => Math.Round(physical) / scale;

    protected static void FillAround(DrawingContext dc, Brush brush, Rect bounds, Rect hole)
    {
        hole.Intersect(bounds);
        if (hole.IsEmpty || hole.Width <= 0 || hole.Height <= 0) { dc.DrawRectangle(brush, null, bounds); return; }
        dc.DrawRectangle(brush, null, new Rect(0, 0, bounds.Width, hole.Top));
        dc.DrawRectangle(brush, null, new Rect(0, hole.Bottom, bounds.Width, Math.Max(0, bounds.Height - hole.Bottom)));
        dc.DrawRectangle(brush, null, new Rect(0, hole.Top, hole.Left, hole.Height));
        dc.DrawRectangle(brush, null, new Rect(hole.Right, hole.Top, Math.Max(0, bounds.Width - hole.Right), hole.Height));
    }
}
