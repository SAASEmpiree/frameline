using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FrameLine.Core.Mcp;

public sealed record McpAppIdentity(
    string Name, string BundleId, string Version, int Pid,
    string EmployeeName, string DisplayName, string Description, string Color);

/// Discovery file: %APPDATA%\FrameLine\mcp.json, readable by the current user only, written
/// atomically, removed on stop/quit. Claude-Code-compatible "mcpServers" block plus an "app" block
/// that Coucou reads to present FrameLine as one of the CEO's employees. Same shape as macOS.
public static class McpDiscovery
{
    public const string ServerKey = "frameline";

    public static string DefaultDirectory(string? appData = null) =>
        Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrameLine");

    public static string DefaultFile(string? appData = null) => Path.Combine(DefaultDirectory(appData), "mcp.json");

    public static string Url(int port) => $"http://127.0.0.1:{port}/mcp";

    /// The block any MCP client understands (also what "Copy MCP config" copies).
    public static JsonObject ClientConfig(int port, string token) => new()
    {
        ["mcpServers"] = new JsonObject
        {
            [ServerKey] = new JsonObject
            {
                ["type"] = "http",
                ["url"] = Url(port),
                ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {token}" },
            },
        },
    };

    public static JsonObject Document(int port, string token, McpAppIdentity app)
    {
        var doc = ClientConfig(port, token);
        doc["app"] = new JsonObject
        {
            ["name"] = app.Name,
            ["bundleId"] = app.BundleId,
            ["version"] = app.Version,
            ["pid"] = app.Pid,
            ["employee"] = new JsonObject
            {
                ["name"] = app.EmployeeName,
                ["displayName"] = app.DisplayName,
                ["description"] = app.Description,
                ["color"] = app.Color,
            },
        };
        return doc;
    }

    /// Atomic, private write: temp file created exclusively in the same directory with its final
    /// permissions already set (current-user-only DACL on Windows, 0600 elsewhere), flushed to
    /// disk, then moved over the target. There is never an instant where the token is readable
    /// by someone else.
    public static void Write(JsonObject document, string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
        }
        else
        {
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var data = McpJson.Bytes(document);
        var tmp = Path.Combine(dir, $".mcp.json.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        bool ok = false;
        try
        {
            using (var stream = CreatePrivate(tmp))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            MoveWithRetry(tmp, file);
            ok = true;
        }
        finally
        {
            if (!ok) TryDelete(tmp);
        }
    }

    private static FileStream CreatePrivate(string path)
    {
        if (OperatingSystem.IsWindows()) return CreateWindowsPrivate(path);
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateWindowsPrivate(string path)
    {
        // Protected DACL (no inheritance from %APPDATA%): the current user only, full control.
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var user = WindowsIdentity.GetCurrent().User!;
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.Synchronize
            | FileSystemRights.ReadAttributes, FileShare.None, 4096, FileOptions.None, security);
    }

    /// A reader (Coucou, a client) holding the file open makes the replace fail briefly on Windows.
    private static void MoveWithRetry(string from, string to)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) when (attempt < 10) { Thread.Sleep(50); }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// Removes the file only if it is still ours (same pid): a newer instance's file survives.
    public static bool RemoveIfOwned(string file, int pid)
    {
        try
        {
            if (!File.Exists(file)) return false;
            var doc = JsonNode.Parse(File.ReadAllBytes(file));
            if (McpJson.Int(McpJson.Get(McpJson.Get(doc, "app"), "pid")) != pid) return false;
            File.Delete(file);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
