import Foundation
import Network

// MARK: - Loopback HTTP listener (Network.framework)
//
// Binds 127.0.0.1 only, on a port picked by the system (or a requested one). One request per
// connection (`Connection: close`), 30 s budget per connection, non-loopback peers dropped.

final class MCPServer: @unchecked Sendable {
    enum ServerError: Error, LocalizedError {
        case failed(String)
        case cancelled
        var errorDescription: String? {
            switch self {
            case .failed(let m): return m
            case .cancelled: return "Server stopped before it was ready"
            }
        }
    }

    static let connectionTimeout: TimeInterval = 30

    private let handler: MCPHTTPHandler
    private let queue = DispatchQueue(label: "FrameLine.mcp.server")
    private var listener: NWListener?
    private(set) var port: UInt16?
    /// Called (on the server queue) when a listener that was ready fails or stops later on.
    var onStopped: ((String) -> Void)?

    init(handler: MCPHTTPHandler) { self.handler = handler }

    /// Starts listening and returns the bound port. `port == 0` lets the system choose a free one.
    func start(port requested: UInt16 = 0) async throws -> UInt16 {
        let params = NWParameters.tcp
        params.acceptLocalOnly = true
        params.allowLocalEndpointReuse = true
        let nwPort = NWEndpoint.Port(rawValue: requested) ?? .any
        params.requiredLocalEndpoint = .hostPort(host: .ipv4(.loopback), port: nwPort)
        let listener: NWListener
        do { listener = try NWListener(using: params) }
        catch { throw ServerError.failed("Cannot create listener: \(error.localizedDescription)") }
        self.listener = listener

        listener.newConnectionHandler = { [weak self] connection in self?.accept(connection) }

        return try await withCheckedThrowingContinuation { (cont: CheckedContinuation<UInt16, Error>) in
            let once = ResumeOnce()                                  // only touched on `queue` (serial)
            listener.stateUpdateHandler = { [weak self] state in
                guard !once.done else {
                    if case .failed(let error) = state { self?.onStopped?(error.localizedDescription) }
                    return
                }
                switch state {
                case .ready:
                    once.done = true
                    let p = listener.port?.rawValue ?? 0
                    self?.port = p
                    cont.resume(returning: p)
                case .failed(let error):
                    once.done = true
                    listener.cancel()
                    cont.resume(throwing: ServerError.failed("Listener failed: \(error.localizedDescription)"))
                case .cancelled:
                    once.done = true
                    cont.resume(throwing: ServerError.cancelled)
                default:
                    break
                }
            }
            listener.start(queue: queue)
        }
    }

    private final class ResumeOnce: @unchecked Sendable { var done = false }

    func stop() {
        queue.sync {
            listener?.cancel()
            listener = nil
            port = nil
        }
    }

    // MARK: - Connections

    private static func isLoopback(_ endpoint: NWEndpoint) -> Bool {
        guard case .hostPort(let host, _) = endpoint else { return false }
        switch host {
        case .ipv4(let a): return a.isLoopback
        case .ipv6(let a): return a.isLoopback || a == IPv6Address("::ffff:127.0.0.1")
        case .name(let n, _): return n == "localhost" || n == "127.0.0.1"
        @unknown default: return false
        }
    }

    private func accept(_ connection: NWConnection) {
        guard Self.isLoopback(connection.endpoint) else { connection.cancel(); return }
        connection.start(queue: queue)
        queue.asyncAfter(deadline: .now() + Self.connectionTimeout) { [weak connection] in
            connection?.cancel()
        }
        receive(on: connection, buffer: Data())
    }

    private func receive(on connection: NWConnection, buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self] data, _, isComplete, error in
            guard let self else { connection.cancel(); return }
            var buffer = buffer
            if let data { buffer.append(data) }
            switch HTTPRequestParser.parse(buffer) {
            case .incomplete:
                if isComplete || error != nil { connection.cancel(); return }
                self.receive(on: connection, buffer: buffer)
            case .invalid(let status, let message):
                self.send(.error(status, message), on: connection)
            case .complete(let request):
                let handler = self.handler
                Task {
                    let response = await handler.handle(request)
                    self.send(response, on: connection)
                }
            }
        }
    }

    private func send(_ response: HTTPResponse, on connection: NWConnection) {
        connection.send(content: response.serialized(), contentContext: .finalMessage, isComplete: true,
                        completion: .contentProcessed { _ in connection.cancel() })
    }
}
