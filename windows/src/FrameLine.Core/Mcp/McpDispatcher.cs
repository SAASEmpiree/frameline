using System.Text.Json;
using System.Text.Json.Nodes;

namespace FrameLine.Core.Mcp;

/// Turns one decoded JSON-RPC message into a reply. Transport-agnostic. Implements the server side
/// of the MCP 2025-06-18 spec that FrameLine needs: initialize, notifications/*, ping, tools/list,
/// tools/call. https://modelcontextprotocol.io/specification/2025-06-18
public sealed class McpDispatcher
{
    /// Newest first. The first one is answered when the client asks for a version we do not know.
    public static readonly IReadOnlyList<string> SupportedProtocolVersions = new[] { "2025-06-18", "2025-03-26", "2024-11-05" };

    public string ServerName { get; }
    public string ServerTitle { get; }
    public string ServerVersion { get; }
    public string Instructions { get; }
    public IMcpToolProvider Provider { get; }

    public McpDispatcher(string serverName, string serverTitle, string serverVersion, string instructions,
                         IMcpToolProvider provider)
    {
        ServerName = serverName;
        ServerTitle = serverTitle;
        ServerVersion = serverVersion;
        Instructions = instructions;
        Provider = provider;
    }

    /// Reply to send back, or null for a notification / client response (HTTP 202).
    public readonly record struct Outcome(JsonObject? Response)
    {
        public bool IsAccepted => Response is null;
        public static Outcome Accepted => new(null);
    }

    public static JsonObject ErrorResponse(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    public static JsonObject ResultResponse(JsonNode id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["result"] = result,
    };

    /// A valid JSON-RPC id is a string or a number (not a boolean, not null for requests).
    public static bool IsValidId(JsonNode? id) =>
        McpJson.Kind(id) is JsonValueKind.String or JsonValueKind.Number;

    public async Task<Outcome> HandleAsync(JsonNode? message, CancellationToken ct = default)
    {
        if (message is not JsonObject msg || McpJson.Str(McpJson.Get(msg, "jsonrpc")) != "2.0")
            return new(ErrorResponse(null, JsonRpcError.InvalidRequest, "Invalid JSON-RPC 2.0 message"));

        var method = McpJson.Str(McpJson.Get(msg, "method"));
        if (method is null)
        {
            // A response from the client (result/error): we never send requests, so accept and ignore.
            if (msg.ContainsKey("result") || msg.ContainsKey("error")) return Outcome.Accepted;
            return new(ErrorResponse(McpJson.Get(msg, "id"), JsonRpcError.InvalidRequest, "Missing method"));
        }
        // Notification (no id): never answered.
        if (!msg.ContainsKey("id")) return Outcome.Accepted;
        var id = McpJson.Get(msg, "id");
        if (!IsValidId(id))
            return new(ErrorResponse(null, JsonRpcError.InvalidRequest, "Request id must be a string or a number"));
        var parameters = McpJson.Get(msg, "params") as JsonObject ?? new JsonObject();

        switch (method)
        {
            case "initialize":
                return new(ResultResponse(id!, InitializeResult(parameters)));
            case "ping":
                return new(ResultResponse(id!, new JsonObject()));
            case "tools/list":
            {
                var tools = new JsonArray();
                foreach (var tool in Provider.Tools) tools.Add(tool.ToJson());
                return new(ResultResponse(id!, new JsonObject { ["tools"] = tools }));
            }
            case "tools/call":
            {
                var name = McpJson.Str(McpJson.Get(parameters, "name"));
                if (name is null)
                    return new(ErrorResponse(id, JsonRpcError.InvalidParams, "tools/call needs a tool name"));
                if (!Provider.Tools.Any(t => t.Name == name))
                    return new(ErrorResponse(id, JsonRpcError.InvalidParams, $"Unknown tool: {name}"));
                var raw = McpJson.Get(parameters, "arguments");
                if (raw is not null && raw is not JsonObject)
                    return new(ErrorResponse(id, JsonRpcError.InvalidParams, "tools/call arguments must be an object"));
                // The provider gets its own copy: the request tree stays untouched.
                var args = (raw as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
                McpToolResult result;
                try { result = await Provider.CallAsync(name, args, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { result = McpToolResult.Error("FrameLine stopped the MCP server before the tool finished."); }
                catch (Exception e) { result = McpToolResult.Error($"FrameLine could not run {name}: {e.Message}"); }
                return new(ResultResponse(id!, result.ToJson()));
            }
            default:
                return new(ErrorResponse(id, JsonRpcError.MethodNotFound, $"Method not found: {method}"));
        }
    }

    /// Version negotiation: echo the client's version when we support it, else offer our newest.
    public static string NegotiatedVersion(string? requested) =>
        requested is not null && SupportedProtocolVersions.Contains(requested) ? requested : SupportedProtocolVersions[0];

    private JsonObject InitializeResult(JsonObject parameters) => new()
    {
        ["protocolVersion"] = NegotiatedVersion(McpJson.Str(McpJson.Get(parameters, "protocolVersion"))),
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
        ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = ServerTitle, ["version"] = ServerVersion },
        ["instructions"] = Instructions,
    };
}
