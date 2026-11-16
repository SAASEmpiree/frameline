namespace FrameLine.Core.Mcp;

public enum McpStatusKind { Stopped, Starting, Running, Failed }

/// Port is set when Running, Reason when Failed (shown as "Error: %@" in Settings).
public readonly record struct McpStatus(McpStatusKind Kind, int Port = 0, string? Reason = null)
{
    public static McpStatus Stopped => new(McpStatusKind.Stopped);
    public bool IsRunning => Kind == McpStatusKind.Running;
}

/// Lifecycle of FrameLine's local MCP server: started at launch when Preferences.McpServerEnabled
/// is on, follows that setting while the app runs, stopped (discovery file removed) on quit.
/// Thread-safe; StatusChanged is raised on the calling thread, the UI marshals to its dispatcher.
public sealed class McpController : IDisposable
{
    public static McpController Shared { get; } = new(Preferences.Shared, new McpServer());

    private readonly object _lock = new();
    private readonly Preferences _preferences;
    private readonly McpServer _server;
    private IFrameLineMcpHost? _host;
    private bool _subscribed;

    public McpStatus Status { get; private set; } = McpStatus.Stopped;
    public event Action<McpStatus>? StatusChanged;

    internal McpController(Preferences preferences, McpServer server)
    {
        _preferences = preferences;
        _server = server;
        _server.Failed += reason => SetStatus(new McpStatus(McpStatusKind.Failed, Reason: reason));
    }

    public McpServer Server => _server;

    /// Called at launch: remembers the host, follows the "Local MCP server" toggle from now on,
    /// and starts the server if the toggle is on (default on).
    public void StartIfEnabled(IFrameLineMcpHost host)
    {
        lock (_lock)
        {
            _host = host;
            if (!_subscribed)
            {
                _preferences.Changed += OnPreferenceChanged;
                _subscribed = true;
            }
        }
        if (_preferences.McpServerEnabled) Start();
    }

    /// From the Settings toggle: persists the choice; the Changed event then starts or stops.
    /// Also applies it directly so a Preferences implementation without the event still works.
    public void SetEnabled(bool enabled)
    {
        if (_preferences.McpServerEnabled != enabled) _preferences.McpServerEnabled = enabled;
        if (enabled) Start(); else Stop();
    }

    internal void OnPreferenceChanged(string property)
    {
        if (property != nameof(Preferences.McpServerEnabled)) return;
        if (_preferences.McpServerEnabled) Start(); else Stop();
    }

    public void Start()
    {
        IFrameLineMcpHost? host;
        lock (_lock)
        {
            host = _host;
            if (host is null || _server.IsRunning) return;
            Status = new McpStatus(McpStatusKind.Starting);
        }
        StatusChanged?.Invoke(new McpStatus(McpStatusKind.Starting));
        McpStatus next;
        lock (_lock)
        {
            // Status is written under the same lock as the server state, so a Stop() racing this
            // Start() can never leave "Running" displayed over a stopped server.
            try
            {
                _server.Start(host);
                next = new McpStatus(McpStatusKind.Running, Port: _server.Port);
            }
            catch (Exception e)                                      // never take the app down at launch
            {
                _server.Stop();
                next = new McpStatus(McpStatusKind.Failed, Reason: e.Message);
            }
            if (Status == next) return;
            Status = next;
        }
        StatusChanged?.Invoke(next);
    }

    public void Stop()
    {
        lock (_lock)
        {
            _server.Stop();
            if (Status == McpStatus.Stopped) return;
            Status = McpStatus.Stopped;
        }
        StatusChanged?.Invoke(McpStatus.Stopped);
    }

    /// Quit: stop listening and remove the discovery file (only if it is still ours).
    public void Shutdown()
    {
        lock (_lock)
        {
            if (_subscribed)
            {
                _preferences.Changed -= OnPreferenceChanged;
                _subscribed = false;
            }
        }
        Stop();
    }

    /// New 256-bit token: old sessions die, the discovery file is rewritten. The shell shows the
    /// "Token regenerated" badge.
    public void RegenerateToken()
    {
        try { lock (_lock) _server.RegenerateToken(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus(new McpStatus(McpStatusKind.Failed, Reason: e.Message));
        }
    }

    /// Text for "Copy MCP config" (the shell puts it on the clipboard); null when not running.
    public string? ClientConfigJson() => Status.IsRunning ? _server.ClientConfigJson() : null;

    private void SetStatus(McpStatus status)
    {
        lock (_lock)
        {
            if (Status == status) return;
            Status = status;
        }
        StatusChanged?.Invoke(status);
    }

    public void Dispose() => Shutdown();
}
