using System;
using System.Windows;
using System.Windows.Interop;
using FrameLine.Platform;

namespace FrameLine.Shell;

/// A tray app has no window of its own to parent a dialog to, and a parentless MessageBox or file
/// dialog can open BEHIND the active app. This invisible owner takes the foreground on the monitor
/// under the cursor first (NSApp.activate before runModal on macOS).
internal sealed class ForegroundOwner : IDisposable
{
    public Window Window { get; }

    public ForegroundOwner()
    {
        Window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false, ShowActivated = true, Topmost = true, ResizeMode = ResizeMode.NoResize,
            Width = 1, Height = 1, Opacity = 0,
        };
        Window.CenterOnActiveScreen();
        Window.Show();
        Native.SetForegroundWindow(new WindowInteropHelper(Window).Handle);
        Window.Activate();
    }

    public void Dispose() => Window.Close();
}

internal static class Dialogs
{
    /// "Check for Updates…": FrameLine has no update channel yet, so it honestly confirms the
    /// installed version is current (localised placeholder, like the macOS app).
    public static void ShowUpToDate()
    {
        using var owner = new ForegroundOwner();
        MessageBox.Show(owner.Window,
            Tr.F("uptodate_msg", AppPaths.Version),
            Tr.L("uptodate_title"),
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// [SEC] Confirmation for a sensitive action asked through frameline://.
    public static bool ConfirmUrlTrigger(string action)
    {
        using var owner = new ForegroundOwner();
        var title = Tr.Format(Tr.L("url_confirm_title", "Autoriser %@ ?"), action);
        var text = Tr.Format(Tr.L("url_confirm_text",
            "Une application ou un site web vient de demander « %@ » via frameline://. N'autorisez que si vous êtes à l'origine de cette action."), action);
        return MessageBox.Show(owner.Window, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
               == MessageBoxResult.Yes;
    }
}
