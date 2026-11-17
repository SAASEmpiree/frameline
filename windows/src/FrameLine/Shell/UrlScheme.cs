using System;
using Microsoft.Win32;

namespace FrameLine.Shell;

/// The frameline:// scheme (region, window, full, record, open, settings), registered per user
/// under HKCU\Software\Classes so no elevation is needed. Re-written at each launch so the command
/// follows the exe if it moved.
internal static class UrlScheme
{
    public const string Scheme = "frameline";

    public static void Register()
    {
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}", true);
            root.SetValue("", "URL:FrameLine Protocol");
            root.SetValue("URL Protocol", "");
            using (var icon = root.CreateSubKey("DefaultIcon", true))
                icon.SetValue("", $"\"{AppPaths.ExecutablePath}\",0");
            using var cmd = root.CreateSubKey(@"shell\open\command", true);
            cmd.SetValue("", $"\"{AppPaths.ExecutablePath}\" \"%1\"");
        }
        catch (Exception e) { ESLog.Log("url scheme registration failed: " + e.Message); }
    }

    /// The frameline:// argument of a launch, if any.
    public static string? FromArgs(string[] args)
    {
        foreach (var a in args)
            if (a.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)) return a;
        return null;
    }
}
