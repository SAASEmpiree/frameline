import Foundation

// Unit tests of the MCP core (JSON-RPC, HTTP parsing, auth, Host/Origin checks, sessions,
// discovery file, capture-folder helpers). Plain executable, no XCTest (Command Line Tools
// only): scripts/test-mcp.sh compiles it with Sources/FrameLine/MCP/*.swift minus the app files.

final class FakeProvider: MCPToolProvider {
    var tools: [MCPTool] { FrameLineMCPCatalog.tools }
    var calls: [(String, [String: Any])] = []
    func call(_ name: String, arguments: [String: Any]) async -> MCPToolResult {
        calls.append((name, arguments))
        if name == "get_status" { return .object(["recording": false]) }
        return .error("not available in tests")
    }
}

@main
enum MCPCoreTests {
    static var count = 0
    static func check(_ name: String, _ ok: Bool, file: StaticString = #file, line: UInt = #line) {
        if !ok { print("FAIL: \(name) (line \(line))"); exit(1) }
        count += 1
    }

    static func json(_ data: Data) -> [String: Any] {
        (try? JSONSerialization.jsonObject(with: data) as? [String: Any]) ?? [:]
    }

    static func request(_ method: String = "POST", path: String = "/mcp", host: String? = "127.0.0.1:4242",
                        auth: String? = nil, session: String? = nil, origin: String? = nil,
                        extra: [String: String] = [:], body: Any? = nil) -> HTTPRequest {
        var h: [String: String] = ["content-type": "application/json"]
        if let host { h["host"] = host }
        if let auth { h["authorization"] = auth }
        if let session { h["mcp-session-id"] = session }
        if let origin { h["origin"] = origin }
        for (k, v) in extra { h[k.lowercased()] = v }
        return HTTPRequest(method: method, path: path, headers: h, body: body.map { MCPJSON.data($0) } ?? Data())
    }

