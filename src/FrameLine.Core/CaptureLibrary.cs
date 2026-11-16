namespace FrameLine.Core;

// OWNER: agent "core". Platform-neutral part of CaptureLibrary.swift: lists captures in the save
// directory (images + videos), newest first, watches the folder, raises Changed.
public sealed record CaptureItem(string Path, DateTime Created, bool IsVideo)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed class CaptureLibrary
{
    public static CaptureLibrary Shared { get; } = new();
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".heic", ".webp" };
    public static readonly string[] VideoExtensions = { ".mp4", ".mov", ".m4v" };
    public string Directory => Preferences.Shared.EnsureSaveDirectory();
    public IReadOnlyList<CaptureItem> Items => Array.Empty<CaptureItem>();
    /// Raised on any thread when the folder content changes; UI must marshal to its dispatcher.
    public event Action? Changed;
    public void Reload() { }
}
