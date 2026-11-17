using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FrameLine.Core;

namespace FrameLine.Capture;

/// Capture orchestration (port of CaptureManager.swift): picks the pixels for a mode, plays the shutter,
/// then either puts the image on the clipboard ONLY (the "copy to clipboard" preference, like the macOS
/// app: no file) or writes it to the capture folder under a fresh name.
/// Interactive entry points (Capture, CaptureFullDelayed) honour that preference; automation entry points
/// (CaptureAsync and the MCP helpers) always write a file, because the caller needs a path.
public sealed class CaptureManager
{
    public static CaptureManager Shared { get; } = new();

    // Lets the tray menu finish closing before the screen is read (Swift waited 0.15 s for interactive
    // modes and 0.08 s before a full-screen shot).
    private const int InteractiveSettleMs = 150, FullScreenSettleMs = 80;

    private int inFlight;          // UI thread only
    private bool overlayOpen;      // UI thread only

    private static Dispatcher UI => Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    public bool IsBusy => inFlight > 0 || overlayOpen || RegionSelectOverlay.IsPresenting;

    /// Runs the interactive capture, saves the file (Preferences format/name), plays the sound,
    /// copies to clipboard if enabled. Callback gets the saved path, or null if cancelled (or clipboard
    /// only, or failed: failures show a toast). UI thread.
    public void Capture(CaptureMode mode, Action<string?> done)
    {
        if (!UI.CheckAccess()) { UI.BeginInvoke(() => Capture(mode, done)); return; }
        _ = Complete(HidingThumbnails(() => RunAsync(mode, automation: false)), done);
    }

