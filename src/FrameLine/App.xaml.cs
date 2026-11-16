using System.Windows;

namespace FrameLine;

// OWNER: agent "shell" (single instance, tray-only startup, crash log).
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Shell.AppCoordinator.Shared.Start();
    }
}
