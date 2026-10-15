import AppKit
import Combine

/// Lifecycle of FrameLine's local MCP server: start at launch when enabled, discovery file
/// written once the port is known, removed on stop / quit, token regenerated on demand.
/// The token lives in memory and in the 0600 discovery file only; it is never logged.
@MainActor
final class MCPController: ObservableObject {
    static let shared = MCPController()

    enum Status: Equatable {
        case stopped
        case starting
        case running(port: UInt16)
        case failed(String)
    }

    @Published private(set) var status: Status = .stopped

    /// FrameLine's accent (logo red, `frameline-logo/svg/frameline.svg`).
    static let accentHex = "#FF453A"

    private var token = MCPAuth.newToken()
    private var server: MCPServer?
    private var handler: MCPHTTPHandler?
    private var generation = 0                                   // ignores a start that finishes after a stop
    private let discoveryFile = MCPDiscovery.defaultFile()

    private var identity: MCPAppIdentity {
        let info = Bundle.main.infoDictionary
        return MCPAppIdentity(
            name: "FrameLine",
            bundleId: Bundle.main.bundleIdentifier ?? "com.saasempiree.frameline",
            version: info?["CFBundleShortVersionString"] as? String ?? "0",
            pid: ProcessInfo.processInfo.processIdentifier,
            employeeName: "frameline",
            displayName: "FrameLine",
            description: "Captures d'écran, enregistrements de l'écran, annotations et OCR, en local.",
            color: Self.accentHex)
    }

    /// Called at launch: honours the "Serveur MCP (local)" toggle (default on).
    func startIfEnabled() {
        if Preferences.shared.mcpServerEnabled { start() }
    }

    func setEnabled(_ enabled: Bool) {
        Preferences.shared.mcpServerEnabled = enabled
        enabled ? start() : stop()
    }

    func start() {
        guard server == nil else { return }
        generation += 1
        let gen = generation
        status = .starting
        let dispatcher = MCPDispatcher(serverName: "frameline", serverTitle: "FrameLine",
                                       serverVersion: identity.version,
                                       instructions: FrameLineMCPCatalog.instructions,
                                       provider: FrameLineMCPTools())
        let handler = MCPHTTPHandler(dispatcher: dispatcher, token: token)
        let server = MCPServer(handler: handler)
        server.onStopped = { [weak self] reason in
            Task { @MainActor in
                guard let self, self.generation == gen else { return }
                self.tearDown()
                self.status = .failed(reason)
            }
        }
        self.server = server
        self.handler = handler
        Task {
            do {
                let port = try await server.start()
                guard self.generation == gen else { server.stop(); return }
                try self.writeDiscovery(port: port)
                self.status = .running(port: port)
            } catch {
                guard self.generation == gen else { return }
                self.tearDown()
                self.status = .failed(error.localizedDescription)
                NSLog("FrameLine — MCP server could not start: %@", error.localizedDescription)
            }
        }
    }

    func stop() {
        generation += 1
        tearDown()
        status = .stopped
    }

    /// Quit: stop listening and remove the discovery file (only if it is still ours).
    func shutdown() { stop() }

    private func tearDown() {
        server?.stop()
        server = nil
        handler = nil
        MCPDiscovery.removeIfOwned(file: discoveryFile, pid: ProcessInfo.processInfo.processIdentifier)
    }

    private func writeDiscovery(port: UInt16) throws {
        try MCPDiscovery.write(MCPDiscovery.document(port: port, token: token, app: identity), to: discoveryFile)
    }

    /// New 256-bit token: old sessions die, the discovery file is rewritten (clients rediscover).
    func regenerateToken() {
        token = MCPAuth.newToken()
        handler?.replaceToken(token)
        if case .running(let port) = status {
            do { try writeDiscovery(port: port) }
            catch { status = .failed(error.localizedDescription) }
        }
        GlassConfirmBadge.show("Jeton régénéré", symbol: "key.fill")
    }

    /// Copies the `mcpServers` block (with the token) for Claude Code, Cursor, etc.
    func copyClientConfig() {
        guard case .running(let port) = status else { return }
        let text = String(decoding: (try? JSONSerialization.data(
            withJSONObject: MCPDiscovery.clientConfig(port: port, token: token),
            options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])) ?? Data(), as: UTF8.self)
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(text, forType: .string)
        GlassConfirmBadge.show("Config MCP copiée")
    }
}
