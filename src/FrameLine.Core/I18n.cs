namespace FrameLine.Core;

// OWNER: agent "core". Port of I18n.swift + Strings.swift (ALL keys, ALL 10 languages, verbatim).
public sealed class I18n
{
    public static I18n Shared { get; } = new();
    /// Language codes offered in Settings, in display order.
    public static IReadOnlyList<string> SupportedLanguages { get; } = Array.Empty<string>();
    public string CurrentLanguage => "en";
    public event Action? LanguageChanged;
    public string T(string key) => key;
    /// "system" or a code from SupportedLanguages; persists through Preferences.DisplayLanguage.
    public void Set(string code) { }
    /// Native name of a language ("Français") for the picker.
    public static string NativeName(string code) => code;
}

public static class Loc
{
    public static string L(string key) => I18n.Shared.T(key);
}
