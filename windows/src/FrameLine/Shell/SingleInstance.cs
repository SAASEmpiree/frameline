using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FrameLine.Shell;

/// One FrameLine per user session. A second launch (Start menu, exe double-click, frameline://
/// link) hands its request to the running instance and exits: "reopen" shows the library, like
/// applicationShouldHandleReopen; a URL is handled like the macOS GetURL Apple Event.
/// The pipe is CurrentUserOnly: another account on the machine cannot talk to it.
internal sealed class SingleInstance : IDisposable
{
    private const string ReopenMessage = "reopen";
    private const string UrlPrefix = "url ";

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private bool _owned;

    public SingleInstance()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        int session = Process.GetCurrentProcess().SessionId;
        _mutex = new Mutex(false, $@"Local\FrameLine.SingleInstance.{sid}");
        _pipeName = $"FrameLine.Instance.{session}.{sid}";
    }

    /// True when this process is the first instance.
    public bool TryAcquire()
    {
        try { _owned = _mutex.WaitOne(0, false); }
        catch (AbandonedMutexException) { _owned = true; }   // previous instance crashed: we own it now
        return _owned;
    }

    /// Second instance: forwards the request ("reopen" or the frameline:// URL) and returns.
    public void Forward(string? url)
    {
        try
        {
            AllowSetForegroundWindow(ASFW_ANY);   // let the first instance bring its window to front
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            var bytes = Encoding.UTF8.GetBytes((url == null ? ReopenMessage : UrlPrefix + url) + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            ESLog.Log("single instance forward failed: " + e.Message);
        }
    }

    /// First instance: listens for forwarded requests; callbacks run on a thread-pool thread.
    public void Listen(Action onReopen, Action<string> onUrl)
    {
        var token = _cts.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (line == null || line.Length > 4096) continue;
                    if (line == ReopenMessage) onReopen();
                    else if (line.StartsWith(UrlPrefix, StringComparison.Ordinal)) onUrl(line[UrlPrefix.Length..]);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    ESLog.Log("single instance listen: " + e.Message);
                    try { await Task.Delay(500, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                }
            }
        }, token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_owned)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _owned = false;
        }
        _mutex.Dispose();
    }

    private const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