    /// "Capture Fullscreen in… 3/5/10 s": toast, wait, then a full-screen capture of the display under
    /// the cursor at that moment.
    public void CaptureFullDelayed(int seconds, Action<string?> done)
    {
        if (!UI.CheckAccess()) { UI.BeginInvoke(() => CaptureFullDelayed(seconds, done)); return; }
        seconds = Math.Max(0, seconds);
        CaptureToast.Show(CaptureSound.Tr("toast_capture_in", "Capture in %ds…").Replace("%d", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            CaptureToast.Timer);
        _ = Complete(DelayThen(seconds, () => HidingThumbnails(() => RunAsync(CaptureMode.FullScreen, automation: false))), done);
    }

    /// Automation capture (MCP): always to a file, never clipboard-only. Region / Window still let the
    /// user choose. Safe to call from any thread and to await from the UI thread (nothing blocks it).
    public Task<string?> CaptureAsync(CaptureMode mode, int delaySeconds = 0) =>
        OnUi(() => DelayThen(delaySeconds, () => HidingThumbnails(() => RunAsync(mode, automation: true))));

    // ---- non-interactive helpers for the MCP tools ----

    public IReadOnlyList<CaptureWindowInfo> ListWindows() => WindowCapture.List();
    public IReadOnlyList<MonitorInfo> ListDisplays() => Monitors.All();

    /// 1-based index (1 = primary, as `screencapture -D`) of the display under the cursor.
    public int DisplayIndexUnderCursor()
    {
        var all = Monitors.All();
        var m = Monitors.UnderCursor();
        for (int i = 0; i < all.Count; i++) if (all[i].Handle == m.Handle) return i + 1;
        return 1;
    }

    /// Full screen of display `displayIndex` (1-based, see ListDisplays) or of the one under the cursor.
    /// Null if the index is out of range or the capture failed.
    public Task<string?> CaptureDisplayAsync(int? displayIndex, int delaySeconds = 0) =>
        OnUi(() => DelayThen(delaySeconds, () => HidingThumbnails(async () =>
        {
            var all = Monitors.All();
            if (displayIndex is int i && (i < 1 || i > all.Count)) return null;
            var m = displayIndex is int j ? all[j - 1] : Monitors.UnderCursor();
            return await Produce(() => GrabDisplay(m), toClipboard: false, reportFailure: true);
        })));

    /// One window by handle (see ListWindows), isolated from what overlaps it.
    public Task<string?> CaptureWindowAsync(IntPtr hwnd, int delaySeconds = 0) =>
        OnUi(() => DelayThen(delaySeconds, () => HidingThumbnails(() =>
            Produce(() => WindowCapture.Capture(hwnd, null, Preferences.Shared.WindowShadow), false, true))));

    /// A rectangle in physical virtual-screen pixels (clipped to the displays).
    public Task<string?> CaptureRegionAsync(Int32Rect physicalRect, int delaySeconds = 0) =>
        OnUi(() => DelayThen(delaySeconds, () => HidingThumbnails(() => Produce(() =>
        {
            var r = Monitors.Intersect(physicalRect, Monitors.VirtualBounds(Monitors.All()));
            return r.IsEmpty ? null : ScreenCapturer.CaptureRect(r, Preferences.Shared.CaptureCursor);
        }, false, true))));

    // ---- pipeline ----

    private async Task<string?> RunAsync(CaptureMode mode, bool automation)
    {
        var prefs = Preferences.Shared;
        bool toClipboard = !automation && prefs.CopyToClipboard;
        switch (mode)
        {
            case CaptureMode.FullScreen:
                if (!automation) await Task.Delay(FullScreenSettleMs);
                var monitor = Monitors.UnderCursor();    // the display the user is on, not always the primary
                return await Produce(() => GrabDisplay(monitor), toClipboard, reportFailure: true);

            case CaptureMode.Region:
            {
                if (overlayOpen || RegionSelectOverlay.IsPresenting) return null;
                overlayOpen = true;
                try
                {
                    if (!automation) await Task.Delay(InteractiveSettleMs);
                    var tcs = new TaskCompletionSource<(Int32Rect?, FrozenScreen?)>(TaskCreationOptions.RunContinuationsAsynchronously);
                    RegionSelectOverlay.BeginFrozen((r, f) => tcs.TrySetResult((r, f)));
                    var (rect, frozen) = await tcs.Task;
                    if (rect is not Int32Rect sel || frozen == null) return null;   // cancelled
                    int at = frozen.IndexAt(sel.X + sel.Width / 2, sel.Y + sel.Height / 2);
                    double dpi = 96 * (at >= 0 ? frozen.Monitors[at].Scale : 1);
                    return await Produce(() => frozen.Crop(sel, dpi), toClipboard, reportFailure: true);
                }
                finally { overlayOpen = false; }
            }

            case CaptureMode.Window:
            {
                if (overlayOpen || RegionSelectOverlay.IsPresenting) return null;
                overlayOpen = true;
                try
                {
                    if (!automation) await Task.Delay(InteractiveSettleMs);
                    FrozenScreen frozen;
                    IReadOnlyList<CaptureWindowInfo> windows;
                    try
                    {
                        windows = await Task.Run(WindowCapture.List);   // z-order before our overlays exist
                        frozen = await Task.Run(ScreenCapturer.Freeze);
                    }
                    catch (Exception e) when (e is InvalidOperationException or ArgumentException or ExternalException)
                    {
                        ShowFailure();
                        return null;
                    }
                    if (windows.Count == 0) return null;               // nothing selectable
                    var tcs = new TaskCompletionSource<CaptureWindowInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    new WindowPickSession(frozen, windows, w => tcs.TrySetResult(w)).Present();
                    var pick = await tcs.Task;
                    if (pick == null) return null;
                    bool shadow = prefs.WindowShadow;
                    return await Produce(() => WindowCapture.Capture(pick.Handle, frozen, shadow), toClipboard, reportFailure: true);
                }
                finally { overlayOpen = false; }
            }
        }
        return null;
    }

    private static BitmapSource GrabDisplay(MonitorInfo m)
    {
        var px = ScreenCapturer.GrabPixels(m.Bounds, Preferences.Shared.CaptureCursor);
        return ScreenCapturer.ToBitmap(px, m.Bounds.Width, m.Bounds.Height, System.Windows.Media.PixelFormats.Bgr32, 96 * m.Scale);
    }

    /// Grabs off the UI thread, then shutter + clipboard or file. UI thread.
    private async Task<string?> Produce(Func<BitmapSource?> grab, bool toClipboard, bool reportFailure)
    {
        inFlight++;
        try
        {
            BitmapSource? image;
            try { image = await Task.Run(grab); }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or ExternalException or OverflowException)
            {
                image = null;
            }
            if (image == null)
            {
                if (reportFailure) ShowFailure();
                return null;
            }

            // Shutter when the pixels are taken, not after the PNG encode (a 4K encode is audible lag).
            CaptureSound.PlayCapture();
            if (toClipboard)
            {
                await CopyToClipboard(image);
                return null;
            }

            string? tmp = null;
            try
            {
                var dir = Preferences.Shared.EnsureSaveDirectory();
                bool jpg = Preferences.Shared.ImageFormat == "jpg";
                tmp = await Task.Run(() => EncodeToTemp(image, dir, jpg));
                // Final name drawn HERE, on the UI thread: captures complete one after another, each one sees
                // the previous file and gets "(2)" instead of overwriting it.
                var path = MoveIntoPlace(tmp, jpg ? "jpg" : "png");
                if (path != null) return path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                                         or ArgumentException or InvalidOperationException or ExternalException)
            {
                // File name only, never the save folder (parity with the macOS log hygiene).
                System.Diagnostics.Debug.WriteLine($"FrameLine: saving the capture failed: {e.GetType().Name}");
            }
            finally
            {
                if (tmp != null) TryDelete(tmp);
            }
            CaptureToast.Show(CaptureSound.Tr("toast_save_failed", "Save failed"), CaptureToast.Warning);
            return null;
        }
        finally { inFlight--; }
    }

