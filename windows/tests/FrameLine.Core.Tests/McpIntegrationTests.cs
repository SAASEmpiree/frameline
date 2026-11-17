using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using FrameLine.Core.Mcp;
using Xunit;
using static FrameLine.Core.Tests.McpTestUtil;

namespace FrameLine.Core.Tests;

// Port of tests/MCPIntegrationTests.swift: the real TcpListener on 127.0.0.1 (random port) with the
// real tool catalog and the real tool handlers over a fake host whose capture folder is a temp
// directory, driven with raw HTTP over a socket (to forge the Host header).
public class McpIntegrationTests : IDisposable
{
    private readonly McpTempDir _dir = new();
    private readonly McpServer _server;
    private readonly string _discovery;

    public McpIntegrationTests()
    {
        File.WriteAllText(_dir.File("FrameLine 1.png"), "png");
        File.WriteAllText(_dir.File("FrameLine 2.mov"), "mov");
        _discovery = Path.Combine(_dir.Path, "appdata", "FrameLine", "mcp.json");
        _server = new McpServer(_discovery);
        _server.Start(new FakeHost(_dir.Path));
    }

    public void Dispose()
    {
        _server.Stop();
        _dir.Dispose();
    }

    private sealed record RawResponse(int Status, Dictionary<string, string> Headers, byte[] Body)
    {
        public JsonObject Json => JsonNode.Parse(Body) as JsonObject ?? new JsonObject();
    }

    private static async Task<RawResponse?> RawRequestAsync(int port, string host, string method = "POST",
        Dictionary<string, string>? headers = null, JsonNode? body = null, string? rawBody = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var payload = rawBody is not null ? Encoding.UTF8.GetBytes(rawBody) : body is null ? Array.Empty<byte>() : McpJson.Bytes(body);
        var head = new StringBuilder($"{method} /mcp HTTP/1.1\r\nHost: {host}\r\nContent-Type: application/json\r\n");
        head.Append($"Accept: application/json, text/event-stream\r\nContent-Length: {payload.Length}\r\n");
        if (headers is not null) foreach (var (k, v) in headers) head.Append($"{k}: {v}\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()));
        await stream.WriteAsync(payload);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new MemoryStream();
        var buf = new byte[65536];
        while (true)
        {
            int n = await stream.ReadAsync(buf, cts.Token);
            if (n <= 0) break;
            received.Write(buf, 0, n);
        }
        var all = received.ToArray();
        int sep = all.AsSpan().IndexOf("\r\n\r\n"u8);
        if (sep < 0) return null;
        var lines = Encoding.UTF8.GetString(all, 0, sep).Split("\r\n");
        int status = int.Parse(lines[0].Split(' ')[1]);
        var hs = new Dictionary<string, string>();
        foreach (var l in lines.Skip(1))
        {
            int c = l.IndexOf(':');
            if (c > 0) hs[l[..c].ToLowerInvariant()] = l[(c + 1)..].Trim();
        }
        return new RawResponse(status, hs, all[(sep + 4)..]);
    }

    [Fact]
    public async Task EndToEnd()
    {
        int port = _server.Port;
        Assert.True(port > 0);
        Assert.True(File.Exists(_discovery));
        var token = _server.Token;
        var host = $"127.0.0.1:{port}";
        var auth = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" };

        Assert.Equal(401, (await RawRequestAsync(port, host, body: InitBody()))?.Status);
        Assert.Equal(401, (await RawRequestAsync(port, host,
            headers: new() { ["Authorization"] = $"Bearer {new string('0', 64)}" }, body: InitBody()))?.Status);
        Assert.Equal(403, (await RawRequestAsync(port, $"evil.example:{port}", headers: auth, body: InitBody()))?.Status);

        var ini = await RawRequestAsync(port, host, headers: auth, body: InitBody());
        Assert.NotNull(ini);
        Assert.Equal(200, ini!.Status);
        var sid = ini.Headers.GetValueOrDefault("mcp-session-id") ?? "";
        Assert.NotEmpty(sid);
        Assert.Equal("close", ini.Headers["connection"]);
        var result = ini.Json["result"]!;
        Assert.Equal("2025-06-18", McpJson.Str(result["protocolVersion"]));
        Assert.Equal("frameline", McpJson.Str(result["serverInfo"]!["name"]));
        Assert.Equal(FrameLineMcpCatalog.Instructions, McpJson.Str(result["instructions"]));

        var h = new Dictionary<string, string>(auth) { ["Mcp-Session-Id"] = sid, ["MCP-Protocol-Version"] = "2025-06-18" };
        Assert.Equal(202, (await RawRequestAsync(port, host, headers: h, body: Rpc(null, "notifications/initialized")))?.Status);

        var list = await RawRequestAsync(port, $"localhost:{port}", headers: h, body: Rpc(2, "tools/list"));
        Assert.Equal(200, list?.Status);
        var tools = (JsonArray)list!.Json["result"]!["tools"]!;
        Assert.Equal(FrameLineMcpCatalog.Tools.Count, tools.Count);
        Assert.Contains(tools, t => McpJson.Str(t!["name"]) == "list_recent_captures");

        var call = await RawRequestAsync(port, host, headers: h, body: Rpc(3, "tools/call",
            new JsonObject { ["name"] = "list_recent_captures", ["arguments"] = new JsonObject { ["limit"] = 10 } }));
        Assert.Equal(200, call?.Status);
        var cres = call!.Json["result"]!;
        Assert.False(McpJson.Bool(cres["isError"]));
        var structured = cres["structuredContent"]!;
        Assert.Equal(2, McpJson.Int(structured["count"]));
        var paths = ((JsonArray)structured["captures"]!).Select(c => McpJson.Str(c!["path"])!).ToList();
        Assert.All(paths, p => Assert.True(Path.IsPathFullyQualified(p) && p.StartsWith(_dir.Path)));

        Assert.Equal(400, (await RawRequestAsync(port, host, headers: auth, body: Rpc(4, "tools/list")))?.Status);
        Assert.Equal(405, (await RawRequestAsync(port, host, "GET", headers: h))?.Status);
        Assert.Equal(400, (await RawRequestAsync(port, host, headers: h, rawBody: "{oops"))?.Status);
    }

    [Fact]
    public async Task OversizedBodyGetsItsResponse()
    {
        // The 413 is sent before the body is read; the graceful close must still deliver it.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _server.Port);
        var stream = client.GetStream();
        var head = $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: {McpHttpRequestParser.MaxBodyBytes + 1}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(new byte[32 * 1024]);
        var buf = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int n = await stream.ReadAsync(buf, cts.Token);
        Assert.StartsWith("HTTP/1.1 413", Encoding.ASCII.GetString(buf, 0, n));
    }

    [Fact]
    public void ListensOnLoopbackOnly()
    {
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(e => e.Port == _server.Port).ToList();
        Assert.NotEmpty(listeners);
        Assert.All(listeners, e => Assert.True(IPAddress.IsLoopback(e.Address)));
    }

    [Fact]
    public async Task StopRemovesDiscoveryAndRefusesConnections()
    {
        int port = _server.Port;
        _server.Stop();
        Assert.False(_server.IsRunning);
        Assert.Equal(0, _server.Port);
        Assert.False(File.Exists(_discovery));
        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port));
    }
}
