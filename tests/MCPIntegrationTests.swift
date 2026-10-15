import Foundation

// Integration test: starts the real MCPServer (NWListener on 127.0.0.1, random port) with the
// real tool catalog and a provider whose list_recent_captures reads a temporary capture folder,
// then talks raw HTTP over a socket: initialize, notifications/initialized, tools/list,
// tools/call list_recent_captures, 401 without token, 403 with a foreign Host.

final class FolderProvider: MCPToolProvider {
    let folder: URL
    init(folder: URL) { self.folder = folder }
    var tools: [MCPTool] { FrameLineMCPCatalog.tools }
    func call(_ name: String, arguments: [String: Any]) async -> MCPToolResult {
        guard name == "list_recent_captures" else { return .error("not available in tests") }
        let limit = MCPJSON.int(arguments["limit"]) ?? 20
        let items = CaptureFolder.list(directory: folder, limit: limit)
        return .object(["folder": folder.path, "count": items.count, "captures": items.map(\.json)])
    }
}

struct RawResponse {
    var status: Int
    var headers: [String: String]
    var body: Data
    var json: [String: Any] { (try? JSONSerialization.jsonObject(with: body) as? [String: Any]) ?? [:] }
}

/// Minimal blocking HTTP/1.1 client over a POSIX socket (lets us forge the Host header,
/// which URLSession refuses to override).
func rawRequest(port: UInt16, method: String = "POST", host: String, headers: [String: String] = [:],
                body: Any? = nil) -> RawResponse? {
    let fd = socket(AF_INET, SOCK_STREAM, 0)
    guard fd >= 0 else { return nil }
    defer { close(fd) }
    var tv = timeval(tv_sec: 10, tv_usec: 0)
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
    var addr = sockaddr_in()
    addr.sin_family = sa_family_t(AF_INET)
    addr.sin_port = port.bigEndian
    addr.sin_addr.s_addr = inet_addr("127.0.0.1")
    let ok = withUnsafePointer(to: &addr) {
        $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
    }
    guard ok == 0 else { return nil }
    let payload = body.map { try! JSONSerialization.data(withJSONObject: $0) } ?? Data()
    var head = "\(method) /mcp HTTP/1.1\r\nHost: \(host)\r\nContent-Type: application/json\r\n"
    head += "Accept: application/json, text/event-stream\r\nContent-Length: \(payload.count)\r\n"
    for (k, v) in headers { head += "\(k): \(v)\r\n" }
    head += "\r\n"
    let out = Data(head.utf8) + payload
    _ = out.withUnsafeBytes { send(fd, $0.baseAddress, $0.count, 0) }
    var received = Data()
    var buf = [UInt8](repeating: 0, count: 65536)
    while true {
        let n = recv(fd, &buf, buf.count, 0)
        if n <= 0 { break }
        received.append(contentsOf: buf[0..<n])
    }
    guard let sep = received.range(of: Data("\r\n\r\n".utf8)),
          let headText = String(data: received[..<sep.lowerBound], encoding: .utf8) else { return nil }
    var lines = headText.components(separatedBy: "\r\n")
    let status = Int(lines.removeFirst().split(separator: " ")[1]) ?? 0
    var hs: [String: String] = [:]
    for l in lines { if let c = l.firstIndex(of: ":") { hs[String(l[..<c]).lowercased()] = l[l.index(after: c)...].trimmingCharacters(in: .whitespaces) } }
    return RawResponse(status: status, headers: hs, body: Data(received[sep.upperBound...]))
}

@main
enum MCPIntegrationTests {
    static var count = 0
    static func check(_ name: String, _ ok: Bool, line: UInt = #line) {
        if !ok { print("FAIL: \(name) (line \(line))"); exit(1) }
        count += 1
    }

