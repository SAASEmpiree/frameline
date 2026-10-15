import Foundation

// MARK: - Discovery file (the owner's universal local-MCP contract)
//
// ~/Library/Application Support/FrameLine/mcp.json, 0600 in a 0700 directory, written
// atomically, removed on quit. Claude-Code-compatible "mcpServers" block plus an "app" block
// that Coucou reads to present FrameLine as one of the CEO's employees.

struct MCPAppIdentity {
    var name: String
    var bundleId: String
    var version: String
    var pid: Int32
    var employeeName: String
    var displayName: String
    var description: String
    var color: String
}

enum MCPDiscovery {
    static let serverKey = "frameline"

    static func defaultDirectory(home: URL = FileManager.default.homeDirectoryForCurrentUser) -> URL {
        home.appendingPathComponent("Library/Application Support/FrameLine", isDirectory: true)
    }

    static func defaultFile(home: URL = FileManager.default.homeDirectoryForCurrentUser) -> URL {
        defaultDirectory(home: home).appendingPathComponent("mcp.json")
    }

    static func url(port: UInt16) -> String { "http://127.0.0.1:\(port)/mcp" }

    /// The block any MCP client understands (also what « Copier la config MCP » copies).
    static func clientConfig(port: UInt16, token: String) -> [String: Any] {
        ["mcpServers": [serverKey: [
            "type": "http",
            "url": url(port: port),
            "headers": ["Authorization": "Bearer \(token)"],
        ]]]
    }

    static func document(port: UInt16, token: String, app: MCPAppIdentity) -> [String: Any] {
        var doc = clientConfig(port: port, token: token)
        doc["app"] = [
            "name": app.name,
            "bundleId": app.bundleId,
            "version": app.version,
            "pid": Int(app.pid),
            "employee": [
                "name": app.employeeName,
                "displayName": app.displayName,
                "description": app.description,
                "color": app.color,
            ],
        ]
        return doc
    }

    enum WriteError: Error, LocalizedError {
        case posix(String, Int32)
        var errorDescription: String? {
            if case .posix(let what, let code) = self { return "\(what): \(String(cString: strerror(code)))" }
            return nil
        }
    }

    /// Atomic, private write: directory forced to 0700, temp file created 0600 with O_EXCL in the
    /// same directory, fsync, then rename(2) over the target. Never a world-readable instant.
    static func write(_ document: [String: Any], to file: URL) throws {
        let dir = file.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        guard chmod(dir.path, 0o700) == 0 else { throw WriteError.posix("chmod directory", errno) }

        let data = MCPJSON.data(document)
        let tmp = dir.appendingPathComponent(".mcp.json.\(getpid()).\(UInt32.random(in: 0...UInt32.max)).tmp")
        let fd = open(tmp.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, 0o600)
        guard fd >= 0 else { throw WriteError.posix("create temp file", errno) }
        var ok = false
        defer { if !ok { unlink(tmp.path) } }
        // umask can only remove bits; fchmod makes the mode explicit anyway.
        guard fchmod(fd, 0o600) == 0 else { let e = errno; close(fd); throw WriteError.posix("fchmod", e) }
        let written = data.withUnsafeBytes { raw -> Int in
            guard let base = raw.baseAddress else { return 0 }
            var off = 0
            while off < raw.count {
                let n = Darwin.write(fd, base + off, raw.count - off)
                if n <= 0 { return -1 }
                off += n
            }
            return off
        }
        guard written == data.count else { let e = errno; close(fd); throw WriteError.posix("write", e) }
        guard fsync(fd) == 0 else { let e = errno; close(fd); throw WriteError.posix("fsync", e) }
        close(fd)
        guard rename(tmp.path, file.path) == 0 else { throw WriteError.posix("rename", errno) }
        ok = true
    }

    /// Removes the file only if it is still ours (same pid): a newer instance's file survives.
    @discardableResult
    static func removeIfOwned(file: URL, pid: Int32) -> Bool {
        guard let data = try? Data(contentsOf: file),
              let doc = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let app = doc["app"] as? [String: Any],
              MCPJSON.int(app["pid"]) == Int(pid) else { return false }
        return unlink(file.path) == 0
    }
}
