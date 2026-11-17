using System.Text.Json.Nodes;

namespace FrameLine.Core.Mcp;

/// The MCP tool handlers: argument validation and result shaping here, every platform action
/// through IFrameLineMcpHost (which drives the EXISTING FrameLine features). Schemas and
/// descriptions live in FrameLineMcpCatalog. Nothing here deletes a file.
public sealed class FrameLineMcpTools : IMcpToolProvider
{
    public const int PreviewMaxPixelSize = 1568;
    internal static TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    internal static TimeSpan StopTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IFrameLineMcpHost _host;

    public FrameLineMcpTools(IFrameLineMcpHost host) => _host = host;

    public IReadOnlyList<McpTool> Tools => FrameLineMcpCatalog.Tools;

    public Task<McpToolResult> CallAsync(string name, JsonObject args, CancellationToken ct) => name switch
    {
        "take_screenshot" => TakeScreenshotAsync(args, ct),
        "start_recording" => StartRecordingAsync(args, ct),
        "stop_recording" => StopRecordingAsync(ct),
        "get_status" => Task.FromResult(Status()),
        "list_recent_captures" => Task.FromResult(ListRecent(args)),
        "list_windows" => Task.FromResult(ListWindows(args)),
        "ocr_capture" => OcrAsync(args, ct),
        "copy_capture_to_clipboard" => CopyToClipboardAsync(args, ct),
        "open_in_editor" => OpenInEditorAsync(args, ct),
        _ => Task.FromResult(McpToolResult.Error($"Unknown tool: {name}")),
    };

    // ---- Shared helpers

    private string CaptureFolderPath => _host.CaptureFolder;

    /// `path` argument -> file inside the capture folder, or the newest capture of `kind`.
    private (string? Path, string? Error) ResolveCapture(JsonObject args, CaptureFolder.Kind kind)
    {
        var dir = CaptureFolderPath;
        if (args.TryGetPropertyValue("path", out var raw))
        {
            if (McpJson.Str(raw) is not string path) return (null, "`path` must be a string.");
            var file = CaptureFolder.Resolve(path, dir);
            if (file is null)
                return (null, $"No capture at {path} inside FrameLine's capture folder ({dir}). Use list_recent_captures to get valid paths.");
            if (kind != CaptureFolder.Kind.All && CaptureFolder.KindOf(file) != kind)
                return (null, $"{Path.GetFileName(file)} is not an {CaptureFolder.Name(kind)} capture.");
            return (file, null);
        }
        var newest = CaptureFolder.List(dir, kind, 1).FirstOrDefault();
        if (newest is null)
        {
            var prefix = kind == CaptureFolder.Kind.All ? "" : CaptureFolder.Name(kind) + " ";
            return (null, $"There is no {prefix}capture in {dir} yet.");
        }
        return (newest.Path, null);
    }

    /// x, y, width, height (pixels, top-left origin of the primary display).
    private static (McpRect? Rect, string? Error) Region(JsonObject args)
    {
        if (McpJson.Double(McpJson.Get(args, "x")) is not double x
            || McpJson.Double(McpJson.Get(args, "y")) is not double y
            || McpJson.Double(McpJson.Get(args, "width")) is not double w
            || McpJson.Double(McpJson.Get(args, "height")) is not double h)
            return (null, "Mode \"region\" needs numeric x, y, width and height (pixels, top-left origin of the primary display).");
        if (w < 1 || h < 1) return (null, "width and height must be at least 1 pixel.");
        return (new McpRect(x, y, w, h), null);
    }

    private static JsonObject RectJson(McpRect r) => new()
    {
        ["x"] = r.X, ["y"] = r.Y, ["width"] = r.Width, ["height"] = r.Height,
    };

    private static JsonObject WindowJson(McpWindowInfo w) => new()
    {
        ["window_id"] = w.Id,
        ["app"] = w.App,
        ["pid"] = w.Pid,
        ["title"] = w.Title,
        ["bounds"] = RectJson(w.Bounds),
    };

