using System;
using Microsoft.Win32;

namespace FrameLine.Shell;

/// SMAppService.mainApp → HKCU\Software\Microsoft\Windows\CurrentVersion\Run, value "FrameLine".
/// The getter reads the real state (the user can remove the entry from Task Manager > Startup apps,
/// which writes a "disabled" flag under StartupApproved), like Preferences.launchAtLogin did.
public static class LaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "FrameLine";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey);
                if (run?.GetValue(ValueName) is not string cmd || string.IsNullOrWhiteSpace(cmd)) return false;
                // StartupApproved: first byte 0x02 (or even) = enabled, 0x03 (odd) = disabled by the user.
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                if (approved?.GetValue(ValueName) is byte[] flags && flags.Length > 0 && (flags[0] & 1) == 1) return false;
                return true;
            }
            catch (Exception e) { ESLog.Log("launchAtLogin read error: " + e.Message); return false; }
        }
        set
        {
            try
            {
                using var run = Registry.CurrentUser.CreateSubKey(RunKey, true);
                if (value)
                {
                    run.SetValue(ValueName, $"\"{AppPaths.ExecutablePath}\"", RegistryValueKind.String);
                    // Clear a previous "disabled in Task Manager" flag, otherwise the toggle would lie.
                    using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true);
                    if (approved?.GetValue(ValueName) != null) approved.DeleteValue(ValueName, false);
                }
                else if (run.GetValue(ValueName) != null)
                {
                    run.DeleteValue(ValueName, false);
                }
            }
            catch (Exception e) { ESLog.Log("launchAtLogin write error: " + e.Message); }
        }
    }

    /// The exe moved (new version unzipped elsewhere): keep an enabled entry pointing at this exe.
    public static void RefreshPathIfEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (run?.GetValue(ValueName) is string cmd && !string.IsNullOrWhiteSpace(cmd))
            {
                var expected = $"\"{AppPaths.ExecutablePath}\"";
                if (!string.Equals(cmd, expected, StringComparison.OrdinalIgnoreCase))
                    run.SetValue(ValueName, expected, RegistryValueKind.String);
            }
        }
        catch (Exception e) { ESLog.Log("launchAtLogin refresh error: " + e.Message); }
    }
}
