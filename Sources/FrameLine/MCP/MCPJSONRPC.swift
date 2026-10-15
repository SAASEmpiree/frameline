import Foundation

// MARK: - Minimal MCP (Model Context Protocol) JSON-RPC layer
//
// Self-contained: Foundation only, no AppKit, so it compiles into the standalone test
// executables of scripts/test-mcp.sh. Implements the server side of the 2025-06-18 spec
// that FrameLine needs: initialize, notifications/initialized, ping, tools/list, tools/call.
// https://modelcontextprotocol.io/specification/2025-06-18

/// Behaviour hints of a tool (MCP "ToolAnnotations"). Hints only: clients must not rely on them
/// for security, but they drive confirmation prompts in most clients.
struct MCPToolAnnotations {
    var title: String?
    var readOnlyHint: Bool
    var destructiveHint: Bool
    var idempotentHint: Bool
    var openWorldHint: Bool

    var json: [String: Any] {
        var out: [String: Any] = [
            "readOnlyHint": readOnlyHint,
            "openWorldHint": openWorldHint,
        ]
        // destructiveHint / idempotentHint are only meaningful when readOnlyHint == false.
        if !readOnlyHint {
            out["destructiveHint"] = destructiveHint
            out["idempotentHint"] = idempotentHint
        }
        if let title { out["title"] = title }
        return out
    }
}

/// One tool as advertised by tools/list.
struct MCPTool {
    let name: String
    let title: String
    let description: String
    let inputSchema: [String: Any]
    let annotations: MCPToolAnnotations

    var json: [String: Any] {
        [
            "name": name,
            "title": title,
            "description": description,
            "inputSchema": inputSchema,
            "annotations": annotations.json,
        ]
    }
}

/// Result of a tools/call. Tool failures are reported IN the result (`isError: true`) so the
/// model can read them; protocol failures (unknown tool, bad params) are JSON-RPC errors.
struct MCPToolResult {
    var content: [[String: Any]]
    var structuredContent: [String: Any]?
    var isError: Bool

    /// Structured success: the object is returned both as structuredContent and as JSON text
    /// (text keeps older clients working).
    static func object(_ object: [String: Any], extra: [[String: Any]] = []) -> MCPToolResult {
        MCPToolResult(content: [["type": "text", "text": MCPJSON.string(object)]] + extra,
                      structuredContent: object, isError: false)
    }

    static func text(_ text: String) -> MCPToolResult {
        MCPToolResult(content: [["type": "text", "text": text]], structuredContent: nil, isError: false)
    }

    static func error(_ message: String) -> MCPToolResult {
        MCPToolResult(content: [["type": "text", "text": message]], structuredContent: nil, isError: true)
    }

    var json: [String: Any] {
        var out: [String: Any] = ["content": content, "isError": isError]
        if let structuredContent { out["structuredContent"] = structuredContent }
        return out
    }
}

/// What the server exposes. Implemented by the app (FrameLineMCPTools) and by fakes in tests.
protocol MCPToolProvider: AnyObject {
    var tools: [MCPTool] { get }
    func call(_ name: String, arguments: [String: Any]) async -> MCPToolResult
}

/// JSON helpers (JSONSerialization, deterministic output).
enum MCPJSON {
    static func data(_ object: Any) -> Data {
        (try? JSONSerialization.data(withJSONObject: object,
                                     options: [.sortedKeys, .withoutEscapingSlashes])) ?? Data("{}".utf8)
    }

    static func string(_ object: Any) -> String { String(decoding: data(object), as: UTF8.self) }

    /// Reads a JSON number as Int, refusing booleans (which JSONSerialization also bridges to NSNumber).
    static func int(_ value: Any?) -> Int? {
        guard let n = value as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID() else { return nil }
        let d = n.doubleValue
        guard d.isFinite, d == d.rounded(), abs(d) < 1e12 else { return nil }
        return Int(d)
    }

    static func double(_ value: Any?) -> Double? {
        guard let n = value as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID() else { return nil }
        return n.doubleValue.isFinite ? n.doubleValue : nil
    }

    static func bool(_ value: Any?) -> Bool? {
        guard let n = value as? NSNumber, CFGetTypeID(n) == CFBooleanGetTypeID() else { return nil }
        return n.boolValue
    }
}

/// JSON-RPC error codes used here.
enum JSONRPCError {
    static let parseError = -32700
    static let invalidRequest = -32600
    static let methodNotFound = -32601
    static let invalidParams = -32602
    static let internalError = -32603
}