    private static bool SameApp(McpWindowInfo w, string app) =>
        string.Equals(w.App, app, StringComparison.InvariantCultureIgnoreCase)
        || (w.ProcessName is { Length: > 0 } p
            && (string.Equals(p, app, StringComparison.InvariantCultureIgnoreCase)
                || string.Equals(p + ".exe", app, StringComparison.InvariantCultureIgnoreCase)));

    /// Normal visible windows, front to back, FrameLine's own excluded, at least 40x40 px.
    private IReadOnlyList<McpWindowInfo> OnScreenWindows()
    {
        int me = Environment.ProcessId;
        return _host.ListWindows()
            .Where(w => w.Pid != me && w.Bounds.Width >= 40 && w.Bounds.Height >= 40)
            .ToList();
    }

    // ---- Windows

    private McpToolResult ListWindows(JsonObject args)
    {
        IEnumerable<McpWindowInfo> windows = OnScreenWindows();
        if (McpJson.Str(McpJson.Get(args, "app_name")) is { Length: > 0 } app)
            windows = windows.Where(w => SameApp(w, app));
        var list = windows.ToList();
        var array = new JsonArray();
        foreach (var w in list.Take(100)) array.Add(WindowJson(w));
        return McpToolResult.Object(new JsonObject { ["count"] = list.Count, ["windows"] = array });
    }

    // ---- take_screenshot

