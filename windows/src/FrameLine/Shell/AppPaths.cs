using System;
using System.IO;
using System.Reflection;

namespace FrameLine.Shell;

internal static class AppPaths
{
    public static string LocalDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameLine");

    public static string LogsDirectory => Path.Combine(LocalDataDirectory, "logs");

    /// Full path of the running exe (single-file publish included).
    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FrameLine.exe");

    /// "1.0.1" (csproj Version), the CFBundleShortVersionString equivalent.
    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "1.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }
}
