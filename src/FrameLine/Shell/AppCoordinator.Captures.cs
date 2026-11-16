using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FrameLine.Capture;
using FrameLine.Core;
using FrameLine.Library;
using FrameLine.Platform;

namespace FrameLine.Shell;

public sealed partial class AppCoordinator
{
    /// Captures being written, and those trashed (from the floating thumbnail) before their write
    /// finished. Without the second set, a capture "deleted" during its write reappeared in the
    /// library once the file landed.
    private readonly HashSet<string> _pendingWrites = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _trashedWhileWriting = new(StringComparer.OrdinalIgnoreCase);

    // MARK: - Captures

    private void CaptureCore(CaptureMode mode, string tag)
    {
        ESLog.Snapshot(tag + " (hotkey/menu)");
        if (CaptureManager.Shared.IsBusy) return;      // one overlay at a time
        CaptureManager.Shared.Capture(mode, Handle);
    }

    /// "Capture Fullscreen in… 3/5/10 s": time to arrange the screen before the shot.
    private void CaptureFullDelayedCore(int seconds)
    {
        GlassConfirmBadge.Show(Tr.Format(Tr.L("capture_in", "Capture in %ds…"), seconds), "timer");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); CaptureFull(); };
        timer.Start();
    }

    /// Post-capture routing shared by every image mode: library, floating thumbnail (per "show
    /// quick preview"), editor (per "open the editor after capture"). The library window is never
    /// reopened by a capture.
    private void Handle(string? path)
    {
        if (path == null) return;
        ESLog.Snapshot($"handle (showPreview={Preferences.Shared.ShowPreview} openEditor={Preferences.Shared.OpenEditor})");
        Notifier.NotifyCapture(path, isVideo: false);
        CaptureLibrary.Shared.Reload();
        if (Preferences.Shared.ShowPreview) CaptureThumbnail.Show(path);
        if (Preferences.Shared.OpenEditor) Annotate(path);
    }

    /// A capture triggered by an MCP client: library + thumbnail (per "show quick preview"), but
    /// never the automatic editor (the open_in_editor tool exists for that).
    public void RouteAutomationCapture(string path)
    {
        CaptureLibrary.Shared.Reload();
        if (Preferences.Shared.ShowPreview) CaptureThumbnail.Show(path);
    }

    // MARK: - Pending writes (for a capture module that shows the thumbnail before the file exists)

    /// Declares that `path` is being written; Trash(path) is deferred until EndPendingWrite.
    public void BeginPendingWrite(string path) => _pendingWrites.Add(path);

    /// The write of `path` finished. Returns false when the capture must NOT be routed further:
    /// it was trashed during the write (the file is now recycled), or the write failed.
    public bool EndPendingWrite(string path, bool ok)
    {
        _pendingWrites.Remove(path);
        if (_trashedWhileWriting.Remove(path))
        {
            if (ok) RecycleBin.Send(path);
            CaptureLibrary.Shared.Reload();
            return false;
        }
        return ok;
    }

    /// Port of handleCapturedImage: an image captured in memory. Copy-to-clipboard mode sends it
    /// ONLY to the clipboard (no file, no library, no notification); otherwise the thumbnail appears
    /// at once from memory while the PNG/JPEG is encoded and written off the UI thread.
    public void HandleCapturedImage(BitmapSource image)
    {
        ESLog.Snapshot($"handleCapturedImage (copyToClipboard={Preferences.Shared.CopyToClipboard})");
        CaptureSound.PlayCapture();
        if (!image.IsFrozen) image.Freeze();
        if (Preferences.Shared.CopyToClipboard)
        {
            ClipboardImage.SetImage(image);
            GlassConfirmBadge.Show(Tr.L("toast_copied", "Copié"));
            return;
        }
        bool jpg = Preferences.Shared.ImageFormat == "jpg";
        var path = Preferences.Shared.NewCapturePath(jpg ? "jpg" : "png");
        BeginPendingWrite(path);
        if (Preferences.Shared.ShowPreview) CaptureThumbnail.Show(path);
        Task.Run(() => WriteImage(image, path, jpg)).ContinueWith(t =>
        {
            bool ok = t.Status == TaskStatus.RanToCompletion && t.Result;
            if (!EndPendingWrite(path, ok))
            {
                if (!ok && !File.Exists(path))
                {
                    // [SEC] file name only in the log, never the save folder.
                    ESLog.Log("capture write failed (" + Path.GetFileName(path) + ")");
                    GlassConfirmBadge.Show(Tr.L("toast_save_failed", "Échec de l'enregistrement"), "exclamationmark.triangle.fill");
                }
                return;
            }
            Notifier.NotifyCapture(path, isVideo: false);
            CaptureLibrary.Shared.Reload();
            if (Preferences.Shared.OpenEditor) Annotate(path);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// Encodes and writes atomically (temp file + rename): never a half-written file in the folder.
    internal static bool WriteImage(BitmapSource image, string path, bool jpg)
    {
        var tmp = path + ".tmp";
        try
        {
            BitmapEncoder enc = jpg ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
            BitmapSource frame = image;
            if (jpg && image.Format.BitsPerPixel == 32)
                frame = new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgr24, null, 0);
            enc.Frames.Add(BitmapFrame.Create(frame));
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                enc.Save(fs);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            try { File.Delete(tmp); } catch { }
            return false;
        }
    }
}