    static func main() async {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("frameline-mcp-it-\(UUID().uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        try? Data("png".utf8).write(to: folder.appendingPathComponent("FrameLine 1.png"))
        try? Data("mov".utf8).write(to: folder.appendingPathComponent("FrameLine 2.mov"))

        let provider = FolderProvider(folder: folder)
        let dispatcher = MCPDispatcher(serverName: "frameline", serverTitle: "FrameLine", serverVersion: "test",
                                       instructions: FrameLineMCPCatalog.instructions, provider: provider)
        let token = MCPAuth.newToken()
        let server = MCPServer(handler: MCPHTTPHandler(dispatcher: dispatcher, token: token))
        let port: UInt16
        do { port = try await server.start() } catch { print("FAIL: server start \(error)"); exit(1) }
        check("bound a port", port > 0)
        let host = "127.0.0.1:\(port)"

        // `MCP_SERVE_HOME=<dir>`: keep serving 20 s with a discovery file under that fake home,
        // so scripts/mcp-smoke.sh can be checked against it (HOME=<dir> ./scripts/mcp-smoke.sh).
        if let home = ProcessInfo.processInfo.environment["MCP_SERVE_HOME"] {
            let identity = MCPAppIdentity(name: "FrameLine", bundleId: "test", version: "test", pid: getpid(),
                                          employeeName: "frameline", displayName: "FrameLine",
                                          description: "test", color: "#FF453A")
            let file = MCPDiscovery.defaultFile(home: URL(fileURLWithPath: home))
            try? MCPDiscovery.write(MCPDiscovery.document(port: port, token: token, app: identity), to: file)
            try? await Task.sleep(nanoseconds: 20_000_000_000)
            MCPDiscovery.removeIfOwned(file: file, pid: getpid())
            server.stop()
            return
        }
        let auth = ["Authorization": "Bearer \(token)"]

        // Everything below blocks on sockets: run it off the cooperative pool.
        await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
            Thread.detachNewThread {
                let initBody: [String: Any] = ["jsonrpc": "2.0", "id": 1, "method": "initialize",
                                               "params": ["protocolVersion": "2025-06-18", "capabilities": [:],
                                                          "clientInfo": ["name": "it", "version": "1"]]]
                let noAuth = rawRequest(port: port, host: host, body: initBody)
                check("401 without token", noAuth?.status == 401)
                let wrong = rawRequest(port: port, host: host, headers: ["Authorization": "Bearer \(String(repeating: "0", count: 64))"], body: initBody)
                check("401 with wrong token", wrong?.status == 401)
                let foreign = rawRequest(port: port, host: "evil.example:\(port)", headers: auth, body: initBody)
                check("403 with foreign Host", foreign?.status == 403)

                guard let ini = rawRequest(port: port, host: host, headers: auth, body: initBody) else {
                    check("initialize reachable", false); return
                }
                check("initialize 200", ini.status == 200)
                let sid = ini.headers["mcp-session-id"] ?? ""
                check("session id header", !sid.isEmpty)
                let result = ini.json["result"] as? [String: Any]
                check("protocol version", result?["protocolVersion"] as? String == "2025-06-18")
                check("server name", (result?["serverInfo"] as? [String: Any])?["name"] as? String == "frameline")

                var h = auth
                h["Mcp-Session-Id"] = sid
                h["MCP-Protocol-Version"] = "2025-06-18"
                let note = rawRequest(port: port, host: host, headers: h,
                                      body: ["jsonrpc": "2.0", "method": "notifications/initialized"])
                check("initialized notification 202", note?.status == 202)

                let list = rawRequest(port: port, host: "localhost:\(port)", headers: h,
                                      body: ["jsonrpc": "2.0", "id": 2, "method": "tools/list"])
                let tools = ((list?.json["result"] as? [String: Any])?["tools"] as? [[String: Any]]) ?? []
                check("tools/list 200", list?.status == 200)
                check("tools/list count", tools.count == FrameLineMCPCatalog.tools.count)
                check("list_recent_captures listed", tools.contains { $0["name"] as? String == "list_recent_captures" })

                let call = rawRequest(port: port, host: host, headers: h,
                                      body: ["jsonrpc": "2.0", "id": 3, "method": "tools/call",
                                             "params": ["name": "list_recent_captures", "arguments": ["limit": 10]]])
                let cres = call?.json["result"] as? [String: Any]
                let structured = cres?["structuredContent"] as? [String: Any]
                check("tools/call 200", call?.status == 200)
                check("not an error", MCPJSON.bool(cres?["isError"]) == false)
                check("two captures", MCPJSON.int(structured?["count"]) == 2)
                let paths = ((structured?["captures"] as? [[String: Any]]) ?? []).compactMap { $0["path"] as? String }
                check("paths are absolute in folder", paths.allSatisfy { $0.hasPrefix("/") && $0.contains("frameline-mcp-it-") })

                let noSession = rawRequest(port: port, host: host, headers: auth,
                                           body: ["jsonrpc": "2.0", "id": 4, "method": "tools/list"])
                check("400 without session", noSession?.status == 400)
                let get = rawRequest(port: port, method: "GET", host: host, headers: h)
                check("405 on GET", get?.status == 405)
                cont.resume()
            }
        }

        // Bound to loopback only: lsof must show the listening socket on 127.0.0.1, never *.
        let lsof = Process()
        lsof.executableURL = URL(fileURLWithPath: "/usr/sbin/lsof")
        lsof.arguments = ["-nP", "-a", "-p", String(getpid()), "-iTCP:\(port)", "-sTCP:LISTEN"]
        let pipe = Pipe()
        lsof.standardOutput = pipe
        try? lsof.run()
        lsof.waitUntilExit()
        let listing = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        check("listens on 127.0.0.1 only", listing.contains("127.0.0.1:\(port)") && !listing.contains("*:\(port)"))
        server.stop()
        print("MCPIntegrationTests: \(count) checks passed (port \(port))")
    }
}
