using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FrameLine.Core;

// Port of Preferences.swift. Persisted as JSON in %APPDATA%\FrameLine\settings.json (or the path given
// to the ctor, for tests). JSON keys are the macOS UserDefaults keys, so the two apps read alike.
// Unknown keys are kept on save: a newer build's settings survive a downgrade.
public sealed class Preferences
{
    public static Preferences Shared { get; } = new();

    private static class K
    {
        public const string SaveDir = "saveDirectoryPath";
        public const string Format = "imageFormat";
        public const string CopyClipboard = "copyToClipboard";
        public const string PlaySound = "playSound";
        public const string ShowPreview = "showPreview";
        public const string CaptureCursor = "captureCursor";
        public const string WindowShadow = "windowShadow";
        public const string RecordFps = "recordFps";
        public const string GifFps = "gifFps";
        public const string GifWidth = "gifWidth";
        public const string ExportGif = "exportGIF";
        public const string RecordSystemAudio = "recordSystemAudio";
        public const string RecordMicrophone = "recordMicrophone";
        public const string PreviewFollowsCursor = "previewFollowsCursor";
        public const string PreviewCorner = "previewCorner";
        public const string ClosePreviewAfterDrag = "closePreviewAfterDrag";
        public const string CaptureSoundName = "captureSoundName";
        public const string RecordSoundName = "recordSoundName";
        public const string McpServerEnabled = "mcpServerEnabled";
        public const string OnboardingCompleted = "onboardingCompleted";
        public const string Theme = "theme";
        public const string OpenEditor = "openEditor";
        public const string DisplayLanguage = "displayLang";
    }

    private readonly object _gate = new();
    private readonly JsonObject _values;

    /// Full path of the JSON file backing these settings.
    public string SettingsPath { get; }

    /// Clock used for capture file names (tests pin it).
    internal Func<DateTime> Now { get; set; } = () => DateTime.Now;

    public Preferences(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrameLine", "settings.json");
        _values = Load(SettingsPath);
    }