    private async Task<McpToolResult> TakeScreenshotAsync(JsonObject args, CancellationToken ct)
    {
        var mode = McpJson.Str(McpJson.Get(args, "mode")) ?? "full_screen";
        var detail = new JsonObject { ["mode"] = mode };
        McpCaptureTarget target;

        switch (mode)
        {
            case "full_screen":
            {
                int count = _host.Displays.Count;
                int index;
                if (McpJson.Int(McpJson.Get(args, "display")) is int display)
                {
                    if (display < 1 || display > count)
                        return McpToolResult.Error($"display must be between 1 and {count} (1 = primary display).");
                    index = display;
                }
                else if (_host.DisplayIndexUnderCursor() is int under && under >= 1 && under <= count) index = under;
                else index = 1;
                detail["display"] = index;
                target = new McpCaptureTarget(McpCaptureKind.FullScreen, Display: index);
                break;
            }
            case "window":
            {
                var windows = OnScreenWindows();
                McpWindowInfo? found;
                if (McpJson.Long(McpJson.Get(args, "window_id")) is long id)
                {
                    found = windows.FirstOrDefault(w => w.Id == id);
                    if (found is null) return McpToolResult.Error($"No on-screen window with window_id {id}. Call list_windows.");
                }
                else if (McpJson.Str(McpJson.Get(args, "app_name")) is { Length: > 0 } app)
                {
                    found = windows.FirstOrDefault(w => SameApp(w, app));
                    if (found is null) return McpToolResult.Error($"No on-screen window of an app named \"{app}\". Call list_windows.");
                }
                else
                {
                    var front = _host.ForegroundProcessId;
                    found = windows.FirstOrDefault(w => w.Pid == front) ?? windows.FirstOrDefault();
                    if (found is null) return McpToolResult.Error("No application window is on screen.");
                }
                detail["window"] = WindowJson(found);
                target = new McpCaptureTarget(McpCaptureKind.Window, WindowId: found.Id);
                break;
            }
            case "region":
            {
                var (rect, error) = Region(args);
                if (rect is not McpRect r) return McpToolResult.Error(error!);
                detail["region"] = RectJson(r);
                target = new McpCaptureTarget(McpCaptureKind.Region, Region: r);
                break;
            }
            default:
                return McpToolResult.Error("mode must be \"full_screen\", \"window\" or \"region\".");
        }

        if (McpJson.Int(McpJson.Get(args, "delay_seconds")) is int delay)
        {
            if (delay < 0 || delay > 10) return McpToolResult.Error("delay_seconds must be between 0 and 10.");
            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
        }

        var path = await _host.CaptureAsync(target, ct).ConfigureAwait(false);
        if (path is null || !File.Exists(path))
            return McpToolResult.Error("The screenshot failed: Windows produced no image (window closed or minimized, or region off screen).");

        var output = (JsonObject)detail.DeepClone();
        output["path"] = path;
        output["format"] = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ImageHeader.TryGetPixelSize(path) is var (w, h))
        {
            output["width"] = w;
            output["height"] = h;
        }
        var extra = new List<JsonObject>();
        if (McpJson.Bool(McpJson.Get(args, "include_image")) == true
            && await _host.CreatePreviewPngAsync(path, PreviewMaxPixelSize, ct).ConfigureAwait(false) is { Length: > 0 } png)
        {
            extra.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" });
        }
        return McpToolResult.Object(output, extra);
    }

    // ---- Recording

    private static async Task<bool> WaitUntilAsync(TimeSpan timeout, Func<bool> done, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (done()) return true;
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
        return done();
    }

    /// Awaits the host call at most `timeout`; the call keeps running if it takes longer.
    private static async Task<T?> WithTimeout<T>(Task<T?> task, TimeSpan timeout, CancellationToken ct)
    {
        try { return await task.WaitAsync(timeout, ct).ConfigureAwait(false); }
        catch (TimeoutException) { return default; }
    }

    private async Task<McpToolResult> StartRecordingAsync(JsonObject args, CancellationToken ct)
    {
        if (_host.IsRecording || _host.IsRecordingStarting)
        {
            var current = _host.CurrentRecordingPath;
            var suffix = current is null ? "" : $" ({current})";
            return McpToolResult.Error($"A recording is already running{suffix}. Call stop_recording first.");
        }
        var mode = McpJson.Str(McpJson.Get(args, "mode")) ?? "full_screen";
        Task<string?> start;
        switch (mode)
        {
            case "full_screen":
                start = _host.StartRecordingAsync(ct);
                break;
            case "region":
            {
                var (rect, error) = Region(args);
                if (rect is not McpRect r) return McpToolResult.Error(error!);
                if (!_host.Displays.Any(d => d.Intersects(r))) return McpToolResult.Error("The region is not on any display.");
                start = _host.StartRegionRecordingAsync(r, ct);
                break;
            }
            default:
                return McpToolResult.Error("mode must be \"full_screen\" or \"region\".");
        }
        var deadline = DateTime.UtcNow + StartTimeout;
        var started = await WithTimeout(start, StartTimeout, ct).ConfigureAwait(false);
        // A host that returns before the encoder runs still gets the macOS budget to report it.
        var left = deadline - DateTime.UtcNow;
        if (left > TimeSpan.Zero)
            await WaitUntilAsync(left, () => _host.IsRecording || !_host.IsRecordingStarting, ct).ConfigureAwait(false);
        var path = _host.CurrentRecordingPath ?? started;
        if (!_host.IsRecording || path is null)
            return McpToolResult.Error("The recording could not start (FrameLine showed the reason on screen).");
        return McpToolResult.Object(new JsonObject { ["status"] = "recording", ["mode"] = mode, ["path"] = path });
    }

    private async Task<McpToolResult> StopRecordingAsync(CancellationToken ct)
    {
        if (_host.IsRecordingStarting) await WaitUntilAsync(StartTimeout, () => !_host.IsRecordingStarting, ct).ConfigureAwait(false);
        var current = _host.CurrentRecordingPath;
        if (!_host.IsRecording || current is null) return McpToolResult.Error("No screen recording is running.");

        var stopped = await WithTimeout(_host.StopRecordingAsync(ct), StopTimeout, ct).ConfigureAwait(false);
        if (_host.IsRecording)
            return McpToolResult.Error("The recording is still being finalized; call get_status in a moment.");
        var path = stopped ?? current;
        long size = 0;
        try { size = new FileInfo(path).Exists ? new FileInfo(path).Length : 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (size <= 0) return McpToolResult.Error("The recording stopped but no video was written.");

        var output = new JsonObject { ["status"] = "stopped", ["path"] = path, ["size_bytes"] = size };
        if (await _host.GetVideoDurationSecondsAsync(path, ct).ConfigureAwait(false) is double seconds && double.IsFinite(seconds))
            output["duration_seconds"] = Math.Round(seconds * 100, MidpointRounding.AwayFromZero) / 100;
        return McpToolResult.Object(output);
    }

    // ---- Read-only

    private McpToolResult Status()
    {
        var output = new JsonObject
        {
            ["app"] = "FrameLine",
            ["version"] = _host.AppVersion,
            ["recording"] = _host.IsRecording,
            ["recording_starting"] = _host.IsRecordingStarting,
            // Windows has no screen-capture permission: kept for the macOS result shape.
            ["screen_recording_permission"] = true,
            ["capture_folder"] = CaptureFolderPath,
            ["image_format"] = _host.ImageFormat == "jpg" ? "jpg" : "png",
        };
        if (_host.CurrentRecordingPath is string path) output["recording_path"] = path;
        return McpToolResult.Object(output);
    }

    private McpToolResult ListRecent(JsonObject args)
    {
        int limit = Math.Min(200, Math.Max(1, McpJson.Int(McpJson.Get(args, "limit")) ?? 20));
        var kindRaw = McpJson.Str(McpJson.Get(args, "kind")) ?? "all";
        if (CaptureFolder.ParseKind(kindRaw) is not CaptureFolder.Kind kind)
            return McpToolResult.Error("kind must be \"all\", \"image\" or \"video\".");
        var dir = CaptureFolderPath;
        var items = CaptureFolder.List(dir, kind, limit);
        var array = new JsonArray();
        foreach (var item in items) array.Add(item.ToJson());
        return McpToolResult.Object(new JsonObject { ["capture_folder"] = dir, ["count"] = items.Count, ["captures"] = array });
    }

    private async Task<McpToolResult> OcrAsync(JsonObject args, CancellationToken ct)
    {
        var (path, error) = ResolveCapture(args, CaptureFolder.Kind.Image);
        if (path is null) return McpToolResult.Error(error!);
        var text = await _host.RecognizeTextAsync(path, ct).ConfigureAwait(false) ?? "";
        int lines = text.Split('\n').Count(l => l.Length > 0);
        return McpToolResult.Object(new JsonObject { ["path"] = path, ["text"] = text, ["line_count"] = lines });
    }

    // ---- Actions (visible to the user, nothing destructive)

    private async Task<McpToolResult> CopyToClipboardAsync(JsonObject args, CancellationToken ct)
    {
        var (path, error) = ResolveCapture(args, CaptureFolder.Kind.All);
        if (path is null) return McpToolResult.Error(error!);
        await _host.CopyToClipboardAsync(path, ct).ConfigureAwait(false);
        var copied = CaptureFolder.KindOf(path) == CaptureFolder.Kind.Video ? "file" : "image";
        return McpToolResult.Object(new JsonObject { ["path"] = path, ["copied"] = copied });
    }

    private async Task<McpToolResult> OpenInEditorAsync(JsonObject args, CancellationToken ct)
    {
        var (path, error) = ResolveCapture(args, CaptureFolder.Kind.All);
        if (path is null) return McpToolResult.Error(error!);
        bool isVideo = CaptureFolder.KindOf(path) == CaptureFolder.Kind.Video;
        bool shown = await _host.OpenInEditorAsync(path, ct).ConfigureAwait(false);
        if (isVideo) return McpToolResult.Object(new JsonObject { ["path"] = path, ["opened"] = "library" });
        if (!shown) return McpToolResult.Error($"{Path.GetFileName(path)} is not a readable image.");
        return McpToolResult.Object(new JsonObject { ["path"] = path, ["opened"] = "editor" });
    }
}
