using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using FrameLine.Shell;

namespace FrameLine;

/// Port of main.swift + AppDelegate lifecycle: a tray-only app (no window at launch, no taskbar
/// button), one instance per user session, every unhandled exception written to a crash log.
public partial class App : Application
{
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        InstallCrashHandlers();
        base.OnStartup(e);

        var url = UrlScheme.FromArgs(e.Args);
        _instance = new SingleInstance();
        if (!_instance.TryAcquire())
        {
            _instance.Forward(url);
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }
        _instance.Listen(
            onReopen: () => Dispatcher.BeginInvoke(() => AppCoordinator.Shared.HandleReopen()),
            onUrl: u => Dispatcher.BeginInvoke(() => AppCoordinator.Shared.HandleUrl(u)));

        AppCoordinator.Shared.Start();
        // A cold launch through a frameline:// link: handled once the app is up.
        if (url != null) Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => AppCoordinator.Shared.HandleUrl(url));
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        AppCoordinator.Shared.HandleSessionEnding();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppCoordinator.Shared.StopServices();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void InstallCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            CrashLog.Write("AppDomain", args.ExceptionObject as Exception);
            if (args.IsTerminating)
            {
                // Last chance: no orphan tray icon, no stale mcp.json pointing at a dead port.
                try { Dispatcher.Invoke(() => AppCoordinator.Shared.StopServices(), TimeSpan.FromSeconds(2)); } catch { }
            }
        };
        // A UI exception is logged and swallowed: a tray utility that vanishes silently is worse
        // than one that skips a single action.
        DispatcherUnhandledException += (_, args) =>
        {
            CrashLog.Write("Dispatcher", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write("UnobservedTask", args.Exception);
            args.SetObserved();
        };
    }
}