/// Turns one decoded JSON-RPC message into a reply. Transport-agnostic.
final class MCPDispatcher {
    /// Newest first. The first one is answered when the client asks for a version we do not know.
    static let supportedProtocolVersions = ["2025-06-18", "2025-03-26", "2024-11-05"]

    let serverName: String
    let serverTitle: String
    let serverVersion: String
    let instructions: String
    let provider: MCPToolProvider

    init(serverName: String, serverTitle: String, serverVersion: String, instructions: String,
         provider: MCPToolProvider) {
        self.serverName = serverName
        self.serverTitle = serverTitle
        self.serverVersion = serverVersion
        self.instructions = instructions
        self.provider = provider
    }

    enum Outcome {
        /// A JSON-RPC response object to send back.
        case response([String: Any])
        /// Notification or client response: nothing to answer (HTTP 202).
        case accepted
    }

    static func errorResponse(id: Any?, code: Int, message: String) -> [String: Any] {
        ["jsonrpc": "2.0", "id": id ?? NSNull(), "error": ["code": code, "message": message]]
    }

    static func resultResponse(id: Any, result: [String: Any]) -> [String: Any] {
        ["jsonrpc": "2.0", "id": id, "result": result]
    }

    /// A valid JSON-RPC id is a string or a number (not a boolean, not null for requests).
    static func isValidID(_ id: Any?) -> Bool {
        if id is String { return true }
        if let n = id as? NSNumber { return CFGetTypeID(n) != CFBooleanGetTypeID() }
        return false
    }

    func handle(_ message: Any) async -> Outcome {
        guard let msg = message as? [String: Any], msg["jsonrpc"] as? String == "2.0" else {
            return .response(Self.errorResponse(id: nil, code: JSONRPCError.invalidRequest,
                                                message: "Invalid JSON-RPC 2.0 message"))
        }
        guard let method = msg["method"] as? String else {
            // A response from the client (result/error): we never send requests, so accept and ignore.
            if msg["result"] != nil || msg["error"] != nil { return .accepted }
            return .response(Self.errorResponse(id: msg["id"], code: JSONRPCError.invalidRequest,
                                                message: "Missing method"))
        }
        // Notification (no id): never answered.
        guard msg.keys.contains("id") else { return .accepted }
        let id = msg["id"]
        guard Self.isValidID(id), let id else {
            return .response(Self.errorResponse(id: nil, code: JSONRPCError.invalidRequest,
                                                message: "Request id must be a string or a number"))
        }
        let params = msg["params"] as? [String: Any] ?? [:]

        switch method {
        case "initialize":
            return .response(Self.resultResponse(id: id, result: initializeResult(params)))
        case "ping":
            return .response(Self.resultResponse(id: id, result: [:]))
        case "tools/list":
            return .response(Self.resultResponse(id: id, result: ["tools": provider.tools.map(\.json)]))
        case "tools/call":
            guard let name = params["name"] as? String else {
                return .response(Self.errorResponse(id: id, code: JSONRPCError.invalidParams,
                                                    message: "tools/call needs a tool name"))
            }
            guard provider.tools.contains(where: { $0.name == name }) else {
                return .response(Self.errorResponse(id: id, code: JSONRPCError.invalidParams,
                                                    message: "Unknown tool: \(name)"))
            }
            if let raw = params["arguments"], !(raw is [String: Any]), !(raw is NSNull) {
                return .response(Self.errorResponse(id: id, code: JSONRPCError.invalidParams,
                                                    message: "tools/call arguments must be an object"))
            }
            let args = params["arguments"] as? [String: Any] ?? [:]
            let result = await provider.call(name, arguments: args)
            return .response(Self.resultResponse(id: id, result: result.json))
        default:
            return .response(Self.errorResponse(id: id, code: JSONRPCError.methodNotFound,
                                                message: "Method not found: \(method)"))
        }
    }

    /// Version negotiation: echo the client's version when we support it, else offer our newest.
    static func negotiatedVersion(requested: String?) -> String {
        if let requested, supportedProtocolVersions.contains(requested) { return requested }
        return supportedProtocolVersions[0]
    }

    private func initializeResult(_ params: [String: Any]) -> [String: Any] {
        [
            "protocolVersion": Self.negotiatedVersion(requested: params["protocolVersion"] as? String),
            "capabilities": ["tools": ["listChanged": false]],
            "serverInfo": ["name": serverName, "title": serverTitle, "version": serverVersion],
            "instructions": instructions,
        ]
    }
}
