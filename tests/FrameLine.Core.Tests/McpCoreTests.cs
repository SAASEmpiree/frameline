using System.Text;
using System.Text.Json.Nodes;
using FrameLine.Core.Mcp;
using Xunit;
using static FrameLine.Core.Tests.McpTestUtil;

namespace FrameLine.Core.Tests;

// Port of tests/MCPCoreTests.swift: JSON-RPC, HTTP parsing, auth, Host/Origin checks, sessions,
// discovery file, capture-folder helpers, plus the tool handlers against a fake host.
public class McpCoreTests
{
    // ---- Token & auth

    [Fact]
    public void Tokens()
    {
        var t1 = McpAuth.NewToken();
        var t2 = McpAuth.NewToken();
        Assert.Equal(64, t1.Length);
        Assert.All(t1, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c)));
        Assert.NotEqual(t1, t2);
        Assert.True(McpAuth.ConstantTimeEquals("abc", "abc"));
        Assert.False(McpAuth.ConstantTimeEquals("abc", "abd"));
        Assert.False(McpAuth.ConstantTimeEquals("abc", "abcd"));
        Assert.False(McpAuth.ConstantTimeEquals("", "a"));
        Assert.True(McpAuth.IsAuthorized($"Bearer {t1}", t1));
        Assert.True(McpAuth.IsAuthorized($"bearer {t1}", t1));
        Assert.False(McpAuth.IsAuthorized($"Bearer {t2}", t1));
        Assert.False(McpAuth.IsAuthorized(null, t1));
        Assert.False(McpAuth.IsAuthorized($"Basic {t1}", t1));
        Assert.False(McpAuth.IsAuthorized(t1, t1));
        Assert.False(McpAuth.IsAuthorized("Bearer ", ""));
    }

    [Theory]
    [InlineData("127.0.0.1:5555", true)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST:1", true)]
    [InlineData("evil.com", false)]
    [InlineData("127.0.0.1.evil.com:80", false)]
    [InlineData(null, false)]
    [InlineData("localhost:abc", false)]
    [InlineData("0.0.0.0:1", false)]
    [InlineData("[::1]:80", false)]
    public void HostGuard(string? host, bool allowed) => Assert.Equal(allowed, McpAuth.IsAllowedHost(host));

    [Theory]
    [InlineData(null, true)]
    [InlineData("http://localhost:3000", true)]
    [InlineData("http://127.0.0.1", true)]
    [InlineData("https://evil.com", false)]
    [InlineData("null", false)]
    [InlineData("file://localhost/x", false)]
    public void OriginGuard(string? origin, bool allowed) => Assert.Equal(allowed, McpAuth.IsAllowedOrigin(origin));

    // ---- HTTP parser

    private static McpHttpRequestParser.Result Parse(string raw) => McpHttpRequestParser.Parse(Encoding.UTF8.GetBytes(raw));

    [Fact]
    public void ParserCompleteRequest()
    {
        var r = Parse("POST /mcp?x=1 HTTP/1.1\r\nHost: 127.0.0.1:1\r\nContent-Length: 2\r\nAuthorization: Bearer x\r\n\r\n{}");
        Assert.Equal(McpHttpRequestParser.State.Complete, r.State);
        Assert.Equal("POST", r.Request!.Method);
        Assert.Equal("/mcp", r.Request.Path);
        Assert.Equal("Bearer x", r.Request.Header("Authorization"));
        Assert.Equal("{}", Utf8(r.Request.Body));
    }

    [Fact]
    public void ParserRejectsAndWaits()
    {
        Assert.Equal(McpHttpRequestParser.State.Incomplete, Parse("POST /mcp HTTP/1.1\r\nContent-Length: 10\r\n\r\n{}").State);
        Assert.Equal(McpHttpRequestParser.State.Incomplete, Parse("POST /mcp HTTP/1.1\r\nHost: a").State);
        Assert.Equal(400, Parse("GARBAGE\r\n\r\n").Status);
        Assert.Equal(411, Parse("POST /mcp HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n").Status);
        Assert.Equal(413, Parse("POST /mcp HTTP/1.1\r\nContent-Length: 99999999\r\n\r\n").Status);
        Assert.Equal(400, Parse("POST /mcp HTTP/1.1\r\nContent-Length: -1\r\n\r\n").Status);
        Assert.Equal(400, Parse("POST /mcp HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n").Status);
        Assert.Equal(431, Parse(new string('a', 20_000)).Status);
        Assert.Equal(400, McpHttpRequestParser.Parse(new byte[] { 0xFF, 0xFE, 0x0D, 0x0A, 0x0D, 0x0A }).Status);
    }

    [Fact]
    public void ResponseSerialization()
    {
        var s = Utf8(McpHttpResponse.Json(200, new JsonObject { ["a"] = 1 }).Serialize());
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", s);
        Assert.Contains("Content-Length: 7\r\n", s);
        Assert.Contains("Connection: close\r\n", s);
        Assert.Contains("Cache-Control: no-store\r\n", s);
        Assert.EndsWith("\r\n\r\n{\"a\":1}", s);
    }

    [Fact]
    public void JsonIsSortedAndUnescaped()
    {
        var s = McpJson.String(new JsonObject { ["b"] = "é/x", ["a"] = new JsonArray(1, 2) });
        Assert.Equal("{\"a\":[1,2],\"b\":\"é/x\"}", s);
        Assert.Null(McpJson.Int(JsonNode.Parse("true")));
        Assert.Equal(3, McpJson.Int(JsonNode.Parse("3.0")));
        Assert.Null(McpJson.Int(JsonNode.Parse("3.5")));
        Assert.Null(McpJson.Int(JsonNode.Parse("\"3\"")));
        Assert.Null(McpJson.Int(JsonNode.Parse("1e400")));
        Assert.Equal(7, McpJson.Int(JsonValue.Create(7)));
    }

    // ---- JSON-RPC dispatcher

    private static McpDispatcher NewDispatcher(IMcpToolProvider? provider = null) =>
        new("frameline", "FrameLine", "9.9", "hi", provider ?? new FakeProvider());

    private static async Task<JsonObject?> Reply(McpDispatcher d, JsonNode? m) => (await d.HandleAsync(m)).Response;

    private static int? ErrorCode(JsonObject? r) => McpJson.Int(McpJson.Get(McpJson.Get(r, "error"), "code"));

    [Fact]
    public async Task DispatcherInitializeAndPing()
    {
        var d = NewDispatcher();
        var ini = await Reply(d, InitBody());
        var res = ini?["result"] as JsonObject;
        Assert.Equal("2025-06-18", McpJson.Str(res?["protocolVersion"]));
        Assert.NotNull(McpJson.Get(res?["capabilities"], "tools"));
        Assert.Equal("frameline", McpJson.Str(McpJson.Get(res?["serverInfo"], "name")));
        Assert.Equal("hi", McpJson.Str(res?["instructions"]));
        Assert.Equal(1, McpJson.Int(ini?["id"]));
        Assert.Equal("2025-03-26", McpDispatcher.NegotiatedVersion("2025-03-26"));
        Assert.Equal("2025-06-18", McpDispatcher.NegotiatedVersion("1999-01-01"));
        Assert.Equal("2025-06-18", McpDispatcher.NegotiatedVersion(null));
        Assert.True((await d.HandleAsync(Rpc(null, "notifications/initialized"))).IsAccepted);
        Assert.True((await d.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 5, ["result"] = new JsonObject() })).IsAccepted);
        var ping = await Reply(d, Rpc("p", "ping"));
        Assert.Empty((JsonObject)ping!["result"]!);
        Assert.Equal("p", McpJson.Str(ping["id"]));
    }

    [Fact]
    public async Task DispatcherToolsList()
    {
        var d = NewDispatcher();
        var list = await Reply(d, Rpc(2, "tools/list"));
        var tools = ((JsonArray)list!["result"]!["tools"]!).Select(t => (JsonObject)t!).ToList();
        var names = tools.Select(t => McpJson.Str(t["name"])).ToHashSet();
        Assert.Equal(new HashSet<string?> { "take_screenshot", "start_recording", "stop_recording", "get_status",
            "list_recent_captures", "list_windows", "ocr_capture", "copy_capture_to_clipboard", "open_in_editor" }, names);
        var readOnly = new[] { "get_status", "list_recent_captures", "list_windows", "ocr_capture" };
        foreach (var tool in tools)
        {
            var n = McpJson.Str(tool["name"])!;
            var ann = (JsonObject)tool["annotations"]!;
            Assert.All(n, c => Assert.True(char.IsLower(c) || c == '_'));
            Assert.Equal("object", McpJson.Str(McpJson.Get(tool["inputSchema"], "type")));
            Assert.False(McpJson.Bool(McpJson.Get(tool["inputSchema"], "additionalProperties")));
            Assert.True((McpJson.Str(tool["description"]) ?? "").Length > 40, n);
            Assert.False(McpJson.Bool(ann["openWorldHint"]));
            bool ro = McpJson.Bool(ann["readOnlyHint"]) == true;
            Assert.Equal(readOnly.Contains(n), ro);
            if (!ro) Assert.False(McpJson.Bool(ann["destructiveHint"]));
            else Assert.False(ann.ContainsKey("destructiveHint"));
        }
    }

    [Fact]
    public async Task DispatcherToolsCallAndErrors()
    {
        var provider = new FakeProvider();
        var d = NewDispatcher(provider);
        var call = await Reply(d, Rpc(3, "tools/call", new JsonObject { ["name"] = "get_status", ["arguments"] = new JsonObject() }));
        var cr = (JsonObject)call!["result"]!;
        Assert.False(McpJson.Bool(cr["isError"]));
        Assert.NotNull(cr["structuredContent"]);
        Assert.Equal("text", McpJson.Str(cr["content"]![0]!["type"]));
        Assert.Single(provider.Calls);

        Assert.Equal(-32602, ErrorCode(await Reply(d, Rpc(4, "tools/call", new JsonObject { ["name"] = "rm_rf" }))));
        Assert.Equal(-32602, ErrorCode(await Reply(d, Rpc(4, "tools/call", new JsonObject()))));
        Assert.Equal(-32602, ErrorCode(await Reply(d, Rpc(5, "tools/call",
            new JsonObject { ["name"] = "get_status", ["arguments"] = new JsonArray(1, 2) }))));
        var nullArgs = await Reply(d, Rpc(5, "tools/call", new JsonObject { ["name"] = "get_status", ["arguments"] = null }));
        Assert.NotNull(nullArgs!["result"]);
        Assert.Equal(-32601, ErrorCode(await Reply(d, Rpc(6, "resources/list"))));
        Assert.Equal(-32600, ErrorCode(await Reply(d, new JsonObject { ["id"] = 7, ["method"] = "ping" })));
        Assert.Equal(-32600, ErrorCode(await Reply(d, Rpc(true, "ping"))));
        Assert.Equal(-32600, ErrorCode(await Reply(d, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = null, ["method"] = "ping" })));
        Assert.Equal(-32600, ErrorCode(await Reply(d, JsonValue.Create(3))));
        Assert.Equal(-32600, ErrorCode(await Reply(d, null)));
        var toolErr = await Reply(d, Rpc(8, "tools/call", new JsonObject { ["name"] = "take_screenshot" }));
        Assert.True(McpJson.Bool(toolErr!["result"]!["isError"]));
    }

    // ---- HTTP handler: Host, Origin, auth, sessions

    [Fact]
    public async Task HandlerSecurityAndSessions()
    {
        var d = NewDispatcher();
        var token = McpAuth.NewToken();
        var h = new McpHttpHandler(d, token);
        var bearer = $"Bearer {token}";

        var r = await h.HandleAsync(Request(auth: null, body: InitBody()));
        Assert.Equal(401, r.Status);
        Assert.True(r.Headers.ContainsKey("WWW-Authenticate"));
        Assert.Equal(401, (await h.HandleAsync(Request(auth: "Bearer nope", body: InitBody()))).Status);
        Assert.Equal(403, (await h.HandleAsync(Request(host: "evil.com", auth: bearer, body: InitBody()))).Status);
        Assert.Equal(403, (await h.HandleAsync(Request(host: null, auth: bearer, body: InitBody()))).Status);
        Assert.Equal(403, (await h.HandleAsync(Request(auth: bearer, origin: "https://evil.com", body: InitBody()))).Status);
        Assert.Equal(404, (await h.HandleAsync(Request(path: "/other", auth: bearer, body: InitBody()))).Status);
        r = await h.HandleAsync(Request("GET", auth: bearer));
        Assert.Equal(405, r.Status);
        Assert.Equal("POST, DELETE", r.Headers["Allow"]);
        Assert.Equal(400, (await h.HandleAsync(Request(auth: bearer, body: Rpc(2, "tools/list")))).Status);
        Assert.Equal(404, (await h.HandleAsync(Request(auth: bearer, session: "bogus", body: Rpc(2, "tools/list")))).Status);

        r = await h.HandleAsync(Request(auth: bearer, body: InitBody()));
        var sid = r.Headers.GetValueOrDefault("Mcp-Session-Id") ?? "";
        Assert.Equal(200, r.Status);
        Assert.Equal(64, sid.Length);
        Assert.Equal("2025-06-18", McpJson.Str(Obj(r)["result"]!["protocolVersion"]));

        r = await h.HandleAsync(Request(auth: bearer, session: sid, body: Rpc(null, "notifications/initialized")));
        Assert.Equal(202, r.Status);
        Assert.Empty(r.Body);

        r = await h.HandleAsync(Request(auth: bearer, session: sid,
            extra: new() { ["MCP-Protocol-Version"] = "2025-06-18" }, body: Rpc(2, "tools/list")));
        Assert.Equal(200, r.Status);
        Assert.Equal("application/json", r.Headers["Content-Type"]);

        r = await h.HandleAsync(Request(auth: bearer, session: sid, extra: new() { ["MCP-Protocol-Version"] = "1.0" }, body: Rpc(2, "ping")));
        Assert.Equal(400, r.Status);

        r = await h.HandleAsync(Request(auth: bearer, session: sid,
            body: new JsonArray(Rpc(9, "ping"), Rpc(null, "notifications/x"))));
        Assert.Equal(200, r.Status);
        Assert.Single((JsonArray)JsonNode.Parse(r.Body)!);

        r = await h.HandleAsync(Request(auth: bearer, session: sid, body: new JsonArray(InitBody())));
        Assert.Equal(400, r.Status);
        r = await h.HandleAsync(Request(auth: bearer, session: sid, body: new JsonArray()));
        Assert.Equal(400, r.Status);

        r = await h.HandleAsync(new McpHttpRequest("POST", "/mcp",
            new Dictionary<string, string> { ["host"] = "localhost", ["authorization"] = bearer, ["mcp-session-id"] = sid },
            Encoding.UTF8.GetBytes("{not json")));
        Assert.Equal(400, r.Status);
        Assert.Equal(-32700, McpJson.Int(Obj(r)["error"]!["code"]));

        r = await h.HandleAsync(new McpHttpRequest("POST", "/mcp",
            new Dictionary<string, string> { ["host"] = "localhost", ["authorization"] = bearer, ["mcp-session-id"] = sid },
            Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}")));
        Assert.Equal(-32700, McpJson.Int(Obj(r)["error"]!["code"]));

        r = await h.HandleAsync(Request(auth: bearer, session: sid, extra: new() { ["content-type"] = "text/plain" }, body: Rpc(1, "ping")));
        Assert.Equal(415, r.Status);

        h.ReplaceToken(McpAuth.NewToken());
        Assert.Equal(401, (await h.HandleAsync(Request(auth: bearer, session: sid, body: Rpc(1, "ping")))).Status);
        Assert.Equal(0, h.SessionCount);

        var h2 = new McpHttpHandler(d, token);
        r = await h2.HandleAsync(Request(auth: bearer, body: InitBody()));
        var sid2 = r.Headers["Mcp-Session-Id"];
        r = await h2.HandleAsync(Request("DELETE", auth: bearer, session: sid2));
        Assert.Equal(204, r.Status);
        Assert.Equal(0, h2.SessionCount);
        Assert.Equal(404, (await h2.HandleAsync(Request("DELETE", auth: bearer, session: sid2))).Status);
        Assert.Equal(400, (await h2.HandleAsync(Request("DELETE", auth: bearer))).Status);
    }

    [Fact]
    public async Task HandlerCapsSessions()
    {
        var token = McpAuth.NewToken();
        var h = new McpHttpHandler(NewDispatcher(), token);
        string? first = null;
        for (int i = 0; i < McpHttpHandler.MaxSessions + 5; i++)
        {
            var r = await h.HandleAsync(Request(auth: $"Bearer {token}", body: InitBody()));
            first ??= r.Headers["Mcp-Session-Id"];
        }
        Assert.Equal(McpHttpHandler.MaxSessions, h.SessionCount);
        Assert.Equal(404, (await h.HandleAsync(Request(auth: $"Bearer {token}", session: first, body: Rpc(1, "ping")))).Status);
    }

    // ---- Discovery file

    [Fact]
    public void DiscoveryFile()
    {
        using var tmp = new McpTempDir();
        var file = Path.Combine(tmp.Path, "FrameLine", "mcp.json");
        Assert.Equal(file, McpDiscovery.DefaultFile(tmp.Path));
        var token = McpAuth.NewToken();
        var identity = new McpAppIdentity("FrameLine", "com.saasempiree.frameline", "1.0.0", Environment.ProcessId,
            "frameline", "FrameLine", "Captures d'écran", "#FF453A");
        if (!OperatingSystem.IsWindows())
            Directory.CreateDirectory(Path.GetDirectoryName(file)!, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        McpDiscovery.Write(McpDiscovery.Document(51234, token, identity), file);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(file)!));
        }
        var back = JsonNode.Parse(File.ReadAllBytes(file))!;
        var server = back["mcpServers"]!["frameline"]!;
        Assert.Equal("http", McpJson.Str(server["type"]));
        Assert.Equal("http://127.0.0.1:51234/mcp", McpJson.Str(server["url"]));
        Assert.Equal($"Bearer {token}", McpJson.Str(server["headers"]!["Authorization"]));
        var app = back["app"]!;
        Assert.Equal(Environment.ProcessId, McpJson.Int(app["pid"]));
        Assert.Equal("com.saasempiree.frameline", McpJson.Str(app["bundleId"]));
        Assert.Equal("1.0.0", McpJson.Str(app["version"]));
        var emp = app["employee"]!;
        Assert.Equal("frameline", McpJson.Str(emp["name"]));
        Assert.Equal("FrameLine", McpJson.Str(emp["displayName"]));
        Assert.Equal("#FF453A", McpJson.Str(emp["color"]));
        Assert.NotNull(emp["description"]);
        Assert.Equal(new[] { "mcp.json" }, Directory.GetFiles(Path.GetDirectoryName(file)!).Select(Path.GetFileName));

        McpDiscovery.Write(McpDiscovery.Document(1, "x", identity), file);
        Assert.Equal("http://127.0.0.1:1/mcp", McpJson.Str(JsonNode.Parse(File.ReadAllBytes(file))!["mcpServers"]!["frameline"]!["url"]));
        Assert.False(McpDiscovery.RemoveIfOwned(file, Environment.ProcessId + 1));
        Assert.True(McpDiscovery.RemoveIfOwned(file, Environment.ProcessId));
        Assert.False(File.Exists(file));
        Assert.False(McpDiscovery.RemoveIfOwned(file, Environment.ProcessId));
        var cfg = McpDiscovery.ClientConfig(7, "t");
        Assert.False(cfg.ContainsKey("app"));
        Assert.True(cfg.ContainsKey("mcpServers"));
    }

    // ---- Capture folder helpers

    [Fact]
    public void CaptureFolderListAndResolve()
    {
        using var tmp = new McpTempDir();
        var caps = Path.Combine(tmp.Path, "caps");
        Directory.CreateDirectory(caps);
        var a = Path.Combine(caps, "a.png");
        var b = Path.Combine(caps, "b.mov");
        File.WriteAllText(a, "aa");
        File.WriteAllText(b, "bbbb");
        File.WriteAllText(Path.Combine(caps, "notes.txt"), "c");
        File.WriteAllText(Path.Combine(caps, ".h.png"), "h");
        Directory.CreateDirectory(Path.Combine(caps, "dir.png"));
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddSeconds(-100));

        var all = CaptureFolder.List(caps);
        Assert.Equal(new[] { "b.mov", "a.png" }, all.Select(i => Path.GetFileName(i.Path)));
        Assert.True(all[0].IsVideo);
        Assert.False(all[1].IsVideo);
        Assert.Equal(4, all[0].Size);
        Assert.Equal(new[] { "a.png" }, CaptureFolder.List(caps, CaptureFolder.Kind.Image).Select(i => Path.GetFileName(i.Path)));
        Assert.Single(CaptureFolder.List(caps, limit: 1));
        Assert.Empty(CaptureFolder.List(Path.Combine(tmp.Path, "missing")));
        var json = all[0].ToJson();
        Assert.Equal("video", McpJson.Str(json["kind"]));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", McpJson.Str(json["modified"]));
        Assert.Equal(4, McpJson.Int(json["size_bytes"]));

        Assert.NotNull(CaptureFolder.Resolve(a, caps));
        Assert.Equal("b.mov", Path.GetFileName(CaptureFolder.Resolve("b.mov", caps)));
        Assert.Equal("b.mov", Path.GetFileName(CaptureFolder.Resolve("  b.mov\n", caps)));
        Assert.Null(CaptureFolder.Resolve("../caps/../../etc/passwd", caps));
        Assert.Null(CaptureFolder.Resolve("../caps/../secret.png", caps));
        Assert.Null(CaptureFolder.Resolve(OperatingSystem.IsWindows() ? @"C:\Windows\win.ini" : "/etc/hosts", caps));
        Assert.Null(CaptureFolder.Resolve("notes.txt", caps));
        Assert.Null(CaptureFolder.Resolve("zzz.png", caps));
        Assert.Null(CaptureFolder.Resolve("dir.png", caps));
        Assert.Null(CaptureFolder.Resolve("  ", caps));
        Assert.Null(CaptureFolder.Resolve("a.png\0", caps));
        Assert.Null(CaptureFolder.Resolve("a.png:stream", caps));

        var outside = Path.Combine(tmp.Path, "secret.png");
        File.WriteAllText(outside, "s");
        try
        {
            File.CreateSymbolicLink(Path.Combine(caps, "link.png"), outside);
            Assert.Null(CaptureFolder.Resolve("link.png", caps));
            Assert.DoesNotContain(CaptureFolder.List(caps), i => i.Path.EndsWith("link.png"));
        }
        catch (IOException) when (OperatingSystem.IsWindows()) { }       // no symlink privilege
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { }
    }

    [Fact]
    public void ResolveThroughLinkedFolder()
    {
        if (OperatingSystem.IsWindows()) return;                           // symlink creation needs a privilege there
        using var tmp = new McpTempDir();
        var real = Path.Combine(tmp.Path, "real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "x.png"), "x");
        var link = Path.Combine(tmp.Path, "link");
        Directory.CreateSymbolicLink(link, real);
        Assert.NotNull(CaptureFolder.Resolve(Path.Combine(real, "x.png"), link));
        Assert.NotNull(CaptureFolder.Resolve(Path.Combine(link, "x.png"), real));
        Assert.NotNull(CaptureFolder.Resolve("x.png", link));
    }

    [Fact]
    public void ImageHeaderReadsPngSize()
    {
        using var tmp = new McpTempDir();
        var png = tmp.File("p.png");
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52 }.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), 640);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), 480);
        File.WriteAllBytes(png, bytes);
        Assert.Equal((640, 480), ImageHeader.TryGetPixelSize(png));

        var jpg = tmp.File("j.jpg");
        File.WriteAllBytes(jpg, new byte[]
        {
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00,             // SOI, APP0 (2 payload bytes)
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0xE0, 0x02, 0x80,       // SOF0 480 x 640
        });
        Assert.Equal((640, 480), ImageHeader.TryGetPixelSize(jpg));
        File.WriteAllText(tmp.File("t.png"), "png");
        Assert.Null(ImageHeader.TryGetPixelSize(tmp.File("t.png")));
    }

    // ---- Tool handlers (platform-neutral logic) against a fake host

    private static async Task<JsonObject> Call(FrameLineMcpTools tools, string name, JsonObject? args = null) =>
        (await tools.CallAsync(name, args ?? new JsonObject(), CancellationToken.None)).ToJson();

    private static bool IsError(JsonObject r) => McpJson.Bool(r["isError"]) == true;
    private static string Text(JsonObject r) => McpJson.Str(r["content"]![0]!["text"])!;

    [Fact]
    public async Task ToolsReadOnly()
    {
        using var tmp = new McpTempDir();
        var host = new FakeHost(tmp.Path);
        var tools = new FrameLineMcpTools(host);

        var empty = await Call(tools, "ocr_capture");
        Assert.True(IsError(empty));
        Assert.Contains("There is no image capture in", Text(empty));
        Assert.Contains("There is no capture in", Text(await Call(tools, "open_in_editor")));

        File.WriteAllText(tmp.File("a.png"), "a");
        File.WriteAllText(tmp.File("b.mp4"), "bb");
        File.SetLastWriteTimeUtc(tmp.File("a.png"), DateTime.UtcNow.AddMinutes(-1));

        var status = await Call(tools, "get_status");
        Assert.False(IsError(status));
        var sc = status["structuredContent"]!;
        Assert.Equal("FrameLine", McpJson.Str(sc["app"]));
        Assert.Equal("test", McpJson.Str(sc["version"]));
        Assert.Equal(tmp.Path, McpJson.Str(sc["capture_folder"]));
        Assert.Equal("png", McpJson.Str(sc["image_format"]));
        Assert.False(McpJson.Bool(sc["recording"]));
        Assert.Equal(McpJson.String(sc), Text(status));

        var list = (await Call(tools, "list_recent_captures", new JsonObject { ["limit"] = 500, ["kind"] = "video" }))["structuredContent"]!;
        Assert.Equal(1, McpJson.Int(list["count"]));
        Assert.Equal(tmp.Path, McpJson.Str(list["capture_folder"]));
        Assert.True(IsError(await Call(tools, "list_recent_captures", new JsonObject { ["kind"] = "gif" })));
        Assert.Equal(1, McpJson.Int((await Call(tools, "list_recent_captures", new JsonObject { ["limit"] = 0 }))["structuredContent"]!["count"]));

        var ocr = await Call(tools, "ocr_capture");
        Assert.Equal(tmp.File("a.png"), McpJson.Str(ocr["structuredContent"]!["path"]));
        Assert.Equal(2, McpJson.Int(ocr["structuredContent"]!["line_count"]));
        var notImage = await Call(tools, "ocr_capture", new JsonObject { ["path"] = "b.mp4" });
        Assert.Equal("b.mp4 is not an image capture.", Text(notImage));
        Assert.Equal("`path` must be a string.", Text(await Call(tools, "ocr_capture", new JsonObject { ["path"] = 3 })));
        Assert.Contains("Use list_recent_captures", Text(await Call(tools, "ocr_capture", new JsonObject { ["path"] = "../x.png" })));

        var copy = (await Call(tools, "copy_capture_to_clipboard"))["structuredContent"]!;
        Assert.Equal("file", McpJson.Str(copy["copied"]));
        Assert.Equal(tmp.File("b.mp4"), host.Copied.Single());
        Assert.Equal("image", McpJson.Str((await Call(tools, "copy_capture_to_clipboard", new JsonObject { ["path"] = "a.png" }))["structuredContent"]!["copied"]));
        Assert.Equal("library", McpJson.Str((await Call(tools, "open_in_editor"))["structuredContent"]!["opened"]));
        Assert.Equal("editor", McpJson.Str((await Call(tools, "open_in_editor", new JsonObject { ["path"] = "a.png" }))["structuredContent"]!["opened"]));
    }

    [Fact]
    public async Task ToolsScreenshotValidation()
    {
        using var tmp = new McpTempDir();
        var host = new FakeHost(tmp.Path);
        var tools = new FrameLineMcpTools(host);
        host.Windows.Add(new McpWindowInfo(100, "Notepad", 4242, "Untitled", new McpRect(-500, 10, 400, 300), "notepad"));
        host.Windows.Add(new McpWindowInfo(101, "Tiny", 4243, "", new McpRect(0, 0, 20, 20)));
        host.Windows.Add(new McpWindowInfo(102, "Me", Environment.ProcessId, "", new McpRect(0, 0, 400, 400)));
        host.Windows.Add(new McpWindowInfo(103, "Google Chrome", 77, "Tab", new McpRect(0, 0, 800, 600), "chrome"));

        var windows = (await Call(tools, "list_windows"))["structuredContent"]!;
        Assert.Equal(2, McpJson.Int(windows["count"]));
        Assert.Equal(-500, McpJson.Int(windows["windows"]![0]!["bounds"]!["x"]));
        Assert.Equal(1, McpJson.Int((await Call(tools, "list_windows", new JsonObject { ["app_name"] = "CHROME.exe" }))["structuredContent"]!["count"]));

        Assert.Equal("mode must be \"full_screen\", \"window\" or \"region\".", Text(await Call(tools, "take_screenshot", new JsonObject { ["mode"] = "x" })));
        Assert.Equal("display must be between 1 and 2 (1 = primary display).", Text(await Call(tools, "take_screenshot", new JsonObject { ["display"] = 3 })));
        Assert.Contains("needs numeric x, y, width and height", Text(await Call(tools, "take_screenshot", new JsonObject { ["mode"] = "region", ["x"] = 1 })));
        Assert.Contains("at least 1 pixel", Text(await Call(tools, "take_screenshot",
            new JsonObject { ["mode"] = "region", ["x"] = 1, ["y"] = 1, ["width"] = 0.5, ["height"] = 10 })));
        Assert.Contains("window_id 999", Text(await Call(tools, "take_screenshot", new JsonObject { ["mode"] = "window", ["window_id"] = 999 })));
        Assert.Contains("\"Safari\"", Text(await Call(tools, "take_screenshot", new JsonObject { ["mode"] = "window", ["app_name"] = "Safari" })));
        Assert.Equal("delay_seconds must be between 0 and 10.", Text(await Call(tools, "take_screenshot", new JsonObject { ["delay_seconds"] = 11 })));

        // Host produced nothing: error, after the target was computed.
        var failed = await Call(tools, "take_screenshot", new JsonObject { ["display"] = 2 });
        Assert.True(IsError(failed));
        Assert.Equal(new McpCaptureTarget(McpCaptureKind.FullScreen, Display: 2), host.LastTarget);

        var shot = tmp.File("FrameLine shot.png");
        var png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        png[19] = 200; png[23] = 100;
        File.WriteAllBytes(shot, png);
        host.CaptureResult = shot;
        host.ForegroundProcessId = 77;
        var ok = await Call(tools, "take_screenshot", new JsonObject { ["mode"] = "window" });
        var sc = ok["structuredContent"]!;
        Assert.False(IsError(ok));
        Assert.Equal(103, McpJson.Int(sc["window"]!["window_id"]));
        Assert.Equal(new McpCaptureTarget(McpCaptureKind.Window, WindowId: 103), host.LastTarget);
        Assert.Equal("png", McpJson.Str(sc["format"]));
        Assert.Equal(200, McpJson.Int(sc["width"]));
        Assert.Equal(100, McpJson.Int(sc["height"]));
        Assert.Equal(shot, McpJson.Str(sc["path"]));

        var region = await Call(tools, "take_screenshot", new JsonObject
        { ["mode"] = "region", ["x"] = -100.5, ["y"] = 20, ["width"] = 300, ["height"] = 200, ["include_image"] = true });
        Assert.Equal(-100.5, McpJson.Double(region["structuredContent"]!["region"]!["x"]));
        Assert.Single((JsonArray)region["content"]!);                   // fake host has no preview
    }

    [Fact]
    public async Task ToolsRecording()
    {
        using var tmp = new McpTempDir();
        var host = new FakeHost(tmp.Path);
        var tools = new FrameLineMcpTools(host);

        Assert.Equal("No screen recording is running.", Text(await Call(tools, "stop_recording")));
        Assert.Equal("The region is not on any display.", Text(await Call(tools, "start_recording",
            new JsonObject { ["mode"] = "region", ["x"] = 5000, ["y"] = 0, ["width"] = 10, ["height"] = 10 })));
        Assert.Equal("mode must be \"full_screen\" or \"region\".", Text(await Call(tools, "start_recording", new JsonObject { ["mode"] = "window" })));

        var start = await Call(tools, "start_recording");
        Assert.False(IsError(start));
        Assert.Equal("recording", McpJson.Str(start["structuredContent"]!["status"]));
        var again = await Call(tools, "start_recording");
        Assert.StartsWith("A recording is already running (", Text(again));
        Assert.Equal(host.CurrentRecordingPath, McpJson.Str((await Call(tools, "get_status"))["structuredContent"]!["recording_path"]));

        var stop = (await Call(tools, "stop_recording"))["structuredContent"]!;
        Assert.Equal("stopped", McpJson.Str(stop["status"]));
        Assert.Equal(5, McpJson.Int(stop["size_bytes"]));
        Assert.Equal(1.23, McpJson.Double(stop["duration_seconds"]));
    }

    [Fact]
    public void ServerAndControllerLifecycle()
    {
        using var tmp = new McpTempDir();
        var file = Path.Combine(tmp.Path, "appdata", "FrameLine", "mcp.json");
        var server = new McpServer(file);
        var prefs = new Preferences(Path.Combine(tmp.Path, "settings.json"));
        var controller = new McpController(prefs, server);
        var seen = new List<McpStatus>();
        controller.StatusChanged += s => seen.Add(s);

        controller.Start();                                               // no host yet: nothing happens
        Assert.False(server.IsRunning);

        controller.StartIfEnabled(new FakeHost(tmp.Path));
        if (!prefs.McpServerEnabled) controller.Start();
        Assert.True(server.IsRunning);
        Assert.True(server.Port > 0);
        Assert.Equal(new McpStatus(McpStatusKind.Running, server.Port), controller.Status);
        Assert.Contains(seen, s => s.Kind == McpStatusKind.Starting);
        Assert.True(File.Exists(file));
        var config = JsonNode.Parse(controller.ClientConfigJson()!)!;
        Assert.Equal($"Bearer {server.Token}", McpJson.Str(config["mcpServers"]!["frameline"]!["headers"]!["Authorization"]));

        var oldToken = server.Token;
        controller.RegenerateToken();
        Assert.NotEqual(oldToken, server.Token);
        Assert.Contains(server.Token, File.ReadAllText(file));

        controller.OnPreferenceChanged("SomethingElse");
        Assert.True(server.IsRunning);
        controller.Stop();
        Assert.False(server.IsRunning);
        Assert.False(File.Exists(file));
        Assert.Equal(McpStatus.Stopped, controller.Status);
        Assert.Null(controller.ClientConfigJson());
        controller.Shutdown();
    }
}
