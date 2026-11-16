namespace FrameLine.Core.Mcp;

/// FrameLine's local MCP server: Streamable HTTP on http://127.0.0.1:{free port}/mcp, Bearer token
/// (256 bits, new at every launch, kept across stop/start within a launch like macOS), discovery
/// file written once the port is known and removed on stop. The token lives in memory and in the
/// private discovery file only; it is never logged.
public sealed class McpServer : IDisposable
{
    public const string ServerName = "frameline";
    public const string ServerTitle = "FrameLine";
    /// FrameLine's accent (logo red).
    public const string AccentHex = "#FF453A";
    public const string BundleId = "com.saasempiree.frameline";

    private readonly object _lock = new();
    private readonly string _discoveryFile;
    private readonly int _requestedPort;
    private string _token = McpAuth.NewToken();
    private McpHttpListener? _listener;
    private McpHttpHandler? _handler;

    /// Raised (on a pool thread) when a running listener dies on its own; the server is stopped.
    public event Action<string>? Failed;

    /// discoveryFile: null = %APPDATA%\FrameLine\mcp.json. port: 0 = any free port.
    public McpServer(string? discoveryFile = null, int port = 0)
    {
        _discoveryFile = discoveryFile ?? McpDiscovery.DefaultFile();
        _requestedPort = port;
    }

    public bool IsRunning { get { lock (_lock) return _listener is not null; } }
    public int Port { get { lock (_lock) return _listener?.Port ?? 0; } }
    public string DiscoveryFile => _discoveryFile;
    internal string Token { get { lock (_lock) return _token; } }

    public static McpAppIdentity Identity(string version) => new(
        Name: "FrameLine",
        BundleId: BundleId,
        Version: version,
        Pid: Environment.ProcessId,
        EmployeeName: "frameline",
        DisplayName: "FrameLine",
        Description: "Captures d'écran, enregistrements de l'écran, annotations et OCR, en local.",
        Color: AccentHex);

    /// Binds the listener and writes the discovery file. Throws IOException (or
    /// UnauthorizedAccessException) when either fails; nothing is left running then.
    public void Start(IFrameLineMcpHost host) => Start(new FrameLineMcpTools(host), host.AppVersion);

    /// Same with any tool provider (tests).
    public void Start(IMcpToolProvider provider, string version)
    {
        lock (_lock)
        {
            if (_listener is not null) return;
            var dispatcher = new McpDispatcher(ServerName, ServerTitle, version, FrameLineMcpCatalog.Instructions, provider);
            var handler = new McpHttpHandler(dispatcher, _token);
            var listener = new McpHttpListener(handler);
            listener.Stopped += reason => OnListenerStopped(listener, reason);
            int port = listener.Start(_requestedPort);
            try
            {
                McpDiscovery.Write(McpDiscovery.Document(port, _token, Identity(version)), _discoveryFile);
            }
            catch
            {
                listener.Stop();
                throw;
            }
            _listener = listener;
            _handler = handler;
        }
    }

    private void OnListenerStopped(McpHttpListener listener, string reason)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_listener, listener)) return;
            TearDown();
        }
        Failed?.Invoke(reason);
    }

    public void Stop()
    {
        lock (_lock) TearDown();
    }

    private void TearDown()
    {
        _listener?.Stop();
        _listener = null;
        _handler = null;
        McpDiscovery.RemoveIfOwned(_discoveryFile, Environment.ProcessId);
    }

    /// New 256-bit token: old sessions die, the discovery file is rewritten (clients rediscover).
    /// Throws when the discovery file cannot be rewritten (the server keeps running).
    public void RegenerateToken()
    {
        lock (_lock)
        {
            _token = McpAuth.NewToken();
            _handler?.ReplaceToken(_token);
            if (_listener is not null)
            {
                var version = _handler?.Dispatcher.ServerVersion ?? "0";
                McpDiscovery.Write(McpDiscovery.Document(_listener.Port, _token, Identity(version)), _discoveryFile);
            }
        }
    }

    /// The "mcpServers" block (with the token) for Claude Code, Cursor, etc.; null when stopped.
    public string? ClientConfigJson()
    {
        lock (_lock)
        {
            if (_listener is null) return null;
            return McpJson.Pretty(McpDiscovery.ClientConfig(_listener.Port, _token));
        }
    }

    public void Dispose() => Stop();
}
