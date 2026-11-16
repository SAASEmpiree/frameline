using System.Globalization;
using System.Text.Json.Nodes;

namespace FrameLine.Core.Mcp;

/// Capture-folder helpers shared by the tools: listing and path containment. Read-only.
public static class CaptureFolder
{
    // The macOS sets, plus .webp/.tif that the Windows library also shows.
    public static readonly IReadOnlySet<string> ImageExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "tiff", "tif", "gif", "heic", "bmp", "webp" };
    public static readonly IReadOnlySet<string> VideoExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mov", "mp4", "m4v" };

    public enum Kind { All, Image, Video }

    public static string Name(Kind kind) => kind switch
    {
        Kind.Image => "image",
        Kind.Video => "video",
        _ => "all",
    };

    public static Kind? ParseKind(string raw) => raw switch
    {
        "all" => Kind.All,
        "image" => Kind.Image,
        "video" => Kind.Video,
        _ => null,
    };

    public sealed record Item(string Path, DateTime ModifiedUtc, long Size, bool IsVideo)
    {
        public JsonObject ToJson() => new()
        {
            ["path"] = Path,
            ["name"] = System.IO.Path.GetFileName(Path),
            ["kind"] = IsVideo ? "video" : "image",
            ["modified"] = Iso8601(ModifiedUtc),
            ["size_bytes"] = Size,
        };
    }

    /// ISO 8601 internet date-time in UTC, seconds precision ("2026-11-16T15:37:05Z").
    public static string Iso8601(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static Kind? KindOf(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        if (ext.Length < 2) return null;
        ext = ext[1..];
        if (ImageExtensions.Contains(ext)) return Kind.Image;
        if (VideoExtensions.Contains(ext)) return Kind.Video;
        return null;
    }

    private static bool IsHidden(FileSystemInfo info) =>
        info.Name.StartsWith('.') || (info.Attributes & FileAttributes.Hidden) != 0;

    /// Captures in `directory` (non recursive, hidden files and links skipped), newest first.
    public static IReadOnlyList<Item> List(string directory, Kind kind = Kind.All, int limit = 20)
    {
        var items = new List<Item>();
        DirectoryInfo dir;
        IEnumerable<FileInfo> files;
        try
        {
            dir = new DirectoryInfo(directory);
            if (!dir.Exists) return items;
            files = dir.EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
            }).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return items;
        }
        foreach (var file in files)
        {
            try
            {
                if (IsHidden(file) || file.LinkTarget is not null) continue;
                if (KindOf(file.FullName) is not Kind k || (kind != Kind.All && kind != k)) continue;
                items.Add(new Item(file.FullName, file.LastWriteTimeUtc, file.Length, k == Kind.Video));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return items.OrderByDescending(i => i.ModifiedUtc).Take(Math.Max(0, limit)).ToList();
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// Resolves a user-supplied path (absolute, or a bare file name) to an existing capture file
    /// directly INSIDE `directory`. Links (symlinks, junctions) and ".." are resolved before the
    /// containment check, so nothing outside the capture folder can be reached. null when outside,
    /// missing or not a capture.
    public static string? Resolve(string raw, string directory)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed.Contains('\0')) return null;
        try
        {
            var expanded = ExpandTilde(trimmed);
            string candidate;
            if (Path.IsPathFullyQualified(expanded)) candidate = expanded;
            else if (Path.IsPathRooted(expanded)) return null;      // "\x" or "C:x": relative to an unknown place
            else candidate = Path.Combine(directory, expanded);

            var lexicalFile = Path.GetFullPath(candidate);
            var lexicalDir = Path.GetFullPath(directory);
            var dir = RealPath(lexicalDir);
            if (dir is null) return null;
            // Never touch a path on another drive or share before the containment check: probing
            // "\\host\share\x.png" would open an SMB connection (and leak NTLM credentials).
            var fileRoot = Path.GetPathRoot(lexicalFile);
            if (!string.Equals(fileRoot, Path.GetPathRoot(lexicalDir), PathComparison)
                && !string.Equals(fileRoot, Path.GetPathRoot(dir), PathComparison)) return null;
            var file = RealPath(lexicalFile);
            if (file is null) return null;
            // Directly in the folder, like the library.
            var parent = Path.GetDirectoryName(file);
            if (parent is null || !string.Equals(TrimSeparators(parent), TrimSeparators(dir), PathComparison)) return null;
            var name = Path.GetFileName(file);
            // "a.png:stream" would address an NTFS alternate data stream.
            if (name.Contains(':')) return null;
            if (KindOf(file) is null) return null;
            if (!File.Exists(file)) return null;
            return file;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string TrimSeparators(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length < root.Length ? root : trimmed;
    }

    private static string ExpandTilde(string path)
    {
        if (path == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return path;
    }

    /// Resolves every link along an absolute, normalized path (like realpath(3)). Components that
    /// do not exist are kept as they are. null when a link loop is detected.
    internal static string? RealPath(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return null;
        var rest = fullPath[root.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                                                 StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        var pending = new Queue<string>(rest);
        int hops = 0;
        while (pending.Count > 0)
        {
            var next = Path.Combine(current, pending.Dequeue());
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.Exists && info.LinkTarget is not null)
            {
                if (++hops > 40) return null;
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null) return null;
                var resolved = Path.GetFullPath(target.FullName);
                // The target may itself sit under links: restart from its root.
                var targetRoot = Path.GetPathRoot(resolved)!;
                var remaining = pending.ToArray();
                pending = new Queue<string>(resolved[targetRoot.Length..]
                    .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                    .Concat(remaining));
                current = targetRoot;
                continue;
            }
            current = next;
        }
        return current;
    }
}
