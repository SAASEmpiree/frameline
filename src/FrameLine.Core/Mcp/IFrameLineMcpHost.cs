using System.Reflection;

namespace FrameLine.Core.Mcp;

// Contract between the platform-neutral MCP server (agent "mcp") and the app (agent "shell"
// implements it in FrameLine/Shell/McpHost.cs). Mirrors what FrameLineMCPTools.swift needs.
//
// Threading: the server calls every member from thread-pool threads, never from the WPF
// dispatcher. Members that touch WPF/Win32 UI must marshal themselves (Dispatcher.InvokeAsync).
// Coordinates: Windows virtual-screen PHYSICAL pixels (PerMonitorV2), origin at the primary
// display's top-left, y down; displays left of / above the primary one are negative.
//
// Members added after the original stub carry a default body so an older McpHost still
// compiles; the app must override them for the tools to work (see each comment).
public interface IFrameLineMcpHost
{
    /// Interactive capture (overlay, the user picks). Not used by the MCP tools, which never ask the
    /// user to click (see CaptureAsync). mode: "area" | "window" | "fullscreen". Returns saved path or null if cancelled.
    Task<string?> TakeScreenshotAsync(string mode, int? delaySeconds, CancellationToken ct);
    /// Starts a full-screen recording of the PRIMARY display (start cue included) and completes once
    /// recording has actually started, with the path the video will be written to; null = failed
    /// (the app shows the reason on screen).
    Task<string?> StartRecordingAsync(CancellationToken ct);
    /// Stops and completes once the file is fully written; returns its path (null if nothing ran).
    Task<string?> StopRecordingAsync(CancellationToken ct);
    bool IsRecording { get; }
    /// Windows.Media.Ocr text, one line per recognized line ("\n"-separated). Never touches the clipboard.
    Task<string> RecognizeTextAsync(string path, CancellationToken ct);
    /// Image capture: the bitmap itself (plus the file); video: the file (file drop list).
    Task CopyToClipboardAsync(string path, CancellationToken ct);
    Task PinAsync(string path, CancellationToken ct);

    // ---- Added for the MCP tools (port of FrameLineMCPTools.swift) ----

    /// The app's version string ("1.0.1"), reported by get_status and serverInfo.
    string AppVersion => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0";

    /// Current capture folder (Preferences save directory, created if needed).
    string CaptureFolder => Preferences.Shared.EnsureSaveDirectory();

    /// "png" or "jpg" (Preferences.ImageFormat).
    string ImageFormat => Preferences.Shared.ImageFormat;

    /// Monitors, index 0 = primary display (tool index 1), then the others in a stable order,
    /// bounds in physical virtual-screen pixels (Screen.AllScreens / EnumDisplayMonitors).
    IReadOnlyList<McpRect> Displays => Array.Empty<McpRect>();

    /// 1-based index into Displays of the monitor under the mouse pointer, null if unknown.
    int? DisplayIndexUnderCursor() => null;

    /// Visible top-level application windows, front to back (EnumWindows order): not cloaked
    /// (DWMWA_CLOAKED), not tool windows, not minimized, with EXTENDED_FRAME_BOUNDS. The tool layer
    /// itself drops FrameLine's own windows and anything smaller than 40x40 px.
    IReadOnlyList<McpWindowInfo> ListWindows() => Array.Empty<McpWindowInfo>();

    /// Process id of the foreground window (GetForegroundWindow + GetWindowThreadProcessId).
    int? ForegroundProcessId => null;

    /// Non-interactive capture to a FILE in the capture folder (format from preferences, cursor
    /// and window-shadow preferences applied, the copy-to-clipboard preference IGNORED: the caller
    /// needs a path), same sound as a manual capture. Then routes it like macOS
    /// routeAutomationCapture: library + floating thumbnail (per "show preview"), never the editor.
    /// Returns the saved path, or null when no image was produced.
    Task<string?> CaptureAsync(McpCaptureTarget target, CancellationToken ct) => Task.FromResult<string?>(null);

    /// PNG bytes of the image downscaled so its longest side is at most maxPixelSize, or null.
    Task<byte[]?> CreatePreviewPngAsync(string path, int maxPixelSize, CancellationToken ct) => Task.FromResult<byte[]?>(null);

    /// True while a recording is being prepared (start cue playing, encoder starting).
    bool IsRecordingStarting => false;

    /// Path of the recording in progress (or being started), else null.
    string? CurrentRecordingPath => null;

    /// Like StartRecordingAsync for a rectangle (physical pixels, may span monitors).
    Task<string?> StartRegionRecordingAsync(McpRect region, CancellationToken ct) => Task.FromResult<string?>(null);

    /// Duration of a video file in seconds (Windows.Media.Editing / MediaClip), or null.
    Task<double?> GetVideoDurationSecondsAsync(string path, CancellationToken ct) => Task.FromResult<double?>(null);

    /// Shows a capture: an image opens in the annotation editor (AppCoordinator.Annotate), a video
    /// opens the main window with it selected (AppCoordinator.OpenInApp). Returns false when an
    /// image cannot be decoded (nothing is shown then).
    Task<bool> OpenInEditorAsync(string path, CancellationToken ct) => Task.FromResult(false);
}

/// A rectangle in physical virtual-screen pixels.
public readonly record struct McpRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Intersects(McpRect other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;
}

/// One top-level window. Id = HWND value. App = a readable app name (FileDescription of the exe,
/// else the process name); ProcessName = exe name without ".exe" (also accepted by app_name).
public sealed record McpWindowInfo(long Id, string App, int Pid, string Title, McpRect Bounds, string? ProcessName = null);

public enum McpCaptureKind { FullScreen, Window, Region }

/// What take_screenshot asks for. Display is 1-based (1 = primary); Region is rounded to whole
/// pixels by the host.
public sealed record McpCaptureTarget(McpCaptureKind Kind, int Display = 1, long WindowId = 0, McpRect Region = default);
