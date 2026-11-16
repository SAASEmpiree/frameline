using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace FrameLine.Core;

// Port of I18n.swift: in-house i18n (no .resx), switched live without restart. Settings and the
// tray menu follow it.
//
// Placeholder convention: table strings keep the macOS placeholders verbatim, "%@" (any value,
// formatted with ToString) and "%d" (integer), plus "%%" for a literal percent. Callers never
// string.Format a table string; they call I18n.Format(key, args) / Loc.F(key, args), which replaces
// placeholders left to right with the arguments in order. Positional "%1$@" / "%2$d" are accepted
// too, for translations that need to reorder.
public sealed class I18n
{
    public static I18n Shared { get; } = new();

    /// Language codes offered in Settings, in display order. "en" is the source of truth.
    public static IReadOnlyList<string> SupportedLanguages { get; } =
        new[] { "en", "fr", "es", "de", "it", "pt-BR", "nl", "ru", "ja", "zh-Hans" };

    private static readonly Dictionary<string, string> NativeNames = new(StringComparer.Ordinal)
    {
        ["en"] = "English", ["fr"] = "Français", ["es"] = "Español", ["de"] = "Deutsch",
        ["it"] = "Italiano", ["pt-BR"] = "Português (Brasil)", ["nl"] = "Nederlands",
        ["ru"] = "Русский", ["ja"] = "日本語", ["zh-Hans"] = "简体中文",
    };

    private readonly Preferences _prefs;
    private readonly Func<IReadOnlyList<string>> _systemLanguages;
    private string _selection;
    private string? _systemResolved;

    public I18n() : this(Preferences.Shared, null) { }

    /// For tests: explicit settings and a fake list of OS preferred languages (BCP-47 names).
    public I18n(Preferences prefs, Func<IReadOnlyList<string>>? systemLanguages)
    {
        _prefs = prefs;
        _systemLanguages = systemLanguages ?? SystemPreferredLanguages;
        _selection = prefs.DisplayLanguage;
        // Someone may write Preferences.DisplayLanguage directly (settings binding): follow it.
        prefs.Changed += name =>
        {
            if (name == nameof(Preferences.DisplayLanguage)) Apply(prefs.DisplayLanguage);
        };
    }

    /// Raw choice: "system" or a code. What the language picker binds to.
    public string Selection => Volatile.Read(ref _selection);

    /// Effective language: the choice, or for "system" the first OS preferred language FrameLine
    /// covers, else "en".
    public string CurrentLanguage
    {
        get
        {
            var sel = Selection;
            if (sel != "system") return sel;
            return _systemResolved ??= Resolve(_systemLanguages());
        }
    }

    /// Raised after the display language changes, on the thread that changed it.
    public event Action? LanguageChanged;

    /// Translated string: display language, then English, then the raw key.
    public string T(string key)
    {
        if (Strings.Table.TryGetValue(CurrentLanguage, out var table) && table.TryGetValue(key, out var s)) return s;
        if (Strings.Table["en"].TryGetValue(key, out var en)) return en;
        return key;
    }

    /// T(key) with its %@ / %d placeholders filled, see the convention at the top of the file.
    public string Format(string key, params object?[] args) => FormatString(T(key), args);

    /// "system" or a code from SupportedLanguages; persists through Preferences.DisplayLanguage.
    public void Set(string code)
    {
        if (code == Selection) return;
        _prefs.DisplayLanguage = code;   // Changed -> Apply
        Apply(code);                     // no-op if the handler already ran
    }

    /// Forgets the cached OS language, e.g. after WM_SETTINGCHANGE "intl".
    public void RefreshSystemLanguage()
    {
        _systemResolved = null;
        if (Selection == "system") LanguageChanged?.Invoke();
    }

    /// Native name of a language ("Français") for the picker.
    public static string NativeName(string code) => NativeNames.TryGetValue(code, out var n) ? n : code;

    private void Apply(string code)
    {
        if (Interlocked.Exchange(ref _selection, code) == code) return;
        LanguageChanged?.Invoke();
    }