    private static void ShowFailure() =>
        CaptureToast.Show(CaptureSound.Tr("toast_capture_failed", "Capture failed"), CaptureToast.Warning);

    /// Encodes into a hidden temp file in the destination folder, so the final step is a same-volume rename
    /// and no half-written capture is ever visible under its real name.
    private static string EncodeToTemp(BitmapSource image, string dir, bool jpg)
    {
        if (string.IsNullOrEmpty(dir)) throw new IOException("No capture folder.");
        var tmp = Path.Combine(dir, $".FrameLine-{Guid.NewGuid():N}.tmp");
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            ScreenCapturer.Encode(image, fs, jpg);
        }
        try { File.SetAttributes(tmp, FileAttributes.Hidden); } catch (IOException) { }
        return tmp;
    }

    /// Renames the temp file to a free "FrameLine … .png" name; retries if the name was taken meanwhile.
    private static string? MoveIntoPlace(string tmp, string ext)
    {
        for (int i = 0; i < 10; i++)
        {
            var dest = Preferences.Shared.NewCapturePath(ext);
            try
            {
                File.Move(tmp, dest, overwrite: false);
                try { File.SetAttributes(dest, FileAttributes.Normal); } catch (IOException) { }
                return dest;
            }
            catch (IOException) when (File.Exists(dest))
            {
                continue;
            }
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// Bitmap (for every app) plus "PNG" (keeps transparency for apps that read it), like the PNG
    /// pasteboard data of the macOS app. The clipboard can be briefly locked by another process: retry.
    private static async Task CopyToClipboard(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(ScreenCapturer.FlattenIfAlpha(image));
        byte[] png = await Task.Run(() => ScreenCapturer.EncodePng(image));
        data.SetData("PNG", new MemoryStream(png), false);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return;
            }
            catch (ExternalException)
            {
                await Task.Delay(60);
            }
        }
        ShowFailure();
    }

    // ---- plumbing ----

    /// The floating thumbnail stack goes off screen for the whole capture, so its cards are never in
    /// the shot (CaptureThumbnailController.begin/endCaptureSession). When cards were actually
    /// hidden, DWM needs a frame or two before the desktop no longer shows them.
    private static async Task<string?> HidingThumbnails(Func<Task<string?>> work)
    {
        bool hid = false;
        try { hid = Library.CaptureThumbnail.BeginCaptureSession(); }
        catch (Exception e) when (e is InvalidOperationException) { }
        try
        {
            if (hid) await Task.Delay(50);
            return await work();
        }
        finally { Library.CaptureThumbnail.EndCaptureSession(); }
    }

    private static async Task<string?> DelayThen(int seconds, Func<Task<string?>> next)
    {
        if (seconds > 0) await Task.Delay(TimeSpan.FromSeconds(seconds));
        return await next();
    }

    private static Task<string?> OnUi(Func<Task<string?>> work) =>
        UI.CheckAccess() ? work() : UI.InvokeAsync(work).Task.Unwrap();

    private static async Task Complete(Task<string?> work, Action<string?> done)
    {
        string? path = null;
        try { path = await work; }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"FrameLine: capture failed: {e}");
            ShowFailure();
        }
        done(path);
    }
}
