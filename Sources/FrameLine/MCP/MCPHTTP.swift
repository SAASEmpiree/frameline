import Foundation
import Security

// MARK: - Streamable HTTP transport (server side, JSON responses only)
//
// Foundation + Security only (testable without the app). POST /mcp carries one JSON-RPC
// message (or a batch, for 2025-03-26 clients) and gets an application/json reply; SSE is not
// offered (GET → 405), which the spec allows. Sessions use the Mcp-Session-Id header.

struct HTTPRequest {
    var method: String
    var path: String
    /// Header names lowercased.
    var headers: [String: String]
    var body: Data

    func header(_ name: String) -> String? { headers[name.lowercased()] }
}

struct HTTPResponse {
    var status: Int
    var headers: [String: String] = [:]
    var body: Data = Data()

    static func json(_ status: Int, _ object: Any, headers: [String: String] = [:]) -> HTTPResponse {
        var h = headers
        h["Content-Type"] = "application/json"
        return HTTPResponse(status: status, headers: h, body: MCPJSON.data(object))
    }

    /// Plain error with a JSON-RPC error body (what MCP clients know how to display).
    static func error(_ status: Int, _ message: String, headers: [String: String] = [:]) -> HTTPResponse {
        json(status, MCPDispatcher.errorResponse(id: nil, code: JSONRPCError.invalidRequest, message: message),
             headers: headers)
    }

    static func reason(_ status: Int) -> String {
        switch status {
        case 200: return "OK"
        case 202: return "Accepted"
        case 204: return "No Content"
        case 400: return "Bad Request"
        case 401: return "Unauthorized"
        case 403: return "Forbidden"
        case 404: return "Not Found"
        case 405: return "Method Not Allowed"
        case 408: return "Request Timeout"
        case 411: return "Length Required"
        case 413: return "Payload Too Large"
        case 415: return "Unsupported Media Type"
        case 431: return "Request Header Fields Too Large"
        case 500: return "Internal Server Error"
        default: return "Status"
        }
    }

    /// Wire format. Always `Connection: close`: one request per connection keeps the server tiny.
    func serialized() -> Data {
        var head = "HTTP/1.1 \(status) \(Self.reason(status))\r\n"
        var all = headers
        all["Content-Length"] = String(body.count)
        all["Connection"] = "close"
        all["Cache-Control"] = "no-store"
        for key in all.keys.sorted() { head += "\(key): \(all[key]!)\r\n" }
        head += "\r\n"
        return Data(head.utf8) + body
    }
}

/// Incremental HTTP/1.1 request parser (bytes received so far → request, need more, or reject).
enum HTTPRequestParser {
    static let maxHeaderBytes = 16 * 1024
    static let maxBodyBytes = 4 * 1024 * 1024

    enum Result {
        case incomplete
        case complete(HTTPRequest)
        case invalid(status: Int, message: String)
    }

    static func parse(_ buffer: Data) -> Result {
        let separator = Data("\r\n\r\n".utf8)
        guard let headEnd = buffer.range(of: separator) else {
            return buffer.count > maxHeaderBytes
                ? .invalid(status: 431, message: "Request headers too large") : .incomplete
        }
        guard headEnd.lowerBound <= maxHeaderBytes else {
            return .invalid(status: 431, message: "Request headers too large")
        }
        guard let head = String(data: buffer[buffer.startIndex..<headEnd.lowerBound], encoding: .utf8) else {
            return .invalid(status: 400, message: "Headers are not UTF-8")
        }
        var lines = head.components(separatedBy: "\r\n")
        let requestLine = lines.removeFirst().split(separator: " ", omittingEmptySubsequences: true)
        guard requestLine.count == 3, requestLine[2].hasPrefix("HTTP/1.") else {
            return .invalid(status: 400, message: "Malformed request line")
        }
        var headers: [String: String] = [:]
        for line in lines where !line.isEmpty {
            guard let colon = line.firstIndex(of: ":") else {
                return .invalid(status: 400, message: "Malformed header line")
            }
            let name = line[..<colon].trimmingCharacters(in: .whitespaces).lowercased()
            let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            guard !name.isEmpty else { return .invalid(status: 400, message: "Empty header name") }
            // Duplicate Host / Authorization / Content-Length are ambiguous: refuse rather than guess.
            if headers[name] != nil, ["host", "authorization", "content-length", "mcp-session-id"].contains(name) {
                return .invalid(status: 400, message: "Duplicate \(name) header")
            }
            headers[name] = headers[name].map { "\($0), \(value)" } ?? value
        }
        if headers["transfer-encoding"] != nil {
            return .invalid(status: 411, message: "Chunked bodies are not supported; send Content-Length")
        }
        var length = 0
        if let raw = headers["content-length"] {
            guard let n = Int(raw), n >= 0 else { return .invalid(status: 400, message: "Bad Content-Length") }
            length = n
        }
        guard length <= maxBodyBytes else { return .invalid(status: 413, message: "Request body too large") }
        let bodyStart = headEnd.upperBound
        guard buffer.count - (bodyStart - buffer.startIndex) >= length else { return .incomplete }
        let body = buffer[bodyStart..<(bodyStart + length)]
        var path = String(requestLine[1])
        if let q = path.firstIndex(of: "?") { path = String(path[..<q]) }
        return .complete(HTTPRequest(method: String(requestLine[0]).uppercased(), path: path,
                                     headers: headers, body: Data(body)))
    }
}

