using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace FrameLine.Shell;

/// The Windows replacement of the macOS permission step: there is no screen-capture permission,
/// only the microphone privacy switches (Settings > Privacy & security > Microphone). Desktop apps
/// are governed by the global switch and by "Let desktop apps access your microphone"
/// (ConsentStore\microphone\NonPackaged). Read-only: Windows never prompts a desktop app.
public static class MicrophoneAccess
{
    private const string ConsentKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    public static bool IsAllowed =>
        !IsDenied(Registry.LocalMachine, ConsentKey) &&
        !IsDenied(Registry.CurrentUser, ConsentKey) &&
        !IsDenied(Registry.CurrentUser, ConsentKey + @"\NonPackaged");

    public static void OpenSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { ESLog.Log("open mic settings: " + e.Message); }
    }

    private static bool IsDenied(RegistryKey hive, string path)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue("Value") is string v && v.Equals("Deny", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }
}
