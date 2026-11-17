using System.Text;
using System.Text.Json.Nodes;
using FrameLine.Core.Mcp;

namespace FrameLine.Core.Tests;

internal sealed class FakeProvider : IMcpToolProvider
{
    public List<(string Name, JsonObject Args)> Calls { get; } = new();
    public IReadOnlyList<McpTool> Tools => FrameLineMcpCatalog.Tools;

    public Task<McpToolResult> CallAsync(string name, JsonObject arguments, CancellationToken ct)
    {
        Calls.Add((name, arguments));
        if (name == "get_status") return Task.FromResult(McpToolResult.Object(new JsonObject { ["recording"] = false }));
        return Task.FromResult(McpToolResult.Error("not available in tests"));
    }
}

/// A host that only knows a capture folder; everything else reports "nothing".
internal sealed class FakeHost : IFrameLineMcpHost
{
    private readonly string _folder;
    public FakeHost(string folder) => _folder = folder;

    public string CaptureFolder => _folder;
    public string ImageFormat => "png";
    public string AppVersion => "test";
    public IReadOnlyList<McpRect> Displays { get; set; } = new[] { new McpRect(0, 0, 1920, 1080), new McpRect(-1280, 0, 1280, 1024) };
    public List<McpWindowInfo> Windows { get; } = new();
    public IReadOnlyList<McpWindowInfo> ListWindows() => Windows;
    public int? ForegroundProcessId { get; set; }
    public McpCaptureTarget? LastTarget { get; private set; }
    public string? CaptureResult { get; set; }
    public List<string> Copied { get; } = new();
    public List<string> Opened { get; } = new();

    public Task<string?> CaptureAsync(McpCaptureTarget target, CancellationToken ct)
    {
        LastTarget = target;
        return Task.FromResult(CaptureResult);
    }

    public Task<string?> TakeScreenshotAsync(string mode, int? delaySeconds, CancellationToken ct) => Task.FromResult<string?>(null);

    // Recording: a fake that "records" by writing a file on stop.
    public bool IsRecording { get; private set; }
    public string? CurrentRecordingPath { get; private set; }
    public Task<string?> StartRecordingAsync(CancellationToken ct)
    {
        IsRecording = true;
        CurrentRecordingPath = Path.Combine(_folder, "FrameLine rec.mp4");
        return Task.FromResult<string?>(CurrentRecordingPath);
    }
    public Task<string?> StartRegionRecordingAsync(McpRect region, CancellationToken ct) => StartRecordingAsync(ct);
    public Task<string?> StopRecordingAsync(CancellationToken ct)
    {
        var path = CurrentRecordingPath;
        if (path is not null) File.WriteAllText(path, "video");
        IsRecording = false;
        CurrentRecordingPath = null;
        return Task.FromResult(path);
    }
    public Task<double?> GetVideoDurationSecondsAsync(string path, CancellationToken ct) => Task.FromResult<double?>(1.23456);

    public Task<string> RecognizeTextAsync(string path, CancellationToken ct) => Task.FromResult("hello\nworld\n");
    public Task CopyToClipboardAsync(string path, CancellationToken ct) { Copied.Add(path); return Task.CompletedTask; }
    public Task PinAsync(string path, CancellationToken ct) => Task.CompletedTask;
    public Task<bool> OpenInEditorAsync(string path, CancellationToken ct) { Opened.Add(path); return Task.FromResult(true); }
}

internal sealed class McpTempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "frameline-mcp-tests-" + Guid.NewGuid().ToString("N"));
    public McpTempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal static class McpTestUtil
{
    public static JsonObject Obj(McpHttpResponse r) => (JsonObject)JsonNode.Parse(r.Body)!;

    public static McpHttpRequest Request(string method = "POST", string path = "/mcp", string? host = "127.0.0.1:4242",
        string? auth = null, string? session = null, string? origin = null,
        Dictionary<string, string>? extra = null, JsonNode? body = null)
    {
        var h = new Dictionary<string, string> { ["content-type"] = "application/json" };
        if (host is not null) h["host"] = host;
        if (auth is not null) h["authorization"] = auth;
        if (session is not null) h["mcp-session-id"] = session;
        if (origin is not null) h["origin"] = origin;
        if (extra is not null) foreach (var (k, v) in extra) h[k.ToLowerInvariant()] = v;
        return new McpHttpRequest(method, path, h, body is null ? Array.Empty<byte>() : McpJson.Bytes(body));
    }

    public static JsonObject InitBody(int id = 1) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "initialize",
        ["params"] = new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "t", ["version"] = "1" },
        },
    };

    public static JsonObject Rpc(object? id, string method, JsonObject? @params = null)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id is int i) o["id"] = i;
        else if (id is string s) o["id"] = s;
        else if (id is bool b) o["id"] = b;
        if (@params is not null) o["params"] = @params;
        return o;
    }

    public static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);
}