    /// Same matching as the Swift effLang: same language + script wins, else same language code
    /// ("pt-PT" -> "pt-BR", "zh-TW" -> "zh-Hans", as on macOS), else the next preference.
    internal static string Resolve(IEnumerable<string> preferred)
    {
        foreach (var pref in preferred)
        {
            if (string.IsNullOrWhiteSpace(pref)) continue;
            var (code, script) = Split(pref);
            if (code.Length == 0) continue;
            if (script != null)
            {
                var m = SupportedLanguages.FirstOrDefault(s =>
                {
                    var (c, sc) = Split(s);
                    return c == code && sc == script;
                });
                if (m != null) return m;
            }
            var byCode = SupportedLanguages.FirstOrDefault(s =>
                s.Equals(code, StringComparison.OrdinalIgnoreCase) || s.StartsWith(code + "-", StringComparison.OrdinalIgnoreCase));
            if (byCode != null) return byCode;
        }
        return "en";
    }

    // Language code + script ("zh-Hans-CN" -> ("zh", "Hans")). Windows names usually omit the script
    // ("zh-CN"), so it is inferred from the culture's parent chain (zh-CN -> zh-Hans).
    private static (string code, string? script) Split(string tag)
    {
        var parts = tag.Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return ("", null);
        var code = parts[0].ToLowerInvariant();
        var script = parts.Skip(1).FirstOrDefault(p => p.Length == 4 && p.All(char.IsLetter));
        if (script != null) return (code, char.ToUpperInvariant(script[0]) + script[1..].ToLowerInvariant());
        try
        {
            for (var c = CultureInfo.GetCultureInfo(tag); !string.IsNullOrEmpty(c.Name); c = c.Parent)
            {
                var p = c.Name.Split('-');
                if (p.Length >= 2 && p[1].Length == 4) return (code, p[1]);
            }
        }
        catch (CultureNotFoundException) { }
        return (code, null);
    }

    private static IReadOnlyList<string> SystemPreferredLanguages()
    {
        var list = new List<string>();
        if (OperatingSystem.IsWindows()) list.AddRange(WindowsPreferredUiLanguages());
        // CurrentUICulture is the Windows display language; it also covers non-Windows test hosts.
        list.Add(CultureInfo.CurrentUICulture.Name);
        return list;
    }

    private const uint MUI_LANGUAGE_NAME = 0x8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserPreferredUILanguages(uint dwFlags, out uint pulNumLanguages,
        [Out] char[]? pwszLanguagesBuffer, ref uint pcchLanguagesBuffer);

    /// The user's ordered UI language list (Settings > Time & language), as a multi-string.
    private static IEnumerable<string> WindowsPreferredUiLanguages()
    {
        try
        {
            uint size = 0;
            if (!GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, out _, null, ref size) || size == 0)
                return Array.Empty<string>();
            var buffer = new char[size];
            if (!GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, out _, buffer, ref size))
                return Array.Empty<string>();
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return Array.Empty<string>();
        }
    }

    /// Fills %@ / %d / %1$@ placeholders, see the convention at the top of the file. Missing
    /// arguments leave the placeholder as is rather than throw.
    public static string FormatString(string template, params object?[] args)
    {
        var sb = new StringBuilder(template.Length + 16);
        var next = 0;
        for (var i = 0; i < template.Length; i++)
        {
            var ch = template[i];
            if (ch != '%' || i + 1 >= template.Length) { sb.Append(ch); continue; }

            var j = i + 1;
            int? position = null;
            var digits = j;
            while (digits < template.Length && char.IsAsciiDigit(template[digits])) digits++;
            if (digits > j && digits + 1 < template.Length && template[digits] == '$')
            {
                position = int.Parse(template.AsSpan(j, digits - j), CultureInfo.InvariantCulture) - 1;
                j = digits + 1;
            }

            var spec = template[j];
            if (spec == '%' && position == null) { sb.Append('%'); i = j; continue; }
            if (spec != '@' && spec != 'd') { sb.Append(ch); continue; }

            var index = position ?? next++;
            if (index < 0 || index >= args.Length) { sb.Append(template, i, j - i + 1); i = j; continue; }
            var arg = args[index];
            sb.Append(spec == 'd' && arg is IFormattable f
                ? f.ToString("0", CultureInfo.CurrentCulture)
                : Convert.ToString(arg, CultureInfo.CurrentCulture));
            i = j;
        }
        return sb.ToString();
    }
}

public static class Loc
{
    public static string L(string key) => I18n.Shared.T(key);

    /// L(key) with %@ / %d filled in order: Loc.F("m_seconds", 5) -> "5 seconds".
    public static string F(string key, params object?[] args) => I18n.Shared.Format(key, args);
}
