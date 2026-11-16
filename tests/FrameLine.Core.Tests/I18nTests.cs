using System.Text.RegularExpressions;
using Xunit;

namespace FrameLine.Core.Tests;

public class I18nTests
{
    private static I18n Make(TempDir tmp, params string[] system) =>
        new(new Preferences(tmp.File("settings.json")), () => system);

    [Fact]
    public void Supported_languages_and_native_names()
    {
        Assert.Equal(new[] { "en", "fr", "es", "de", "it", "pt-BR", "nl", "ru", "ja", "zh-Hans" }, I18n.SupportedLanguages);
        Assert.Equal("Français", I18n.NativeName("fr"));
        Assert.Equal("简体中文", I18n.NativeName("zh-Hans"));
        Assert.Equal("xx", I18n.NativeName("xx"));
        Assert.Equal(I18n.SupportedLanguages.OrderBy(x => x), Strings.Table.Keys.OrderBy(x => x));
    }

    [Theory]
    [InlineData("fr-FR", "fr")]
    [InlineData("fr-CA", "fr")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-Hans-CN", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hans")]
    [InlineData("ja-JP", "ja")]
    [InlineData("en-GB", "en")]
    [InlineData("sv-SE", "en")]
    public void System_language_resolution_matches_macOS(string system, string expected)
    {
        Assert.Equal(expected, I18n.Resolve(new[] { system }));
    }

    [Fact]
    public void First_covered_preferred_language_wins()
    {
        Assert.Equal("de", I18n.Resolve(new[] { "sv-SE", "fi", "de-AT", "fr" }));
        Assert.Equal("en", I18n.Resolve(Array.Empty<string>()));
    }

    [Fact]
    public void Lookup_falls_back_to_english_then_to_the_key()
    {
        using var tmp = new TempDir();
        var i = Make(tmp, "es-ES");
        Assert.Equal("es", i.CurrentLanguage);
        Assert.Equal("General", Strings.Table["en"]["tab_general"]);
        Assert.Equal(Strings.Table["es"]["tab_general"], i.T("tab_general"));
        // sec_mcp is missing from the Spanish table in the macOS source too.
        Assert.False(Strings.Table["es"].ContainsKey("sec_mcp"));
        Assert.Equal("Local MCP server", i.T("sec_mcp"));
        Assert.Equal("no_such_key", i.T("no_such_key"));
    }

    [Fact]
    public void Set_persists_and_raises_LanguageChanged_once()
    {
        using var tmp = new TempDir();
        var prefs = new Preferences(tmp.File("settings.json"));
        var i = new I18n(prefs, () => new[] { "en-US" });
        var raised = 0;
        i.LanguageChanged += () => raised++;

        i.Set("fr");
        Assert.Equal(1, raised);
        Assert.Equal("fr", i.CurrentLanguage);
        Assert.Equal("Général", i.T("tab_general"));
        Assert.Equal("fr", new Preferences(prefs.SettingsPath).DisplayLanguage);

        i.Set("fr");
        Assert.Equal(1, raised);

        prefs.DisplayLanguage = "system";   // written elsewhere: followed too
        Assert.Equal(2, raised);
        Assert.Equal("en", i.CurrentLanguage);
        Assert.Equal("system", i.Selection);
    }

    [Fact]
    public void Every_language_key_exists_in_english()
    {
        foreach (var (lang, table) in Strings.Table)
            foreach (var key in table.Keys)
                Assert.True(Strings.Table["en"].ContainsKey(key), $"{lang}:{key} has no English source");
    }

    [Fact]
    public void Key_counts_match_the_Swift_source_plus_the_Windows_keys()
    {
        // Strings.swift: en 72, fr 72, every other language 54; + 4 Windows-only keys each, + 151
        // strings the macOS app hard-coded outside the table, translated in every language.
        var expected = new Dictionary<string, int> { ["en"] = 76 + 151, ["fr"] = 76 + 151 };
        foreach (var lang in I18n.SupportedLanguages)
            Assert.Equal(expected.GetValueOrDefault(lang, 58 + 151), Strings.Table[lang].Count);
    }

    [Theory]
    [InlineData("reveal_in_explorer")]
    [InlineData("tray_tooltip")]
    [InlineData("mic_permission_windows")]
    [InlineData("open_windows_settings")]
    [InlineData("sound_mac")]
    public void Windows_keys_are_translated_in_every_language(string key)
    {
        foreach (var lang in I18n.SupportedLanguages)
        {
            Assert.True(Strings.Table[lang].ContainsKey(key), $"{lang} lacks {key}");
            if (lang != "en") Assert.NotEqual(Strings.Table["en"][key], Strings.Table[lang][key]);
        }
    }

    [Fact]
    public void No_string_names_macOS_only_things()
    {
        var mac = new Regex(@"\bmacOS\b|\bMac\b|Finder|⌘|menu bar|System Settings");
        foreach (var (lang, table) in Strings.Table)
            foreach (var (key, value) in table)
                Assert.False(mac.IsMatch(value), $"{lang}:{key} = {value}");
    }

    [Fact]
    public void Placeholders_are_consistent_with_english()
    {
        var ph = new Regex(@"%(\d+\$)?[@d]|\{\d+\}");
        foreach (var (lang, table) in Strings.Table)
            foreach (var (key, value) in table)
            {
                var want = ph.Matches(Strings.Table["en"][key]).Select(m => m.Value[^1]).OrderBy(c => c);
                var got = ph.Matches(value).Select(m => m.Value[^1]).OrderBy(c => c);
                Assert.True(want.SequenceEqual(got), $"{lang}:{key} placeholders differ");
            }
    }

    [Fact]
    public void Format_fills_placeholders_in_order()
    {
        using var tmp = new TempDir();
        var i = Make(tmp, "en-US");
        Assert.Equal("5 seconds", i.Format("m_seconds", 5));
        Assert.Equal("FrameLine 1.2.0 is the latest version.", i.Format("uptodate_msg", "1.2.0"));
        Assert.Equal("Running on 127.0.0.1:4567", i.Format("mcp_status_running", "127.0.0.1:4567"));
        i.Set("ja");
        Assert.Contains("10", i.Format("m_seconds", 10));
    }

    [Theory]
    [InlineData("%@ and %d", new object[] { "a", 3 }, "a and 3")]
    [InlineData("%2$@ before %1$@", new object[] { "x", "y" }, "y before x")]
    [InlineData("100%% sure %@", new object[] { "ok" }, "100% sure ok")]
    [InlineData("missing %@ %@", new object[] { "one" }, "missing one %@")]
    [InlineData("trailing %", new object[0], "trailing %")]
    [InlineData("other %s kept", new object[] { "z" }, "other %s kept")]
    public void FormatString_cases(string template, object[] args, string expected)
    {
        Assert.Equal(expected, I18n.FormatString(template, args));
    }
}
