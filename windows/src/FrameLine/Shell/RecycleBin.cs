using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace FrameLine.Shell;

/// Finder "Move to Trash" → Recycle Bin (reversible), never a silent permanent delete.
/// SHFileOperation is called directly: Microsoft.VisualBasic's DeleteFile sets FOF_NOCONFIRMATION
/// without FOF_WANTNUKEWARNING, so a file that cannot be recycled (network share, drive without a
/// bin, file bigger than the bin, bin disabled) was destroyed with no prompt. With the nuke warning
/// Windows asks first; a "No" leaves the file in place and it is not counted as deleted.
internal static class RecycleBin
{
    public static bool Send(string path) => SendMany(new[] { path }) == 1;

    /// Recycles every existing path in one shell operation (a single nuke prompt for the batch) and
    /// returns how many are actually gone.
    public static int SendMany(IEnumerable<string> paths)
    {
        var existing = paths.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (existing.Count == 0) return 0;

        // pFrom: NUL-separated full paths, terminated by a double NUL.
        var from = new StringBuilder();
        foreach (var p in existing) from.Append(p).Append('\0');
        from.Append('\0');

        var op = new SHFILEOPSTRUCTW
        {
            hwnd = OwnerHandle(),
            wFunc = FO_DELETE,
            pFrom = from.ToString(),
            pTo = null,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_SILENT | FOF_NOERRORUI,
        };
        try
        {
            int rc = SHFileOperationW(ref op);
            if (rc != 0) ESLog.Log($"recycle failed: 0x{rc:X}");
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            ESLog.Log("recycle failed: " + e.Message);
        }
        // Partial success is possible (one file locked, a "No" to the nuke prompt): count what left.
        return existing.Count(p => !File.Exists(p));
    }

    /// The nuke prompt is parented to our active window when there is one, so it is not hidden behind it.
    private static IntPtr OwnerHandle()
    {
        var app = Application.Current;
        if (app == null || !app.Dispatcher.CheckAccess()) return IntPtr.Zero;
        foreach (Window w in app.Windows)
            if (w.IsActive) return new WindowInteropHelper(w).Handle;
        return IntPtr.Zero;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    // Natural alignment on x64 (the 1-byte packing of the Windows header applies to 32-bit only).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);
}