    static func main() async {
        // --- Token & auth
        let t1 = MCPAuth.newToken(), t2 = MCPAuth.newToken()
        check("token is 64 hex chars (256 bits)", t1.count == 64 && t1.allSatisfy(\.isHexDigit))
        check("tokens differ", t1 != t2)
        check("ct equal", MCPAuth.constantTimeEquals("abc", "abc"))
        check("ct differ", !MCPAuth.constantTimeEquals("abc", "abd"))
        check("ct length", !MCPAuth.constantTimeEquals("abc", "abcd"))
        check("ct empty vs non", !MCPAuth.constantTimeEquals("", "a"))
        check("bearer ok", MCPAuth.isAuthorized(header: "Bearer \(t1)", token: t1))
        check("bearer case-insensitive scheme", MCPAuth.isAuthorized(header: "bearer \(t1)", token: t1))
        check("bearer wrong", !MCPAuth.isAuthorized(header: "Bearer \(t2)", token: t1))
        check("bearer missing", !MCPAuth.isAuthorized(header: nil, token: t1))
        check("basic refused", !MCPAuth.isAuthorized(header: "Basic \(t1)", token: t1))
        check("bare token refused", !MCPAuth.isAuthorized(header: t1, token: t1))
        check("empty expected token never matches", !MCPAuth.isAuthorized(header: "Bearer ", token: ""))

        // --- Host / Origin
        check("host 127.0.0.1:port", MCPAuth.isAllowedHost("127.0.0.1:5555"))
        check("host localhost", MCPAuth.isAllowedHost("localhost"))
        check("host LOCALHOST:1", MCPAuth.isAllowedHost("LOCALHOST:1"))
        check("host evil", !MCPAuth.isAllowedHost("evil.com"))
        check("host rebinding name", !MCPAuth.isAllowedHost("127.0.0.1.evil.com:80"))
        check("host missing", !MCPAuth.isAllowedHost(nil))
        check("host bad port", !MCPAuth.isAllowedHost("localhost:abc"))
        check("host 0.0.0.0", !MCPAuth.isAllowedHost("0.0.0.0:1"))
        check("origin absent ok", MCPAuth.isAllowedOrigin(nil))
        check("origin loopback ok", MCPAuth.isAllowedOrigin("http://localhost:3000"))
        check("origin foreign refused", !MCPAuth.isAllowedOrigin("https://evil.com"))
        check("origin null refused", !MCPAuth.isAllowedOrigin("null"))

        // --- HTTP parser
        let raw = "POST /mcp?x=1 HTTP/1.1\r\nHost: 127.0.0.1:1\r\nContent-Length: 2\r\nAuthorization: Bearer x\r\n\r\n{}"
        if case .complete(let r) = HTTPRequestParser.parse(Data(raw.utf8)) {
            check("parse method", r.method == "POST")
            check("parse path strips query", r.path == "/mcp")
            check("parse header lowercased", r.header("Authorization") == "Bearer x")
            check("parse body", r.body == Data("{}".utf8))
        } else { check("parse complete", false) }
        if case .incomplete = HTTPRequestParser.parse(Data("POST /mcp HTTP/1.1\r\nContent-Length: 10\r\n\r\n{}".utf8)) {
            check("partial body incomplete", true)
        } else { check("partial body incomplete", false) }
        if case .incomplete = HTTPRequestParser.parse(Data("POST /mcp HTTP/1.1\r\nHost: a".utf8)) {
            check("partial head incomplete", true)
        } else { check("partial head incomplete", false) }
        if case .invalid(let s, _) = HTTPRequestParser.parse(Data("GARBAGE\r\n\r\n".utf8)) { check("bad request line 400", s == 400) }
        else { check("bad request line", false) }
        if case .invalid(let s, _) = HTTPRequestParser.parse(Data("POST /mcp HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n".utf8)) {
            check("chunked 411", s == 411)
        } else { check("chunked refused", false) }
        if case .invalid(let s, _) = HTTPRequestParser.parse(Data("POST /mcp HTTP/1.1\r\nContent-Length: 99999999\r\n\r\n".utf8)) {
            check("too large 413", s == 413)
        } else { check("too large refused", false) }
        if case .invalid(let s, _) = HTTPRequestParser.parse(Data("POST /mcp HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n".utf8)) {
            check("duplicate host 400", s == 400)
        } else { check("duplicate host refused", false) }
        if case .invalid(let s, _) = HTTPRequestParser.parse(Data(String(repeating: "a", count: 20_000).utf8)) {
            check("huge head 431", s == 431)
        } else { check("huge head refused", false) }
        let ser = String(decoding: HTTPResponse.json(200, ["a": 1]).serialized(), as: UTF8.self)
        check("response status line", ser.hasPrefix("HTTP/1.1 200 OK\r\n"))
        check("response length", ser.contains("Content-Length: 7\r\n"))
        check("response close", ser.contains("Connection: close\r\n"))

        // --- JSON-RPC dispatcher
        let provider = FakeProvider()
        let d = MCPDispatcher(serverName: "frameline", serverTitle: "FrameLine", serverVersion: "9.9",
                              instructions: "hi", provider: provider)
        func reply(_ m: Any) async -> [String: Any]? {
            if case .response(let r) = await d.handle(m) { return r }
            return nil
        }
        let ini = await reply(["jsonrpc": "2.0", "id": 1, "method": "initialize",
                               "params": ["protocolVersion": "2025-06-18", "capabilities": [:],
                                          "clientInfo": ["name": "t", "version": "1"]]])
        let res = ini?["result"] as? [String: Any]
        check("initialize version echoed", res?["protocolVersion"] as? String == "2025-06-18")
        check("initialize tools capability", (res?["capabilities"] as? [String: Any])?["tools"] != nil)
        check("initialize serverInfo", (res?["serverInfo"] as? [String: Any])?["name"] as? String == "frameline")
        check("initialize id echoed", MCPJSON.int(ini?["id"]) == 1)
        check("old version accepted", MCPDispatcher.negotiatedVersion(requested: "2025-03-26") == "2025-03-26")
        check("unknown version → newest", MCPDispatcher.negotiatedVersion(requested: "1999-01-01") == "2025-06-18")
        if case .accepted = await d.handle(["jsonrpc": "2.0", "method": "notifications/initialized"]) {
            check("notification accepted", true)
        } else { check("notification accepted", false) }
        let ping = await reply(["jsonrpc": "2.0", "id": "p", "method": "ping"])
        check("ping empty result", (ping?["result"] as? [String: Any])?.isEmpty == true && ping?["id"] as? String == "p")
        let list = await reply(["jsonrpc": "2.0", "id": 2, "method": "tools/list"])
        let tools = ((list?["result"] as? [String: Any])?["tools"] as? [[String: Any]]) ?? []
        let names = Set(tools.compactMap { $0["name"] as? String })
        let expected: Set<String> = ["take_screenshot", "start_recording", "stop_recording", "get_status",
                                     "list_recent_captures", "list_windows", "ocr_capture",
                                     "copy_capture_to_clipboard", "open_in_editor"]
        check("tools/list has every tool", names == expected)
        for tool in tools {
            let n = tool["name"] as? String ?? "?"
            let ann = tool["annotations"] as? [String: Any] ?? [:]
            check("\(n) snake_case", n.allSatisfy { $0.isLowercase || $0 == "_" })
            check("\(n) has object schema", (tool["inputSchema"] as? [String: Any])?["type"] as? String == "object")
            check("\(n) has description", ((tool["description"] as? String) ?? "").count > 40)
            check("\(n) openWorld false", MCPJSON.bool(ann["openWorldHint"]) == false)
            let ro = MCPJSON.bool(ann["readOnlyHint"]) == true
            let shouldBeRO = ["get_status", "list_recent_captures", "list_windows", "ocr_capture"].contains(n)
            check("\(n) readOnlyHint", ro == shouldBeRO)
            if !ro { check("\(n) not destructive", MCPJSON.bool(ann["destructiveHint"]) == false) }
        }
        let call = await reply(["jsonrpc": "2.0", "id": 3, "method": "tools/call",
                                "params": ["name": "get_status", "arguments": [:]]])
        let cr = call?["result"] as? [String: Any]
        check("tools/call result", MCPJSON.bool(cr?["isError"]) == false && cr?["structuredContent"] != nil)
        check("tools/call text content", ((cr?["content"] as? [[String: Any]])?.first?["type"] as? String) == "text")
        let unknown = await reply(["jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": ["name": "rm_rf"]])
        check("unknown tool -32602", MCPJSON.int((unknown?["error"] as? [String: Any])?["code"]) == -32602)
        let badArgs = await reply(["jsonrpc": "2.0", "id": 5, "method": "tools/call",
                                   "params": ["name": "get_status", "arguments": [1, 2]]])
        check("array arguments -32602", MCPJSON.int((badArgs?["error"] as? [String: Any])?["code"]) == -32602)
        let nf = await reply(["jsonrpc": "2.0", "id": 6, "method": "resources/list"])
        check("unknown method -32601", MCPJSON.int((nf?["error"] as? [String: Any])?["code"]) == -32601)
        let inv = await reply(["id": 7, "method": "ping"])
        check("missing jsonrpc -32600", MCPJSON.int((inv?["error"] as? [String: Any])?["code"]) == -32600)
        let boolID = await reply(["jsonrpc": "2.0", "id": true, "method": "ping"])
        check("boolean id -32600", MCPJSON.int((boolID?["error"] as? [String: Any])?["code"]) == -32600)
        let toolErr = await reply(["jsonrpc": "2.0", "id": 8, "method": "tools/call", "params": ["name": "take_screenshot"]])
        check("tool failure is isError result", MCPJSON.bool((toolErr?["result"] as? [String: Any])?["isError"]) == true)

        // --- HTTP handler: Host, Origin, auth, sessions
        let token = MCPAuth.newToken()
        let h = MCPHTTPHandler(dispatcher: d, token: token)
        let bearer = "Bearer \(token)"
        let initBody: [String: Any] = ["jsonrpc": "2.0", "id": 1, "method": "initialize",
                                       "params": ["protocolVersion": "2025-06-18", "capabilities": [:],
                                                  "clientInfo": ["name": "t", "version": "1"]]]
        var r = await h.handle(request(auth: nil, body: initBody))
        check("401 without token", r.status == 401 && r.headers["WWW-Authenticate"] != nil)
        r = await h.handle(request(auth: "Bearer nope", body: initBody))
        check("401 wrong token", r.status == 401)
        r = await h.handle(request(host: "evil.com", auth: bearer, body: initBody))
        check("403 foreign host (even with token)", r.status == 403)
        r = await h.handle(request(host: nil, auth: bearer, body: initBody))
        check("403 missing host", r.status == 403)
        r = await h.handle(request(auth: bearer, origin: "https://evil.com", body: initBody))
        check("403 foreign origin", r.status == 403)
        r = await h.handle(request(path: "/other", auth: bearer, body: initBody))
        check("404 other path", r.status == 404)
        r = await h.handle(request("GET", auth: bearer))
        check("405 GET (no SSE)", r.status == 405)
        r = await h.handle(request(auth: bearer, body: ["jsonrpc": "2.0", "id": 2, "method": "tools/list"]))
        check("400 without session", r.status == 400)
        r = await h.handle(request(auth: bearer, session: "bogus", body: ["jsonrpc": "2.0", "id": 2, "method": "tools/list"]))
        check("404 unknown session", r.status == 404)
        r = await h.handle(request(auth: bearer, body: initBody))
        let sid = r.headers["Mcp-Session-Id"] ?? ""
        check("initialize 200 + session id", r.status == 200 && sid.count == 64)
        check("initialize JSON body", (json(r.body)["result"] as? [String: Any])?["protocolVersion"] as? String == "2025-06-18")
        r = await h.handle(request(auth: bearer, session: sid, body: ["jsonrpc": "2.0", "method": "notifications/initialized"]))
        check("202 for notification", r.status == 202 && r.body.isEmpty)
        r = await h.handle(request(auth: bearer, session: sid, extra: ["MCP-Protocol-Version": "2025-06-18"],
                                   body: ["jsonrpc": "2.0", "id": 2, "method": "tools/list"]))
        check("tools/list over HTTP", r.status == 200 && r.headers["Content-Type"] == "application/json")
        r = await h.handle(request(auth: bearer, session: sid, extra: ["MCP-Protocol-Version": "1.0"],
                                   body: ["jsonrpc": "2.0", "id": 2, "method": "ping"]))
        check("400 bad protocol version header", r.status == 400)
        r = await h.handle(request(auth: bearer, session: sid,
                                   body: [["jsonrpc": "2.0", "id": 9, "method": "ping"],
                                          ["jsonrpc": "2.0", "method": "notifications/x"]]))
        let batch = (try? JSONSerialization.jsonObject(with: r.body)) as? [[String: Any]]
        check("batch returns array of replies", r.status == 200 && batch?.count == 1)
        r = await h.handle(HTTPRequest(method: "POST", path: "/mcp",
                                       headers: ["host": "localhost", "authorization": bearer, "mcp-session-id": sid],
                                       body: Data("{not json".utf8)))
        check("parse error 400 -32700", r.status == 400 && MCPJSON.int((json(r.body)["error"] as? [String: Any])?["code"]) == -32700)
        r = await h.handle(request(auth: bearer, session: sid, extra: ["content-type": "text/plain"],
                                   body: ["jsonrpc": "2.0", "id": 1, "method": "ping"]))
        check("415 wrong content type", r.status == 415)
        h.replaceToken(MCPAuth.newToken())
        r = await h.handle(request(auth: bearer, session: sid, body: ["jsonrpc": "2.0", "id": 1, "method": "ping"]))
        check("old token refused after regeneration", r.status == 401)
        check("sessions cleared on regeneration", h.sessionCount == 0)
        let h2 = MCPHTTPHandler(dispatcher: d, token: token)
        r = await h2.handle(request(auth: bearer, body: initBody))
        let sid2 = r.headers["Mcp-Session-Id"] ?? ""
        r = await h2.handle(request("DELETE", auth: bearer, session: sid2))
        check("DELETE ends session", r.status == 204 && h2.sessionCount == 0)

        // --- Discovery file
        let tmp = FileManager.default.temporaryDirectory
            .appendingPathComponent("frameline-mcp-tests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: tmp) }
        let file = tmp.appendingPathComponent("Library/Application Support/FrameLine/mcp.json")
        check("default path", MCPDiscovery.defaultFile(home: tmp).path == file.path)
        let identity = MCPAppIdentity(name: "FrameLine", bundleId: "com.saasempiree.frameline", version: "1.0.0",
                                      pid: getpid(), employeeName: "frameline", displayName: "FrameLine",
                                      description: "Captures d'écran", color: "#FF453A")
        let doc = MCPDiscovery.document(port: 51234, token: token, app: identity)
        do {
            // Pre-create the directory world-readable: the writer must tighten it.
            try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true,
                                                    attributes: [.posixPermissions: 0o755])
            try MCPDiscovery.write(doc, to: file)
        } catch { check("discovery write: \(error)", false) }
        let fattr = try? FileManager.default.attributesOfItem(atPath: file.path)
        let dattr = try? FileManager.default.attributesOfItem(atPath: file.deletingLastPathComponent().path)
        check("file 0600", (fattr?[.posixPermissions] as? NSNumber)?.intValue == 0o600)
        check("dir 0700", (dattr?[.posixPermissions] as? NSNumber)?.intValue == 0o700)
        let back = json((try? Data(contentsOf: file)) ?? Data())
        let server = ((back["mcpServers"] as? [String: Any])?["frameline"] as? [String: Any]) ?? [:]
        check("type http", server["type"] as? String == "http")
        check("url", server["url"] as? String == "http://127.0.0.1:51234/mcp")
        check("auth header", (server["headers"] as? [String: Any])?["Authorization"] as? String == "Bearer \(token)")
        let app = back["app"] as? [String: Any] ?? [:]
        check("app pid", MCPJSON.int(app["pid"]) == Int(getpid()))
        check("app bundle", app["bundleId"] as? String == "com.saasempiree.frameline")
        let emp = app["employee"] as? [String: Any] ?? [:]
        check("employee fields", emp["name"] as? String == "frameline" && emp["displayName"] as? String == "FrameLine"
              && emp["color"] as? String == "#FF453A" && emp["description"] != nil)
        let leftovers = (try? FileManager.default.contentsOfDirectory(atPath: file.deletingLastPathComponent().path)) ?? []
        check("no temp file left", leftovers == ["mcp.json"])
        do { try MCPDiscovery.write(MCPDiscovery.document(port: 1, token: "x", app: identity), to: file) }
        catch { check("rewrite: \(error)", false) }
        check("rewrite replaces", (((json((try? Data(contentsOf: file)) ?? Data())["mcpServers"] as? [String: Any])?["frameline"]
                                     as? [String: Any])?["url"] as? String) == "http://127.0.0.1:1/mcp")
        check("not removed for another pid", !MCPDiscovery.removeIfOwned(file: file, pid: getpid() + 1))
        check("removed for our pid", MCPDiscovery.removeIfOwned(file: file, pid: getpid()))
        check("file gone", !FileManager.default.fileExists(atPath: file.path))
        let cfg = MCPDiscovery.clientConfig(port: 7, token: "t")
        check("client config has no app block", cfg["app"] == nil && cfg["mcpServers"] != nil)

        // --- Capture folder helpers
        let caps = tmp.appendingPathComponent("caps", isDirectory: true)
        try? FileManager.default.createDirectory(at: caps, withIntermediateDirectories: true)
        let a = caps.appendingPathComponent("a.png"), b = caps.appendingPathComponent("b.mov")
        let c = caps.appendingPathComponent("notes.txt"), hidden = caps.appendingPathComponent(".h.png")
        for (u, s) in [(a, "aa"), (b, "bbbb"), (c, "c"), (hidden, "h")] { try? Data(s.utf8).write(to: u) }
        try? FileManager.default.setAttributes([.modificationDate: Date(timeIntervalSinceNow: -100)], ofItemAtPath: a.path)
        let all = CaptureFolder.list(directory: caps)
        check("list skips non-captures + hidden", all.map { $0.url.lastPathComponent } == ["b.mov", "a.png"])
        check("list kinds", all.first?.isVideo == true && all.last?.isVideo == false)
        check("list size", all.first?.size == 4)
        check("list filter image", CaptureFolder.list(directory: caps, kind: .image).map { $0.url.lastPathComponent } == ["a.png"])
        check("list limit", CaptureFolder.list(directory: caps, limit: 1).count == 1)
        check("item json", (all.first?.json["kind"] as? String) == "video" && all.first?.json["modified"] is String)
        check("resolve absolute", CaptureFolder.resolve(a.path, in: caps) != nil)
        check("resolve bare name", CaptureFolder.resolve("b.mov", in: caps)?.lastPathComponent == "b.mov")
        check("resolve refuses ..", CaptureFolder.resolve("../caps/../../etc/passwd", in: caps) == nil)
        check("resolve refuses outside", CaptureFolder.resolve("/etc/hosts", in: caps) == nil)
        check("resolve refuses non-capture", CaptureFolder.resolve("notes.txt", in: caps) == nil)
        check("resolve refuses missing", CaptureFolder.resolve("zzz.png", in: caps) == nil)
        let outside = tmp.appendingPathComponent("secret.png")
        try? Data("s".utf8).write(to: outside)
        try? FileManager.default.createSymbolicLink(at: caps.appendingPathComponent("link.png"), withDestinationURL: outside)
        check("resolve refuses symlink escaping", CaptureFolder.resolve("link.png", in: caps) == nil)
        check("resolve refuses empty", CaptureFolder.resolve("  ", in: caps) == nil)

        print("MCPCoreTests: \(count) checks passed")
    }
}
