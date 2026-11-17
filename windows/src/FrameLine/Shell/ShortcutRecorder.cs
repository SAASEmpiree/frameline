using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FrameLine.Core;

namespace FrameLine.Shell;

/// Settings > Shortcuts button that shows a shortcut as keycaps and records a new one: click, press
/// the keys, Esc cancels. While it records, every global shortcut is suspended (RegisterHotKey would
/// swallow the keys) and the Ctrl+, / Ctrl+Q window shortcuts stand aside (IsRecording).
internal sealed class ShortcutRecorder : Button
{
    /// True while a recorder waits for keys (checked by the Ctrl+, / Ctrl+Q class handler).
    public static bool IsRecording { get; private set; }

    private readonly Func<HotkeySpec> _current;
    private readonly Action<HotkeySpec> _picked;
    private bool _recording;

    /// <param name="picked">A complete combination was typed (validation is the caller's).</param>
    public ShortcutRecorder(Func<HotkeySpec> current, Action<HotkeySpec> picked)
    {
        _current = current;
        _picked = picked;
        MinWidth = 120;
        Focusable = true;
        Click += (_, _) => { if (!_recording) Start(); };
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        LostKeyboardFocus += (_, _) => Stop();
        Unloaded += (_, _) => Stop();
        ShowCurrent();
    }

    public void ShowCurrent()
    {
        var keys = new StackPanel { Orientation = Orientation.Horizontal };
        bool first = true;
        foreach (var k in CaptureHotkey.Keys(_current()))
        {
            keys.Children.Add(Keycap(k, first));
            first = false;
        }
        Content = keys;
    }

    private void Start()
    {
        _recording = true;
        IsRecording = true;
        HotKeyManager.Shared.Suspend();
        var text = new TextBlock { Text = Tr.L("hk_press", "Press the new shortcut… (Esc to cancel)"), FontSize = 12 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "FL.Accent");
        Content = text;
        Focus();
        Keyboard.Focus(this);
    }

    /// Ends recording without a new shortcut (Esc, focus lost, window closed).
    public void Stop()
    {
        if (!_recording) return;
        _recording = false;
        IsRecording = false;
        HotKeyManager.Shared.Resume();
        ShowCurrent();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;                    // no menu (Alt / F10), no focus move (Tab), no click (Space)
        var key = RealKey(e);
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { Stop(); return; }
        if (key == Key.Snapshot) return;     // Print Screen often sends only its key-up: taken there
        Pick(key);
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        if (RealKey(e) == Key.Snapshot) Pick(Key.Snapshot);
    }

    /// The physical key behind Alt combinations (System), an IME or a dead key.
    private static Key RealKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key,
    };

    private void Pick(Key key)
    {
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0 || HotkeySpec.IsModifierKey(vk)) return;   // still waiting for the main key
        var m = Keyboard.Modifiers;
        var mods = HotkeyModifiers.None;
        if (m.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Ctrl;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (m.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;
        _recording = false;
        IsRecording = false;
        // The caller validates and registers (TrySet resumes the other shortcuts itself), then the
        // row shows whatever is current: the new shortcut, or the old one after a refusal.
        _picked(new HotkeySpec(vk, mods));
        HotKeyManager.Shared.Resume();
        ShowCurrent();
    }

    private static Border Keycap(string label, bool first)
    {
        var text = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        var cap = new Border
        {
            MinWidth = 20, Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(first ? 0 : 4, 0, 0, 0),
            CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Child = text,
        };
        cap.SetResourceReference(Border.BackgroundProperty, "FL.ControlHoverBackground");
        cap.SetResourceReference(Border.BorderBrushProperty, "FL.ControlBorder");
        return cap;
    }
}