    /// Default capture folder: the user's Pictures known folder + FrameLine (the macOS ~/Pictures/FrameLine).
    public static string DefaultSaveDirectory
    {
        get
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(pictures))
                pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
            return Path.Combine(pictures, "FrameLine");
        }
    }

    /// Capture folder. Reads the default when nothing (or an empty string) is stored; assigning ""
    /// returns to the default.
    public string SaveDirectory
    {
        get
        {
            var p = GetString(K.SaveDir, "");
            return string.IsNullOrWhiteSpace(p) ? DefaultSaveDirectory : p;
        }
        set => SetValue(K.SaveDir, string.IsNullOrWhiteSpace(value) ? "" : Path.GetFullPath(value), nameof(SaveDirectory));
    }

    public string ImageFormat { get => GetString(K.Format, "png"); set => SetValue(K.Format, value, nameof(ImageFormat)); }
    public bool CopyToClipboard { get => GetBool(K.CopyClipboard, false); set => SetValue(K.CopyClipboard, value, nameof(CopyToClipboard)); }
    public bool PlaySound { get => GetBool(K.PlaySound, true); set => SetValue(K.PlaySound, value, nameof(PlaySound)); }
    public bool ShowPreview { get => GetBool(K.ShowPreview, true); set => SetValue(K.ShowPreview, value, nameof(ShowPreview)); }
    public bool CaptureCursor { get => GetBool(K.CaptureCursor, false); set => SetValue(K.CaptureCursor, value, nameof(CaptureCursor)); }
    public bool WindowShadow { get => GetBool(K.WindowShadow, true); set => SetValue(K.WindowShadow, value, nameof(WindowShadow)); }
    // Like the Swift getters, a stored 0 or negative value reads as the default.
    public int RecordFps { get => GetPositiveInt(K.RecordFps, 30); set => SetValue(K.RecordFps, value, nameof(RecordFps)); }
    public int GifFps { get => GetPositiveInt(K.GifFps, 15); set => SetValue(K.GifFps, value, nameof(GifFps)); }
    public int GifWidth { get => GetPositiveInt(K.GifWidth, 900); set => SetValue(K.GifWidth, value, nameof(GifWidth)); }
    public bool ExportGif { get => GetBool(K.ExportGif, true); set => SetValue(K.ExportGif, value, nameof(ExportGif)); }
    public bool RecordSystemAudio { get => GetBool(K.RecordSystemAudio, true); set => SetValue(K.RecordSystemAudio, value, nameof(RecordSystemAudio)); }
    public bool RecordMicrophone { get => GetBool(K.RecordMicrophone, false); set => SetValue(K.RecordMicrophone, value, nameof(RecordMicrophone)); }
    public bool PreviewFollowsCursor { get => GetBool(K.PreviewFollowsCursor, true); set => SetValue(K.PreviewFollowsCursor, value, nameof(PreviewFollowsCursor)); }
    /// "bottomLeft" | "topLeft" | "bottomRight" | "topRight".
    public string PreviewCorner { get => GetString(K.PreviewCorner, "bottomLeft"); set => SetValue(K.PreviewCorner, value, nameof(PreviewCorner)); }
    public bool ClosePreviewAfterDrag { get => GetBool(K.ClosePreviewAfterDrag, false); set => SetValue(K.ClosePreviewAfterDrag, value, nameof(ClosePreviewAfterDrag)); }
    /// "default" (bundled file) or a sound id from the capture list.
    public string CaptureSoundName { get => GetString(K.CaptureSoundName, "default"); set => SetValue(K.CaptureSoundName, value, nameof(CaptureSoundName)); }
    /// "default" or a sound id from the recording list.
    public string RecordSoundName { get => GetString(K.RecordSoundName, "default"); set => SetValue(K.RecordSoundName, value, nameof(RecordSoundName)); }
    public bool McpServerEnabled { get => GetBool(K.McpServerEnabled, true); set => SetValue(K.McpServerEnabled, value, nameof(McpServerEnabled)); }
    public bool OnboardingCompleted { get => GetBool(K.OnboardingCompleted, false); set => SetValue(K.OnboardingCompleted, value, nameof(OnboardingCompleted)); }
    /// "system" | "light" | "dark".
    public string Theme { get => GetString(K.Theme, "dark"); set => SetValue(K.Theme, value, nameof(Theme)); }
    public bool OpenEditor { get => GetBool(K.OpenEditor, false); set => SetValue(K.OpenEditor, value, nameof(OpenEditor)); }
    /// "system" or a code from I18n.SupportedLanguages.
    public string DisplayLanguage { get => GetString(K.DisplayLanguage, "system"); set => SetValue(K.DisplayLanguage, value, nameof(DisplayLanguage)); }

    /// JSON key of an action's shortcut: hotkeyRegion, hotkeyWindow, hotkeyFullScreen, hotkeyRecord.
    public static string HotkeyKey(HotkeyAction action) => "hotkey" + action;

    /// The action's global shortcut; a missing or unreadable value reads as the default.
    public HotkeySpec GetHotkey(HotkeyAction action) =>
        HotkeySpec.TryParse(GetString(HotkeyKey(action), ""), out var spec) ? spec : HotkeySpec.Default(action);

    /// Changed is raised with the JSON key (e.g. "hotkeyRegion").
    public void SetHotkey(HotkeyAction action, HotkeySpec spec) =>
        SetValue(HotkeyKey(action), spec.Serialize(), HotkeyKey(action));

    /// Raised after a property value actually changes, with the C# property name (or the raw key for
    /// SetCustom). Raised on the writing thread, outside the lock.
    public event Action<string>? Changed;

    /// Typed access for settings other modules own (e.g. a Windows-only flag) without a contract change.
    public T GetCustom<T>(string key, T fallback)
    {
        lock (_gate)
        {
            if (_values[key] is not { } node) return fallback;
            try { return node.Deserialize<T>() ?? fallback; }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return fallback; }
        }
    }

    public void SetCustom<T>(string key, T value) => SetNode(key, JsonSerializer.SerializeToNode(value), key);

    /// Ensures the capture folder exists. When it cannot be created (removed drive, bad path, no
    /// permission), falls back to the default folder so a capture is never lost silently.
    public string EnsureSaveDirectory()
    {
        var dir = SaveDirectory;
        if (TryCreate(dir)) return dir;
        var fallback = DefaultSaveDirectory;
        TryCreate(fallback);
        Trace.WriteLine($"FrameLine: capture folder unavailable, falling back to {fallback}");
        return fallback;
    }

    /// "FrameLine 2026-11-16 à 16.37.05.png", then " (2)", " (3)"... when the name is taken.
    public string NewCapturePath(string ext)
    {
        var dir = EnsureSaveDirectory();
        ext = ext.TrimStart('.');
        // Computed once so every candidate shares the same stamp. Invariant culture: the pattern is
        // numeric only, and a fr-FR CultureInfo would throw under InvariantGlobalization.
        var stamp = Now().ToString("yyyy-MM-dd 'à' HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.Combine(dir, $"FrameLine {stamp}.{ext}");
        for (var n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(dir, $"FrameLine {stamp} ({n}).{ext}");
        return path;
    }

    private static bool TryCreate(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private string GetString(string key, string fallback)
    {
        lock (_gate)
            return _values[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
    }

    private bool GetBool(string key, bool fallback)
    {
        lock (_gate)
            return _values[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
    }

    private int GetPositiveInt(string key, int fallback)
    {
        lock (_gate)
        {
            if (_values[key] is not JsonValue v) return fallback;
            if (v.TryGetValue<int>(out var i)) return i > 0 ? i : fallback;
            // Numbers read back from disk are JsonElement-backed; a hand-edited 30.0 is accepted too.
            if (v.TryGetValue<double>(out var dbl) && dbl >= 1 && dbl <= int.MaxValue) return (int)dbl;
            return fallback;
        }
    }

    private void SetValue(string key, string value, string name) => SetNode(key, JsonValue.Create(value), name);
    private void SetValue(string key, bool value, string name) => SetNode(key, JsonValue.Create(value), name);
    private void SetValue(string key, int value, string name) => SetNode(key, JsonValue.Create(value), name);

    private void SetNode(string key, JsonNode? node, string name)
    {
        lock (_gate)
        {
            if (JsonNode.DeepEquals(_values[key], node) && _values.ContainsKey(key)) return;
            _values[key] = node;
            Save();
        }
        Changed?.Invoke(name);
    }

    private static JsonObject Load(string path)
    {
        string text;
        try
        {
            if (!File.Exists(path)) return new JsonObject();
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"FrameLine: cannot read settings ({e.Message}), using defaults");
            return new JsonObject();
        }

        try
        {
            if (JsonNode.Parse(text) is JsonObject obj) return obj;
        }
        catch (JsonException) { }

        // Corrupt or not an object: keep a copy for diagnosis, then start from defaults. The next
        // write replaces the broken file.
        try { File.Copy(path, path + ".corrupt", overwrite: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Trace.WriteLine("FrameLine: settings file unreadable, defaults restored");
        return new JsonObject();
    }

    // Caller holds _gate. Write to a sibling temp file, then swap it in, so a crash or power loss
    // mid-write never leaves a truncated settings.json.
    private void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        var tmp = Path.Combine(dir, $".{Path.GetFileName(SettingsPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(dir);
            var json = _values.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                w.Write(json);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            try
            {
                if (File.Exists(SettingsPath)) File.Replace(tmp, SettingsPath, null, ignoreMetadataErrors: true);
                else File.Move(tmp, SettingsPath);
            }
            catch (IOException)
            {
                // File.Replace can fail transiently on Windows (indexer/antivirus holding the target);
                // a plain overwrite move is still atomic at the directory-entry level on NTFS.
                File.Move(tmp, SettingsPath, overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"FrameLine: cannot save settings ({e.Message})");
            try { File.Delete(tmp); } catch (Exception) { }
        }
    }
}
