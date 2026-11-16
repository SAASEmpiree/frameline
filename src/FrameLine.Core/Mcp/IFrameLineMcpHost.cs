namespace FrameLine.Core.Mcp;

// Contract between the platform-neutral MCP server (agent "mcp") and the app (agent "shell"
// implements it in FrameLine/Shell/McpHost.cs). Mirrors what FrameLineMCPTools.swift needs.
public interface IFrameLineMcpHost
{
    /// mode: "area" | "window" | "fullscreen" (exact names from FrameLineMCPCatalog.swift). Returns saved path or null if cancelled.
    Task<string?> TakeScreenshotAsync(string mode, int? delaySeconds, CancellationToken ct);
    Task<string?> StartRecordingAsync(CancellationToken ct);
    Task<string?> StopRecordingAsync(CancellationToken ct);
    bool IsRecording { get; }
    Task<string> RecognizeTextAsync(string path, CancellationToken ct);
    Task CopyToClipboardAsync(string path, CancellationToken ct);
    Task PinAsync(string path, CancellationToken ct);
}
