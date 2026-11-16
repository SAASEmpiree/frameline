using System.Collections.Generic;

namespace FrameLine.Shell;

/// Port of Shortcuts.swift (CaptureHotkey): the single source of the capture shortcuts.
/// macOS ⇧⌘ + physical key becomes Ctrl+Shift + virtual key. Digit-row virtual keys are the same on
/// QWERTY and AZERTY (the AZERTY "4 '" key is VK_4), so the shortcuts are layout-independent, and
/// the label shows the digit, which is how Windows names these keys.
public static class CaptureHotkey
{
    public const uint Region = 0x34;      // VK_4 — "Capture Area"
    public const uint Window = 0x38;      // VK_8 — "Capture Window"
    public const uint FullScreen = 0x39;  // VK_9 — "Capture Fullscreen"
    public const uint Record = 0x45;      // VK_E — "Start/Stop Recording"

    public const string Ctrl = "Ctrl";
    public const string Shift = "Shift";

    public static string Display(uint vk) => vk switch
    {
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        _ => $"0x{vk:X2}",
    };

    /// "Ctrl+Shift+4" (Windows order: Ctrl before Shift).
    public static string Label(uint vk) => $"{Ctrl}+{Shift}+{Display(vk)}";

    /// ["Ctrl", "Shift", "4"] for keycaps.
    public static IReadOnlyList<string> Keys(uint vk) => new[] { Ctrl, Shift, Display(vk) };

    /// Shown next to Settings / Quit in the tray menu (handled while a FrameLine window has focus).
    public const string SettingsHint = "Ctrl+,";
    public const string QuitHint = "Ctrl+Q";
}
