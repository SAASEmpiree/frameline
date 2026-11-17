using System.Globalization;

namespace FrameLine.Core;

/// The four global shortcuts the user can change in Settings > Shortcuts.
public enum HotkeyAction { Region, Window, FullScreen, Record }

/// RegisterHotKey modifier bits (MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN), so a value goes to
/// Win32 unchanged.
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x1,
    Ctrl = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// One global shortcut: a Windows virtual-key code plus modifiers. Stored in settings.json as
/// "Ctrl+Shift+0x34" (modifiers by name, the key as a hex VK), readable and hand-editable.
public readonly record struct HotkeySpec(uint Vk, HotkeyModifiers Modifiers)
{
    public const uint VkPrintScreen = 0x2C;   // VK_SNAPSHOT
    public const uint VkEscape = 0x1B;
    public const uint VkF1 = 0x70;
    public const uint VkF24 = 0x87;

    public static HotkeySpec Default(HotkeyAction action) => action switch
    {
        HotkeyAction.Region => new(0x34, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift),      // VK_4
        HotkeyAction.Window => new(0x38, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift),      // VK_8
        HotkeyAction.FullScreen => new(0x39, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift),  // VK_9
        _ => new(0x45, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift),                         // VK_E
    };

    public bool IsFunctionKey => Vk >= VkF1 && Vk <= VkF24;

    /// Ctrl, Shift, Alt and Win keys (generic, left and right): never a shortcut's main key.
    public static bool IsModifierKey(uint vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    /// Modifier names in Windows order: Ctrl, Alt, Shift, Win.
    public IReadOnlyList<string> ModifierNames()
    {
        var names = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) names.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) names.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) names.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) names.Add("Win");
        return names;
    }

    /// Name of a key that does not depend on the keyboard layout, or null (punctuation: the caller
    /// asks the active layout).
    public static string? KeyName(uint vk) => vk switch
    {
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),                 // 0-9
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),                 // A-Z
        >= VkF1 and <= VkF24 => "F" + (vk - VkF1 + 1).ToString(CultureInfo.InvariantCulture),
        >= 0x60 and <= 0x69 => "Num " + (vk - 0x60).ToString(CultureInfo.InvariantCulture),
        0x6A => "Num *",
        0x6B => "Num +",
        0x6D => "Num -",
        0x6E => "Num .",
        0x6F => "Num /",
        VkPrintScreen => "PrtScn",
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x13 => "Pause",
        0x14 => "Caps Lock",
        0x20 => "Space",
        0x21 => "PgUp",
        0x22 => "PgDn",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0x2D => "Ins",
        0x2E => "Del",
        0x90 => "Num Lock",
        0x91 => "Scroll Lock",
        _ => null,
    };

    /// "Ctrl+Shift+0x34".
    public string Serialize()
    {
        var parts = new List<string>(ModifierNames()) { "0x" + Vk.ToString("X2", CultureInfo.InvariantCulture) };
        return string.Join("+", parts);
    }

    /// Reads Serialize()'s form. Anything else (unknown modifier, missing or out-of-range key,
    /// a modifier key as the main key) returns false.
    public static bool TryParse(string? text, out HotkeySpec spec)
    {
        spec = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var mods = HotkeyModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            HotkeyModifiers m = parts[i].ToLowerInvariant() switch
            {
                "ctrl" => HotkeyModifiers.Ctrl,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None,
            };
            if (m == HotkeyModifiers.None || mods.HasFlag(m)) return false;
            mods |= m;
        }
        var key = parts[^1];
        if (!key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !uint.TryParse(key.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vk)
            || vk is 0 or > 0xFE || IsModifierKey(vk))
            return false;
        spec = new HotkeySpec(vk, mods);
        return true;
    }
}

public enum HotkeyValidation
{
    /// Acceptable: try to register it.
    Ok,
    /// Ctrl, Alt or Win is required (F1-F24 and Print Screen are accepted alone).
    NeedsModifier,
    /// Another FrameLine action already has this shortcut.
    UsedByOtherAction,
}

public static class HotkeyRules
{
    /// Checks a shortcut typed for <paramref name="action"/> against the current shortcuts of the
    /// other actions. <paramref name="conflictWith"/> names the action that already has it.
    public static HotkeyValidation Validate(HotkeyAction action, HotkeySpec spec,
        Func<HotkeyAction, HotkeySpec> current, out HotkeyAction conflictWith)
    {
        conflictWith = action;
        const HotkeyModifiers strong = HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win;
        bool standsAlone = spec.IsFunctionKey || spec.Vk == HotkeySpec.VkPrintScreen;
        if ((spec.Modifiers & strong) == 0 && !standsAlone) return HotkeyValidation.NeedsModifier;
        foreach (var other in Enum.GetValues<HotkeyAction>())
        {
            if (other == action || current(other) != spec) continue;
            conflictWith = other;
            return HotkeyValidation.UsedByOtherAction;
        }
        return HotkeyValidation.Ok;
    }
}
