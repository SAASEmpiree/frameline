using System.Text.Json.Nodes;

namespace FrameLine.Core.Mcp;

/// Behaviour hints of a tool (MCP "ToolAnnotations"). Hints only: clients must not rely on them
/// for security, but they drive confirmation prompts in most clients.
public sealed record McpToolAnnotations(
    string? Title, bool ReadOnlyHint, bool DestructiveHint, bool IdempotentHint, bool OpenWorldHint)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["readOnlyHint"] = ReadOnlyHint,
            ["openWorldHint"] = OpenWorldHint,
        };
        // destructiveHint / idempotentHint are only meaningful when readOnlyHint == false.
        if (!ReadOnlyHint)
        {
            o["destructiveHint"] = DestructiveHint;
            o["idempotentHint"] = IdempotentHint;
        }
        if (Title is not null) o["title"] = Title;
        return o;
    }
}

/// One tool as advertised by tools/list.
public sealed record McpTool(
    string Name, string Title, string Description, JsonObject InputSchema, McpToolAnnotations Annotations)
{
    // A JsonNode can only have one parent: every serialization gets its own copy of the schema.
    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["title"] = Title,
        ["description"] = Description,
        ["inputSchema"] = InputSchema.DeepClone(),
        ["annotations"] = Annotations.ToJson(),
    };
}

/// Result of a tools/call. Tool failures are reported IN the result (isError: true) so the model
/// can read them; protocol failures (unknown tool, bad params) are JSON-RPC errors.
public sealed class McpToolResult
{
    public List<JsonObject> Content { get; } = new();
    public JsonObject? StructuredContent { get; init; }
    public bool IsError { get; init; }

    /// Structured success: the object is returned both as structuredContent and as JSON text
    /// (text keeps older clients working).
    public static McpToolResult Object(JsonObject obj, IEnumerable<JsonObject>? extra = null)
    {
        var r = new McpToolResult { StructuredContent = obj, IsError = false };
        r.Content.Add(TextBlock(McpJson.String(obj)));
        if (extra is not null) r.Content.AddRange(extra);
        return r;
    }

    public static McpToolResult Text(string text)
    {
        var r = new McpToolResult();
        r.Content.Add(TextBlock(text));
        return r;
    }

    public static McpToolResult Error(string message)
    {
        var r = new McpToolResult { IsError = true };
        r.Content.Add(TextBlock(message));
        return r;
    }

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    public JsonObject ToJson()
    {
        var content = new JsonArray();
        foreach (var block in Content) content.Add(block.DeepClone());
        var o = new JsonObject { ["content"] = content, ["isError"] = IsError };
        if (StructuredContent is not null) o["structuredContent"] = StructuredContent.DeepClone();
        return o;
    }
}

/// What the server exposes. Implemented by FrameLineMcpTools and by fakes in tests.
public interface IMcpToolProvider
{
    IReadOnlyList<McpTool> Tools { get; }
    Task<McpToolResult> CallAsync(string name, JsonObject arguments, CancellationToken ct);
}

/// JSON-RPC error codes used here.
public static class JsonRpcError
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}
