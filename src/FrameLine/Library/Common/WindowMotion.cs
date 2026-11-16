using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static FrameLine.Library.LibNative;

namespace FrameLine.Library;

/// Moves and animates top-level windows in physical pixels (SetWindowPos), so a borderless panel
/// lands exactly on a monitor corner whatever the DPI mix or the sign of the coordinates.
internal static class WindowMotion
{
    private sealed class Anim
    {
        public int FromX, FromY, ToX, ToY;
        public double FromOpacity, ToOpacity;
        public double Duration;
        public CubicBezier Ease = CubicBezier.EaseOut;
        public Stopwatch Clock = new();
        public Action? Completed;
    }

    private static readonly ConditionalWeakTable<Window, Anim> Running = new();
    private static bool _hooked;

    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).EnsureHandle();

    public static bool TryGetRect(Window w, out RECT r)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        r = default;
        return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out r);
    }

    /// Instant move to a physical origin. Done twice when the destination may have another DPI:
    /// the first move triggers WM_DPICHANGED (WPF rescales and may shift the window to Windows'
    /// suggested rect), the second one pins the exact origin.
    public static void MoveTo(Window w, int x, int y)
    {
        Stop(w);
        var hwnd = Handle(w);
        for (int pass = 0; pass < 2; pass++)
        {
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            if (GetWindowRect(hwnd, out var r) && r.Left == x && r.Top == y) break;
        }
    }

    /// Physical size of the window once it sits on a monitor of the given scale.
    public static (int W, int H) PhysicalSize(Window w, MonitorRef m) => (m.Px(w.Width), m.Px(w.Height));

    public static void Animate(Window w, int toX, int toY, double toOpacity, double seconds,
                               CubicBezier ease, Action? completed = null)
    {
        var hwnd = Handle(w);
        GetWindowRect(hwnd, out var r);
        Stop(w);
        var a = new Anim
        {
            FromX = r.Left, FromY = r.Top, ToX = toX, ToY = toY,
            FromOpacity = w.Opacity, ToOpacity = toOpacity,
            Duration = Math.Max(0.001, seconds), Ease = ease, Completed = completed,
        };
        a.Clock.Start();
        Running.Add(w, a);
        if (!_hooked) { CompositionTarget.Rendering += OnFrame; _hooked = true; }
        foreach (var weak in Tracked)
            if (weak.TryGetTarget(out var t) && ReferenceEquals(t, w)) return;
        Tracked.Add(new WeakReference<Window>(w));
    }

    public static void Stop(Window w)
    {
        Running.Remove(w);
    }

    public static bool IsAnimating(Window w) => Running.TryGetValue(w, out _);

    private static readonly System.Collections.Generic.List<WeakReference<Window>> Tracked = new();

    private static void OnFrame(object? sender, EventArgs e)
    {
        for (int i = Tracked.Count - 1; i >= 0; i--)
        {
            if (!Tracked[i].TryGetTarget(out var w) || !Running.TryGetValue(w, out var a))
            {
                Tracked.RemoveAt(i);
                continue;
            }
            double t = Math.Min(1, a.Clock.Elapsed.TotalSeconds / a.Duration);
            double k = a.Ease.Evaluate(t);
            int x = (int)Math.Round(a.FromX + (a.ToX - a.FromX) * k);
            int y = (int)Math.Round(a.FromY + (a.ToY - a.FromY) * k);
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd != IntPtr.Zero)
                SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            w.Opacity = a.FromOpacity + (a.ToOpacity - a.FromOpacity) * k;
            if (t >= 1)
            {
                Running.Remove(w);
                Tracked.RemoveAt(i);
                // A DPI boundary crossed mid-flight can leave Windows' suggested origin: pin the end.
                if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r) && (r.Left != a.ToX || r.Top != a.ToY))
                    SetWindowPos(hwnd, IntPtr.Zero, a.ToX, a.ToY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                a.Completed?.Invoke();
            }
        }
        if (Tracked.Count == 0 && _hooked)
        {
            CompositionTarget.Rendering -= OnFrame;
            _hooked = false;
        }
    }
}

/// CSS / CAMediaTimingFunction cubic-bezier easing (x1, y1, x2, y2), solved by Newton + bisection.
internal sealed class CubicBezier : IEasingFunction
{
    public static readonly CubicBezier EaseOut = new(0, 0, 0.58, 1);
    public static readonly CubicBezier EaseIn = new(0.42, 0, 1, 1);
    public static readonly CubicBezier EaseInOut = new(0.42, 0, 0.58, 1);

    private readonly double _x1, _y1, _x2, _y2;
    public CubicBezier(double x1, double y1, double x2, double y2) { _x1 = x1; _y1 = y1; _x2 = x2; _y2 = y2; }

    double IEasingFunction.Ease(double normalizedTime) => Evaluate(normalizedTime);

    public double Evaluate(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double t = x;
        for (int i = 0; i < 8; i++)
        {
            double err = Sample(_x1, _x2, t) - x;
            if (Math.Abs(err) < 1e-5) return Sample(_y1, _y2, t);
            double d = Deriv(_x1, _x2, t);
            if (Math.Abs(d) < 1e-6) break;
            t -= err / d;
        }
        double lo = 0, hi = 1;
        t = x;
        for (int i = 0; i < 30; i++)
        {
            double v = Sample(_x1, _x2, t);
            if (Math.Abs(v - x) < 1e-5) break;
            if (v < x) lo = t; else hi = t;
            t = (lo + hi) / 2;
        }
        return Sample(_y1, _y2, t);
    }

    private static double Sample(double a, double b, double t) =>
        ((1 - 3 * b + 3 * a) * t + (3 * b - 6 * a)) * t * t + 3 * a * t;

    private static double Deriv(double a, double b, double t) =>
        3 * (1 - 3 * b + 3 * a) * t * t + 2 * (3 * b - 6 * a) * t + 3 * a;
}
