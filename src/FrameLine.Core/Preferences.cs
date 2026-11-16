namespace FrameLine.Core;

// OWNER: agent "core". Port of Preferences.swift. Stub signatures are the contract; keep them.
// Persisted as JSON in %APPDATA%\FrameLine\settings.json (or a path given to the ctor for tests).
public sealed class Preferences
{
    public static Preferences Shared { get; } = new();
    public Preferences(string? settingsPath = null) { }

    public string SaveDirectory { get; set; } = "";          // default: %USERPROFILE%\Pictures\FrameLine
    public string ImageFormat { get; set; } = "png";          // "png" | "jpg"
    public bool CopyToClipboard { get; set; }
    public bool PlaySound { get; set; } = true;
    public bool ShowPreview { get; set; } = true;
    public bool CaptureCursor { get; set; }
    public bool WindowShadow { get; set; } = true;
    public int RecordFps { get; set; } = 30;
    public int GifFps { get; set; } = 15;
    public int GifWidth { get; set; } = 900;
    public bool ExportGif { get; set; } = true;
    public bool RecordSystemAudio { get; set; } = true;
    public bool RecordMicrophone { get; set; }
    public bool PreviewFollowsCursor { get; set; } = true;
    public string PreviewCorner { get; set; } = "bottomLeft"; // bottomLeft|topLeft|bottomRight|topRight
    public bool ClosePreviewAfterDrag { get; set; }
    public string CaptureSoundName { get; set; } = "default";
    public string RecordSoundName { get; set; } = "default";
    public bool McpServerEnabled { get; set; } = true;
    public bool OnboardingCompleted { get; set; }
    public string Theme { get; set; } = "dark";               // system | light | dark
    public bool OpenEditor { get; set; }
    public string DisplayLanguage { get; set; } = "system";   // "system" or a language code

    /// Raised after any property is written (UI rebinding, MCP toggle...).
    public event Action<string>? Changed;

    public string EnsureSaveDirectory() => throw new NotImplementedException();
    /// "FrameLine 2026-11-16 à 16.37.05.png", "(2)" suffix when taken.
    public string NewCapturePath(string ext) => throw new NotImplementedException();
}
