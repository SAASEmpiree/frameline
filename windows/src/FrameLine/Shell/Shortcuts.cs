using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FrameLine.Core;

namespace FrameLine.Shell;

/// Port of Shortcuts.swift (CaptureHotkey): the single source of the capture shortcuts. Each one is
/// a virtual key + modifiers, Ctrl+Shift+4 / 8 / 9 / E by default (the macOS ⇧⌘ + physical key),
/// and the user can change it in Settings > Shortcuts. Digit-row virtual keys are the same on
/// QWERTY and AZERTY (the AZERTY "4 '" key is VK_4), so the label shows the digit, which is how
/// Windows names these keys; punctuation keys show the character of the active layout.
public static class CaptureHotkey
{
    /// Current shortcuts (Preferences), what HotKeyManager registers.
    public static HotkeySpec Region => Get(HotkeyAction.Region);
    public static HotkeySpec Window => Get(HotkeyAction.Window);
    public static HotkeySpec FullScreen => Get(HotkeyAction.FullScreen);
    public static HotkeySpec Record => Get(HotkeyAction.Record);

    public static HotkeySpec Get(HotkeyAction action) => Preferences.Shared.GetHotkey(action);

    /// A shortcut was changed (Settings) or reset. Raised on the thread that wrote it (the UI thread).
    public static event Action? ShortcutsChanged;

    static CaptureHotkey()
    {
        Preferences.Shared.Changed += name =>
        {
            if (name.StartsWith("hotkey", StringComparison.Ordinal)) ShortcutsChanged?.Invoke();
        };
    }

    /// Tray and Settings label of an action ("Capture Area"…).
    public static string ActionTitle(HotkeyAction action) => action switch
    {
        HotkeyAction.Region => Tr.L("m_capture_area"),
        HotkeyAction.Window => Tr.L("m_capture_window"),
        HotkeyAction.FullScreen => Tr.L("m_capture_fullscreen"),
        _ => Tr.L("hk_record", "Start / stop recording"),
    };

    public static string Display(uint vk) => HotkeySpec.KeyName(vk) ?? LayoutCharacter(vk) ?? $"0x{vk:X2}";

    /// "Ctrl+Shift+4" (Windows order: Ctrl, Alt, Shift, Win).
    public static string Label(HotkeySpec spec) => string.Join("+", Keys(spec));

    /// ["Ctrl", "Shift", "4"] for keycaps.
    public static IReadOnlyList<string> Keys(HotkeySpec spec)
    {
        var keys = new List<string>(spec.ModifierNames()) { Display(spec.Vk) };
        return keys;
    }

    /// Shown next to Settings / Quit in the tray menu (handled while a FrameLine window has focus).
    public const string SettingsHint = "Ctrl+,";
    public const string QuitHint = "Ctrl+Q";

    /// Unshifted character of a punctuation key in the active layout (";" on QWERTY, "$" on AZERTY…).
    private static string? LayoutCharacter(uint vk)
    {
        const uint MAPVK_VK_TO_CHAR = 2;
        uint ch = MapVirtualKeyW(vk, MAPVK_VK_TO_CHAR) & 0x7FFF;   // high bit = dead key
        if (ch < 0x21) return null;
        return char.ToUpperInvariant((char)ch).ToString();
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);
}