/// Bearer token generation and constant-time checking.
enum MCPAuth {
    /// 32 random bytes (256 bits) from the system CSPRNG, hex encoded (64 chars).
    static func newToken() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        let status = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        precondition(status == errSecSuccess, "SecRandomCopyBytes failed")
        return bytes.map { String(format: "%02x", $0) }.joined()
    }

    /// Compares every byte whatever the first mismatch, and the lengths without an early exit.
    static func constantTimeEquals(_ a: String, _ b: String) -> Bool {
        let x = Array(a.utf8), y = Array(b.utf8)
        var diff = UInt8(truncatingIfNeeded: x.count ^ y.count)
        diff |= x.count == y.count ? 0 : 1
        let n = max(x.count, y.count)
        for i in 0..<n {
            let p = i < x.count ? x[i] : 0
            let q = i < y.count ? y[i] : 0
            diff |= p ^ q
        }
        return diff == 0
    }

    /// `Authorization: Bearer <token>` (scheme case-insensitive) against the expected token.
    static func isAuthorized(header: String?, token: String) -> Bool {
        guard let header, !token.isEmpty else { return false }
        let parts = header.split(separator: " ", maxSplits: 1, omittingEmptySubsequences: true)
        guard parts.count == 2, parts[0].lowercased() == "bearer" else { return false }
        return constantTimeEquals(String(parts[1]).trimmingCharacters(in: .whitespaces), token)
    }

    /// DNS-rebinding guard: only loopback names, whatever the port.
    static func isAllowedHost(_ host: String?) -> Bool {
        guard var h = host?.trimmingCharacters(in: .whitespaces).lowercased(), !h.isEmpty else { return false }
        if let colon = h.lastIndex(of: ":") {
            let port = h[h.index(after: colon)...]
            guard !port.isEmpty, port.allSatisfy(\.isNumber) else { return false }
            h = String(h[..<colon])
        }
        return h == "127.0.0.1" || h == "localhost"
    }

    /// Browsers send Origin on cross-site requests: only a loopback origin is accepted.
    static func isAllowedOrigin(_ origin: String?) -> Bool {
        guard let origin else { return true }                       // non-browser clients send none
        guard let url = URL(string: origin), let scheme = url.scheme?.lowercased(),
              scheme == "http" || scheme == "https", let host = url.host?.lowercased() else { return false }
        return host == "127.0.0.1" || host == "localhost"
    }
}

/// HTTP → MCP: Host/Origin checks, Bearer auth, sessions, then the JSON-RPC dispatcher.
/// Thread-safe (called from the listener queue; state behind a lock).
final class MCPHTTPHandler: @unchecked Sendable {
    static let endpointPath = "/mcp"
    static let maxSessions = 64

    let dispatcher: MCPDispatcher
    private let lock = NSLock()
    private var token: String
    private var sessions: [String] = []                              // oldest first

    init(dispatcher: MCPDispatcher, token: String) {
        self.dispatcher = dispatcher
        self.token = token
    }

