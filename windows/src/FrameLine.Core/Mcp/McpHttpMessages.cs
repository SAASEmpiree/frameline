using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace FrameLine.Core.Mcp;

// Streamable HTTP transport, server side, JSON responses only. POST /mcp carries one JSON-RPC
// message (or a batch, for 2025-03-26 clients) and gets an application/json reply; SSE is not
// offered (GET -> 405), which the spec allows. Sessions use the Mcp-Session-Id header.

public sealed class McpHttpRequest
{
    public string Method { get; }
    public string Path { get; }
    /// Header names lowercased.
    public IReadOnlyDictionary<string, string> Headers { get; }
    public byte[] Body { get; }

    public McpHttpRequest(string method, string path, IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        Method = method;
        Path = path;
        Headers = headers;
        Body = body;
    }

    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;
}

public sealed class McpHttpResponse
{
    public int Status { get; }
    public Dictionary<string, string> Headers { get; }
    public byte[] Body { get; }

    public McpHttpResponse(int status, Dictionary<string, string>? headers = null, byte[]? body = null)
    {
        Status = status;
        Headers = headers ?? new Dictionary<string, string>();
        Body = body ?? Array.Empty<byte>();
    }

    public static McpHttpResponse Json(int status, JsonNode node, Dictionary<string, string>? headers = null)
    {
        var h = headers is null ? new Dictionary<string, string>() : new Dictionary<string, string>(headers);
        h["Content-Type"] = "application/json";
        return new McpHttpResponse(status, h, McpJson.Bytes(node));
    }

    /// Plain error with a JSON-RPC error body (what MCP clients know how to display).
    public static McpHttpResponse Error(int status, string message, Dictionary<string, string>? headers = null) =>
        Json(status, McpDispatcher.ErrorResponse(null, JsonRpcError.InvalidRequest, message), headers);

    public static string Reason(int status) => status switch
    {
        200 => "OK",
        202 => "Accepted",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        408 => "Request Timeout",
        411 => "Length Required",
        413 => "Payload Too Large",
        415 => "Unsupported Media Type",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        _ => "Status",
    };

    /// Wire format. Always "Connection: close": one request per connection keeps the server tiny.
    public byte[] Serialize()
    {
        var all = new Dictionary<string, string>(Headers)
        {
            ["Content-Length"] = Body.Length.ToString(CultureInfo.InvariantCulture),
            ["Connection"] = "close",
            ["Cache-Control"] = "no-store",
        };
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(Status.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(Reason(Status)).Append("\r\n");
        foreach (var key in all.Keys.OrderBy(k => k, StringComparer.Ordinal))
            head.Append(key).Append(": ").Append(all[key]).Append("\r\n");
        head.Append("\r\n");
        var headBytes = Encoding.UTF8.GetBytes(head.ToString());
        var output = new byte[headBytes.Length + Body.Length];
        headBytes.CopyTo(output, 0);
        Body.CopyTo(output, headBytes.Length);
        return output;
    }
}

/// Incremental HTTP/1.1 request parser: bytes received so far -> request, need more, or reject.
public static class McpHttpRequestParser
{
    public const int MaxHeaderBytes = 16 * 1024;
    public const int MaxBodyBytes = 4 * 1024 * 1024;

    public enum State { Incomplete, Complete, Invalid }

    public readonly record struct Result(State State, McpHttpRequest? Request = null, int Status = 0, string? Message = null)
    {
        public static Result Incomplete => new(State.Incomplete);
        public static Result Invalid(int status, string message) => new(State.Invalid, null, status, message);
    }

    private static readonly byte[] Separator = "\r\n\r\n"u8.ToArray();
    private static readonly HashSet<string> SingleHeaders = new(StringComparer.Ordinal)
    {
        "host", "authorization", "content-length", "mcp-session-id",
    };

    public static Result Parse(ReadOnlySpan<byte> buffer)
    {
        int headEnd = buffer.IndexOf(Separator);
        if (headEnd < 0)
            return buffer.Length > MaxHeaderBytes ? Result.Invalid(431, "Request headers too large") : Result.Incomplete;
        if (headEnd > MaxHeaderBytes) return Result.Invalid(431, "Request headers too large");

        string head;
        try { head = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer[..headEnd]); }
        catch (DecoderFallbackException) { return Result.Invalid(400, "Headers are not UTF-8"); }

        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            return Result.Invalid(400, "Malformed request line");

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon < 0) return Result.Invalid(400, "Malformed header line");
            var name = TrimSpaces(line[..colon]).ToLowerInvariant();
            var value = TrimSpaces(line[(colon + 1)..]);
            if (name.Length == 0) return Result.Invalid(400, "Empty header name");
            if (headers.TryGetValue(name, out var previous))
            {
                // Duplicate Host / Authorization / Content-Length are ambiguous: refuse rather than guess.
                if (SingleHeaders.Contains(name)) return Result.Invalid(400, $"Duplicate {name} header");
                headers[name] = previous + ", " + value;
            }
            else headers[name] = value;
        }
        if (headers.ContainsKey("transfer-encoding"))
            return Result.Invalid(411, "Chunked bodies are not supported; send Content-Length");

        long length = 0;
        if (headers.TryGetValue("content-length", out var raw))
        {
            if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out length) || length < 0)
                return Result.Invalid(400, "Bad Content-Length");
        }
        if (length > MaxBodyBytes) return Result.Invalid(413, "Request body too large");

        int bodyStart = headEnd + Separator.Length;
        if (buffer.Length - bodyStart < length) return Result.Incomplete;
        var body = buffer.Slice(bodyStart, (int)length).ToArray();

        var path = requestLine[1];
        int q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        return new Result(State.Complete, new McpHttpRequest(requestLine[0].ToUpperInvariant(), path, headers, body));
    }

    private static string TrimSpaces(string s) => s.Trim(' ', '\t');
}
