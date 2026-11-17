using System.IO;

namespace FrameLine.Shell;

/// Port of Notifications.swift (Notifier). An unpackaged Win32 app has no UNUserNotificationCenter
/// equivalent without an AppUserModelID shortcut, so notifications go through the tray icon's
/// balloon, which Windows 10/11 shows as a regular toast in the notification centre. Clicking the
/// "capture saved" toast opens that capture in the library.
public static class Notifier
{
    public static void NotifyCapture(string path, bool isVideo)
    {
        var title = isVideo
            ? Tr.L("notif_recording_done", "Enregistrement terminé")
            : Tr.L("notif_capture_saved", "Capture enregistrée");
        StatusBarController.Current?.ShowBalloon(title, Path.GetFileName(path),
            () => AppCoordinator.Shared.OpenInApp(path));
    }

    public static void ShowMessage(string title, string text) =>
        StatusBarController.Current?.ShowBalloon(title, text, null);
}