    /// New token: every existing session dies with the old one.
    func replaceToken(_ newToken: String) {
        lock.lock(); defer { lock.unlock() }
        token = newToken
        sessions.removeAll()
    }

    var sessionCount: Int { lock.lock(); defer { lock.unlock() }; return sessions.count }

    private func currentToken() -> String { lock.lock(); defer { lock.unlock() }; return token }

    private func hasSession(_ id: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return sessions.contains(id)
    }

    private func createSession() -> String {
        let id = MCPAuth.newToken()
        lock.lock(); defer { lock.unlock() }
        sessions.append(id)
        if sessions.count > Self.maxSessions { sessions.removeFirst(sessions.count - Self.maxSessions) }
        return id
    }

    private func endSession(_ id: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let i = sessions.firstIndex(of: id) else { return false }
        sessions.remove(at: i)
        return true
    }

    func handle(_ request: HTTPRequest) async -> HTTPResponse {
        // 1. Host + Origin first: a rebinding page gets nothing, not even a 401.
        guard MCPAuth.isAllowedHost(request.header("host")) else {
            return .error(403, "Forbidden host")
        }
        guard MCPAuth.isAllowedOrigin(request.header("origin")) else {
            return .error(403, "Forbidden origin")
        }
        // 2. Bearer token.
        guard MCPAuth.isAuthorized(header: request.header("authorization"), token: currentToken()) else {
            return .error(401, "Missing or invalid bearer token",
                          headers: ["WWW-Authenticate": "Bearer realm=\"frameline\""])
        }
        // 3. Route.
        guard request.path == Self.endpointPath else { return .error(404, "Not found") }
        if let version = request.header("mcp-protocol-version"),
           !MCPDispatcher.supportedProtocolVersions.contains(version) {
            return .error(400, "Unsupported MCP-Protocol-Version: \(version)")
        }
        switch request.method {
        case "POST": return await handlePost(request)
        case "DELETE":
            guard let sid = request.header("mcp-session-id") else { return .error(400, "Missing Mcp-Session-Id") }
            return endSession(sid) ? HTTPResponse(status: 204) : .error(404, "Unknown session")
        default:
            // No server-initiated SSE stream: GET is not offered (allowed by the spec).
            return HTTPResponse.error(405, "Method not allowed", headers: ["Allow": "POST, DELETE"])
        }
    }

    private func handlePost(_ request: HTTPRequest) async -> HTTPResponse {
        if let type = request.header("content-type"), !type.lowercased().contains("application/json") {
            return .error(415, "Content-Type must be application/json")
        }
        guard let parsed = try? JSONSerialization.jsonObject(with: request.body, options: [.fragmentsAllowed]) else {
            return .json(400, MCPDispatcher.errorResponse(id: nil, code: JSONRPCError.parseError, message: "Parse error"))
        }
        let messages: [Any]
        let isBatch: Bool
        if let array = parsed as? [Any] {
            guard !array.isEmpty else {
                return .json(400, MCPDispatcher.errorResponse(id: nil, code: JSONRPCError.invalidRequest,
                                                              message: "Empty batch"))
            }
            messages = array
            isBatch = true
        } else {
            messages = [parsed]
            isBatch = false
        }

        let initializes = messages.filter { ($0 as? [String: Any])?["method"] as? String == "initialize" }
        var responseHeaders: [String: String] = [:]
        if !initializes.isEmpty {
            // initialize must travel alone and opens a session.
            guard !isBatch else { return .error(400, "initialize must not be part of a batch") }
            responseHeaders["Mcp-Session-Id"] = createSession()
        } else {
            guard let sid = request.header("mcp-session-id") else {
                return .error(400, "Missing Mcp-Session-Id header (send initialize first)")
            }
            guard hasSession(sid) else { return .error(404, "Unknown or expired session; initialize again") }
        }

        var replies: [[String: Any]] = []
        for message in messages {
            if case .response(let reply) = await dispatcher.handle(message) { replies.append(reply) }
        }
        if replies.isEmpty { return HTTPResponse(status: 202, headers: responseHeaders) }
        return .json(200, isBatch ? replies as Any : replies[0] as Any, headers: responseHeaders)
    }
}
