using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameLine.Capture;
using FrameLine.Core;
using FrameLine.Core.Mcp;
using FrameLine.Editor;
using FrameLine.Library;
using FrameLine.Platform;
using FrameLine.Recording;

namespace FrameLine.Shell;

/// The app side of the MCP tools (FrameLineMCPTools.swift's handlers): every member drives an
/// EXISTING FrameLine feature. The server calls from thread-pool threads, so anything touching WPF
/// is marshalled to the dispatcher; image encoding stays off it.
public sealed class McpHost : IFrameLineMcpHost
{
    public static McpHost Shared { get; } = new();

    private static Task<T> OnUi<T>(Func<T> f) => Application.Current.Dispatcher.InvokeAsync(f).Task;
    private static Task<T> OnUi<T>(Func<Task<T>> f) => Application.Current.Dispatcher.InvokeAsync(f).Task.Unwrap();

    // MARK: - Interactive capture / recording (original contract)

    public async Task<string?> TakeScreenshotAsync(string mode, int? delaySeconds, CancellationToken ct)
    {
        var captureMode = mode switch
        {
            "area" => CaptureMode.Region,
            "window" => CaptureMode.Window,
            _ => CaptureMode.FullScreen,
        };
        if (delaySeconds is int d && d > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Min(d, 10)), ct).ConfigureAwait(false);
        var path = await OnUi(() => CaptureManager.Shared.CaptureAsync(captureMode)).ConfigureAwait(false);
        if (path != null) await OnUi(() => { AppCoordinator.Shared.RouteAutomationCapture(path); return true; }).ConfigureAwait(false);
        return path;
    }

    public Task<string?> StartRecordingAsync(CancellationToken ct) => OnUi(() => RecordingManager.Shared.StartAsync());

    public Task<string?> StopRecordingAsync(CancellationToken ct) => OnUi(() => RecordingManager.Shared.StopAsync());

    public bool IsRecording => RecordingManager.Shared.IsRecording;

    public bool IsRecordingStarting => RecordingManager.Shared.IsStarting;

    public string? CurrentRecordingPath => RecordingManager.Shared.CurrentPath;

    /// A rectangle in physical virtual-screen pixels, cropped out of the monitor holding its centre.
    public Task<string?> StartRegionRecordingAsync(McpRect region, CancellationToken ct) =>
        Clip(region) is Int32Rect rect
            ? OnUi(() => RecordingManager.Shared.StartRegionAsync(rect))
            : Task.FromResult<string?>(null);

    public async Task<string> RecognizeTextAsync(string path, CancellationToken ct) =>
        await TextRecognizer.RecognizeAsync(path).ConfigureAwait(false) ?? "";

    public Task CopyToClipboardAsync(string path, CancellationToken ct) =>
        OnUi(() => { AppCoordinator.Shared.CopyImage(path); return true; });

    public Task PinAsync(string path, CancellationToken ct) =>
        OnUi(() => { PinnedShotWindow.Show(path); return true; });

    // MARK: - Environment

    public string AppVersion => AppPaths.Version;

    public string CaptureFolder => Preferences.Shared.EnsureSaveDirectory();

    public string ImageFormat => Preferences.Shared.ImageFormat == "jpg" ? "jpg" : "png";

    public IReadOnlyList<McpRect> Displays => McpNative.Displays();

    public int? DisplayIndexUnderCursor() => McpNative.DisplayIndexUnderCursor();

    public IReadOnlyList<McpWindowInfo> ListWindows() => McpNative.ListWindows();

    public int? ForegroundProcessId => McpNative.ForegroundProcessId();

    // MARK: - Non-interactive capture (take_screenshot)

    /// Through CaptureManager: the grab and the PNG/JPEG encode run off the UI thread, PrintWindow of a
    /// hung app is bounded, the window shadow and cursor preferences apply, the shutter plays, and the
    /// file is always written (the copy-to-clipboard preference is ignored, like captureForAutomation).
    public async Task<string?> CaptureAsync(McpCaptureTarget target, CancellationToken ct)
    {
        Task<string?> work;
        switch (target.Kind)
        {
            case McpCaptureKind.Window:
                work = CaptureManager.Shared.CaptureWindowAsync(new IntPtr(target.WindowId));
                break;
            case McpCaptureKind.Region:
                if (Clip(target.Region) is not Int32Rect rect) return null;
                work = CaptureManager.Shared.CaptureRegionAsync(rect);
                break;
            default:
                work = CaptureManager.Shared.CaptureDisplayAsync(target.Display);
                break;
        }
        string? path;
        try { path = await work.ConfigureAwait(false); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ESLog.Log("mcp capture failed: " + e.GetType().Name);
            return null;
        }
        if (path == null) return null;
        await OnUi(() => { AppCoordinator.Shared.RouteAutomationCapture(path); return true; }).ConfigureAwait(false);
        return path;
    }

    /// Rounds to whole pixels and clips to the union of the displays; null when nothing is left.
    private static Int32Rect? Clip(McpRect region)
    {
        var displays = McpNative.Displays();
        if (displays.Count == 0) return null;
        double left = displays.Min(d => d.X), top = displays.Min(d => d.Y);
        double right = displays.Max(d => d.Right), bottom = displays.Max(d => d.Bottom);
        int x0 = (int)Math.Round(Math.Max(region.X, left)), y0 = (int)Math.Round(Math.Max(region.Y, top));
        int x1 = (int)Math.Round(Math.Min(region.Right, right)), y1 = (int)Math.Round(Math.Min(region.Bottom, bottom));
        if (x1 - x0 < 1 || y1 - y0 < 1) return null;
        return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
    }

    // MARK: - Files

    public Task<byte[]?> CreatePreviewPngAsync(string path, int maxPixelSize, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            BitmapSource frame;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                frame = dec.Frames[0];
            }
            int longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
            if (longest > maxPixelSize && longest > 0)
            {
                double s = (double)maxPixelSize / longest;
                frame = new TransformedBitmap(frame, new ScaleTransform(s, s));
            }
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(frame));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return (byte[]?)ms.ToArray();
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }, ct);

    public async Task<double?> GetVideoDurationSecondsAsync(string path, CancellationToken ct)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var props = await file.Properties.GetVideoPropertiesAsync();
            var seconds = props.Duration.TotalSeconds;
            return seconds > 0 ? Math.Round(seconds * 100) / 100 : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// An image opens in the annotation editor, a video in the library with it selected.
    public Task<bool> OpenInEditorAsync(string path, CancellationToken ct)
    {
        bool video = CaptureLibrary.VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
        if (video) return OnUi(() => { AppCoordinator.Shared.OpenInApp(path); return true; });
        if (!IsReadableImage(path)) return Task.FromResult(false);
        return OnUi(() => { AppCoordinator.Shared.Annotate(path); return true; });
    }

    private static bool IsReadableImage(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return dec.Frames.Count > 0 && dec.Frames[0].PixelWidth > 0;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
