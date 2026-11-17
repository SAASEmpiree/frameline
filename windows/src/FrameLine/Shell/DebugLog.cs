using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;

namespace FrameLine.Shell;

/// Port of DebugLog.swift (ESLog): DEBUG builds only, a timestamped trace of activation and window
/// events in %LOCALAPPDATA%\FrameLine\logs\FrameLine-debug.log. Release builds compile every call
/// to a no-op: no usage trace, no capture path is ever written to disk.
public static class ESLog
{
#if DEBUG
    private static readonly string LogPath = Path.Combine(AppPaths.LogsDirectory, "FrameLine-debug.log");
    private static readonly BlockingCollection<string> Queue = new();
    private static int _started;

    public static void Log(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}";
        Debug.WriteLine("ESLOG " + msg);
        StartWriter();
        Queue.TryAdd(line);
    }

    /// Activation state at `tag`: foreground window owner and our visible windows.
    public static void Snapshot(string tag)
    {
        var app = Application.Current;
        if (app == null) { Log(tag); return; }
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(() => Snapshot(tag)); return; }
        var visible = app.Windows.Cast<Window>().Where(w => w.IsVisible).ToList();
        bool active = app.Windows.Cast<Window>().Any(w => w.IsActive);
        var vis = string.Join(" ", visible.Select(w => $"{w.GetType().Name}(active={w.IsActive},topmost={w.Topmost})"));
        Log($"{tag} | selfActive={active} vis=[{vis}]");
    }

    public static void Stack(string tag, int depth = 10)
    {
        var frames = new StackTrace(1, false).GetFrames().Take(depth)
            .Select(f => f.GetMethod() is { } m ? $"{m.DeclaringType?.Name}.{m.Name}" : "?");
        Log($"{tag} — call stack:\n    {string.Join("\n    ", frames)}");
    }

    public static void InstallObservers()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            File.WriteAllText(LogPath, "=== FrameLine debug log ===" + Environment.NewLine);
        }
        catch { }
        Log("observers installed → log file: " + LogPath);
    }

    private static void StartWriter()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        var t = new Thread(() =>
        {
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.LogsDirectory);
                    File.AppendAllText(LogPath, line, Encoding.UTF8);
                }
                catch { }
            }
        }) { IsBackground = true, Name = "FrameLine ESLog" };
        t.Start();
    }
#else
    public static void Log(string msg) { }
    public static void Snapshot(string tag) { }
    public static void Stack(string tag, int depth = 10) { }
    public static void InstallObservers() { }
#endif
}

/// Unhandled exceptions are always written (Release too): one file per crash, local only.
public static class CrashLog
{
    public static void Write(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            var file = Path.Combine(AppPaths.LogsDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            var text = new StringBuilder()
                .AppendLine($"FrameLine {AppPaths.Version} crash ({source}) at {DateTime.Now:O}")
                .AppendLine($"OS: {Environment.OSVersion} / .NET {Environment.Version}")
                .AppendLine()
                .AppendLine(ex?.ToString() ?? "(no exception object)")
                .ToString();
            File.WriteAllText(file, text, Encoding.UTF8);
        }
        catch { }
    }
}
