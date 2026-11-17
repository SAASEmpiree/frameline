using System.Text.Json;
using System.Text.Json.Nodes;

namespace FrameLine.Core.Mcp;

/// HTTP -> MCP: Host/Origin checks, Bearer auth, sessions, then the JSON-RPC dispatcher.
/// Thread-safe: called concurrently from connection tasks, state behind a lock.
public sealed class McpHttpHandler
{
    public const string EndpointPath = "/mcp";
    public const int MaxSessions = 64;

    public McpDispatcher Dispatcher { get; }

    private readonly object _lock = new();
    private string _token;
    private readonly List<string> _sessions = new();                // oldest first

    public McpHttpHandler(McpDispatcher dispatcher, string token)
    {
        Dispatcher = dispatcher;
        _token = token;
    }

    /// New token: every existing session dies with the old one.
    public void ReplaceToken(string newToken)
    {
        lock (_lock)
        {
            _token = newToken;
            _sessions.Clear();
        }
    }

    public int SessionCount { get { lock (_lock) return _sessions.Count; } }

    private string CurrentToken() { lock (_lock) return _token; }

    private bool HasSession(string id) { lock (_lock) return _sessions.Contains(id); }

    private string CreateSession()
    {
        var id = McpAuth.NewToken();
        lock (_lock)
        {
            _sessions.Add(id);
            if (_sessions.Count > MaxSessions) _sessions.RemoveRange(0, _sessions.Count - MaxSessions);
        }
        return id;
    }

    private bool EndSession(string id) { lock (_lock) return _sessions.Remove(id); }

    public async Task<McpHttpResponse> HandleAsync(McpHttpRequest request, CancellationToken ct = default)
    {
        // 1. Host + Origin first: a rebinding page gets nothing, not even a 401.
        if (!McpAuth.IsAllowedHost(request.Header("host"))) return McpHttpResponse.Error(403, "Forbidden host");
        if (!McpAuth.IsAllowedOrigin(request.Header("origin"))) return McpHttpResponse.Error(403, "Forbidden origin");
        // 2. Bearer token.
        if (!McpAuth.IsAuthorized(request.Header("authorization"), CurrentToken()))
            return McpHttpResponse.Error(401, "Missing or invalid bearer token",
                new() { ["WWW-Authenticate"] = "Bearer realm=\"frameline\"" });
        // 3. Route.
        if (request.Path != EndpointPath) return McpHttpResponse.Error(404, "Not found");
        var version = request.Header("mcp-protocol-version");
        if (version is not null && !McpDispatcher.SupportedProtocolVersions.Contains(version))
            return McpHttpResponse.Error(400, $"Unsupported MCP-Protocol-Version: {version}");

        switch (request.Method)
        {
            case "POST":
                return await HandlePostAsync(request, ct).ConfigureAwait(false);
            case "DELETE":
            {
                var sid = request.Header("mcp-session-id");
                if (sid is null) return McpHttpResponse.Error(400, "Missing Mcp-Session-Id");
                return EndSession(sid) ? new McpHttpResponse(204) : McpHttpResponse.Error(404, "Unknown session");
            }
            default:
                // No server-initiated SSE stream: GET is not offered (allowed by the spec).
                return McpHttpResponse.Error(405, "Method not allowed", new() { ["Allow"] = "POST, DELETE" });
        }
    }

    private async Task<McpHttpResponse> HandlePostAsync(McpHttpRequest request, CancellationToken ct)
    {
        var type = request.Header("content-type");
        if (type is not null && !type.ToLowerInvariant().Contains("application/json"))
            return McpHttpResponse.Error(415, "Content-Type must be application/json");

        JsonNode? parsed;
        try { parsed = McpJson.Parse(request.Body); }
        catch (JsonException)
        {
            return McpHttpResponse.Json(400, McpDispatcher.ErrorResponse(null, JsonRpcError.ParseError, "Parse error"));
        }

        List<JsonNode?> messages;
        bool isBatch;
        if (parsed is JsonArray array)
        {
            if (array.Count == 0)
                return McpHttpResponse.Json(400, McpDispatcher.ErrorResponse(null, JsonRpcError.InvalidRequest, "Empty batch"));
            messages = array.ToList();
            isBatch = true;
        }
        else
        {
            messages = new List<JsonNode?> { parsed };
            isBatch = false;
        }

        bool hasInitialize = messages.Any(m => McpJson.Str(McpJson.Get(m, "method")) == "initialize");
        var responseHeaders = new Dictionary<string, string>();
        if (hasInitialize)
        {
            // initialize must travel alone and opens a session.
            if (isBatch) return McpHttpResponse.Error(400, "initialize must not be part of a batch");
            responseHeaders["Mcp-Session-Id"] = CreateSession();
        }
        else
        {
            var sid = request.Header("mcp-session-id");
            if (sid is null) return McpHttpResponse.Error(400, "Missing Mcp-Session-Id header (send initialize first)");
            if (!HasSession(sid)) return McpHttpResponse.Error(404, "Unknown or expired session; initialize again");
        }

        var replies = new JsonArray();
        foreach (var message in messages)
        {
            var outcome = await Dispatcher.HandleAsync(message, ct).ConfigureAwait(false);
            if (outcome.Response is not null) replies.Add(outcome.Response);
        }
        if (replies.Count == 0) return new McpHttpResponse(202, responseHeaders);
        if (isBatch) return McpHttpResponse.Json(200, replies, responseHeaders);
        var single = replies[0]!;
        replies.RemoveAt(0);                                         // detach so it can be serialized alone
        return McpHttpResponse.Json(200, single, responseHeaders);
    }
}
