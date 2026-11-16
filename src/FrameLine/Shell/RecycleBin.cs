using System;
using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace FrameLine.Shell;

/// Finder "Move to Trash" → Recycle Bin (reversible), never a permanent delete.
internal static class RecycleBin
{
    public static bool Send(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
            return !File.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or OperationCanceledException)
        {
            ESLog.Log("recycle failed: " + e.Message);
            return false;
        }
    }
}
