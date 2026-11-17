using System.Text.Json.Nodes;
using Xunit;

namespace FrameLine.Core.Tests;

public class HotkeyTests
{
    private const HotkeyModifiers CtrlShift = HotkeyModifiers.Ctrl | HotkeyModifiers.Shift;

    [Fact]
    public void Defaults_are_ctrl_shift_4_8_9_E()
    {
        using var tmp = new TempDir();
        var p = new Preferences(tmp.File("settings.json"));
        Assert.Equal(new HotkeySpec(0x34, CtrlShift), p.GetHotkey(HotkeyAction.Region));
        Assert.Equal(new HotkeySpec(0x38, CtrlShift), p.GetHotkey(HotkeyAction.Window));
        Assert.Equal(new HotkeySpec(0x39, CtrlShift), p.GetHotkey(HotkeyAction.FullScreen));
        Assert.Equal(new HotkeySpec(0x45, CtrlShift), p.GetHotkey(HotkeyAction.Record));
        Assert.False(File.Exists(p.SettingsPath));
    }

    [Fact]
    public void A_shortcut_survives_a_reload_under_its_json_key()
    {
        using var tmp = new TempDir();
        var path = tmp.File("settings.json");
        var spec = new HotkeySpec(0x53, HotkeyModifiers.Ctrl | HotkeyModifiers.Alt);
        string? changed = null;
        var p = new Preferences(path);
        p.Changed += n => changed = n;
        p.SetHotkey(HotkeyAction.Region, spec);
        Assert.Equal("hotkeyRegion", changed);

        var json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("Ctrl+Alt+0x53", json["hotkeyRegion"]!.GetValue<string>());
        Assert.Equal(spec, new Preferences(path).GetHotkey(HotkeyAction.Region));
        Assert.Equal(HotkeySpec.Default(HotkeyAction.Window), new Preferences(path).GetHotkey(HotkeyAction.Window));
    }

    [Theory]
    [InlineData("\"garbage\"")]
    [InlineData("\"Ctrl+Shift\"")]
    [InlineData("\"Ctrl+Hyper+0x34\"")]
    [InlineData("\"Ctrl+Ctrl+0x34\"")]
    [InlineData("\"Ctrl+0x1FF\"")]
    [InlineData("\"Ctrl+0x11\"")]        // a modifier key as the main key
    [InlineData("\"Ctrl+52\"")]          // decimal, not the stored hex form
    [InlineData("42")]
    [InlineData("{\"vk\":52}")]
    public void An_unreadable_value_reads_as_the_default(string raw)
    {
        using var tmp = new TempDir();
        var path = tmp.File("settings.json");
        File.WriteAllText(path, "{\"hotkeyRecord\":" + raw + "}");
        Assert.Equal(HotkeySpec.Default(HotkeyAction.Record), new Preferences(path).GetHotkey(HotkeyAction.Record));
    }

    [Fact]
    public void Serialize_and_parse_round_trip_every_modifier()
    {
        var spec = new HotkeySpec(0x2C, HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Win);
        Assert.Equal("Ctrl+Alt+Shift+Win+0x2C", spec.Serialize());
        Assert.True(HotkeySpec.TryParse(spec.Serialize(), out var back));
        Assert.Equal(spec, back);
        Assert.True(HotkeySpec.TryParse("0x7B", out var f12));                // a key alone
        Assert.Equal(new HotkeySpec(0x7B, HotkeyModifiers.None), f12);
        Assert.True(HotkeySpec.TryParse(" shift + ctrl + 0x34 ", out var loose)); // hand-edited
        Assert.Equal(new HotkeySpec(0x34, CtrlShift), loose);
    }

    [Theory]
    [InlineData(0x34u, "4")]
    [InlineData(0x45u, "E")]
    [InlineData(0x70u, "F1")]
    [InlineData(0x87u, "F24")]
    [InlineData(0x2Cu, "PrtScn")]
    [InlineData(0x20u, "Space")]
    [InlineData(0x25u, "←")]
    [InlineData(0x61u, "Num 1")]
    public void Layout_independent_keys_have_a_name(uint vk, string name) =>
        Assert.Equal(name, HotkeySpec.KeyName(vk));

    [Fact]
    public void Punctuation_is_left_to_the_layout() => Assert.Null(HotkeySpec.KeyName(0xBA)); // VK_OEM_1

    [Fact]
    public void Modifier_names_follow_the_Windows_order()
    {
        var spec = new HotkeySpec(0x34, HotkeyModifiers.Win | HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Ctrl);
        Assert.Equal(new[] { "Ctrl", "Alt", "Shift", "Win" }, spec.ModifierNames());
    }

    private static HotkeySpec Defaults(HotkeyAction a) => HotkeySpec.Default(a);

    [Theory]
    [InlineData(0x41u, HotkeyModifiers.Ctrl)]
    [InlineData(0x41u, HotkeyModifiers.Alt)]
    [InlineData(0x41u, HotkeyModifiers.Win)]
    [InlineData(0x41u, HotkeyModifiers.Alt | HotkeyModifiers.Shift)]
    [InlineData(0x70u, HotkeyModifiers.None)]     // F1 alone
    [InlineData(0x87u, HotkeyModifiers.Shift)]    // Shift+F24
    [InlineData(0x2Cu, HotkeyModifiers.None)]     // Print Screen alone
    public void Accepted_shortcuts(uint vk, HotkeyModifiers mods) =>
        Assert.Equal(HotkeyValidation.Ok, HotkeyRules.Validate(HotkeyAction.Region, new HotkeySpec(vk, mods), Defaults, out _));

    [Theory]
    [InlineData(0x41u, HotkeyModifiers.None)]
    [InlineData(0x41u, HotkeyModifiers.Shift)]
    [InlineData(0x20u, HotkeyModifiers.Shift)]
    public void A_shortcut_needs_ctrl_alt_or_win(uint vk, HotkeyModifiers mods) =>
        Assert.Equal(HotkeyValidation.NeedsModifier, HotkeyRules.Validate(HotkeyAction.Region, new HotkeySpec(vk, mods), Defaults, out _));

    [Fact]
    public void A_shortcut_of_another_action_is_refused_and_named()
    {
        var result = HotkeyRules.Validate(HotkeyAction.Region, HotkeySpec.Default(HotkeyAction.Record), Defaults, out var other);
        Assert.Equal(HotkeyValidation.UsedByOtherAction, result);
        Assert.Equal(HotkeyAction.Record, other);
    }

    [Fact]
    public void Keeping_its_own_shortcut_is_fine() =>
        Assert.Equal(HotkeyValidation.Ok,
            HotkeyRules.Validate(HotkeyAction.Window, HotkeySpec.Default(HotkeyAction.Window), Defaults, out _));

    [Fact]
    public void Modifier_keys_are_never_a_main_key()
    {
        foreach (var vk in new uint[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
            Assert.True(HotkeySpec.IsModifierKey(vk));
        Assert.False(HotkeySpec.IsModifierKey(0x34));
    }
}
