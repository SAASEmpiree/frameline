using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using FrameLine.Core;
using FrameLine.Platform;

namespace FrameLine.Shell;

/// Port of HotKeyManager.swift: system-wide shortcuts with RegisterHotKey on a message-only window
/// (no hook, no elevated rights, like Carbon's RegisterEventHotKey). A shortcut already owned by
/// another app is reported to the user once per session. The shortcuts come from Preferences and
/// can be changed live from Settings (TrySet), which suspends them while it records a key.
public sealed class HotKeyManager : IDisposable
{
    public static HotKeyManager Shared { get; } = new();

    private static readonly HotkeyAction[] Actions = Enum.GetValues<HotkeyAction>();

    private HwndSource? _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private readonly HashSet<int> _registered = new();
    private bool _conflictReported;
    private bool _suspended;

    /// Labels of the shortcuts another application already owns (empty when all are ours).
    public IReadOnlyList<string> Conflicts => _conflicts;
    private readonly List<string> _conflicts = new();

    private HotKeyManager() { }

    private static int IdOf(HotkeyAction action) => (int)action + 1;

    /// Registers the 4 shortcuts saved in Preferences (Ctrl+Shift+4 / 8 / 9 / E by default).
    /// Call on the UI thread.
    public void RegisterDefaults(Action region, Action window, Action full, Action record)
    {
        EnsureSource();
        _handlers[IdOf(HotkeyAction.Region)] = region;
        _handlers[IdOf(HotkeyAction.Window)] = window;
        _handlers[IdOf(HotkeyAction.FullScreen)] = full;
        _handlers[IdOf(HotkeyAction.Record)] = record;
        RegisterAll();
        ReportConflictsOnce();
    }

    /// True when the action's shortcut is registered (false: another app owns it, or suspended).
    public bool IsActive(HotkeyAction action) => _registered.Contains(IdOf(action));

    /// Releases every shortcut while Settings records a new one: a registered combination never
    /// reaches the window that has the focus. Resume() puts them back.
    public void Suspend()
    {
        if (_suspended || _source == null) return;
        _suspended = true;
        foreach (var id in _registered) Native.UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
        ESLog.Log("hotkeys suspended");
    }

    public void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        RegisterAll();
        ESLog.Log("hotkeys resumed");
    }

    /// Replaces an action's shortcut: registers the new one first and saves it only when Windows
    /// accepts it. When another application owns it, the previous shortcut is put back and the
    /// method returns false. Validation (modifier, duplicate between actions) is HotkeyRules' job.
    public bool TrySet(HotkeyAction action, HotkeySpec spec)
    {
        Resume();
        if (_source == null) return false;
        var previous = Preferences.Shared.GetHotkey(action);
        int id = IdOf(action);
        if (_registered.Remove(id)) Native.UnregisterHotKey(_source.Handle, id);
        if (Register(id, spec))
        {
            Preferences.Shared.SetHotkey(action, spec);
            RefreshConflicts();
            return true;
        }
        Register(id, previous);
        RefreshConflicts();
        return false;
    }

    /// Back to Ctrl+Shift+4 / 8 / 9 / E. Returns the actions whose default another app owns
    /// (those keep their current shortcut).
    public IReadOnlyList<HotkeyAction> ResetToDefaults()
    {
        var failed = new List<HotkeyAction>();
        // Free every slot first: a default may be what another action currently uses.
        Resume();
        if (_source != null)
            foreach (var id in _registered) Native.UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
        foreach (var action in Actions)
        {
            var spec = HotkeySpec.Default(action);
            if (Register(IdOf(action), spec)) Preferences.Shared.SetHotkey(action, spec);
            else failed.Add(action);
        }
        foreach (var action in failed) Register(IdOf(action), Preferences.Shared.GetHotkey(action));
        RefreshConflicts();
        return failed;
    }

    private void EnsureSource()
    {
        if (_source != null) return;
        // HWND_MESSAGE parent: invisible, never enumerated, still receives WM_HOTKEY.
        var p = new HwndSourceParameters("FrameLine.HotKeys")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    private void RegisterAll()
    {
        if (_source == null) return;
        foreach (var action in Actions)
        {
            int id = IdOf(action);
            if (_registered.Contains(id) || !_handlers.ContainsKey(id)) continue;
            Register(id, Preferences.Shared.GetHotkey(action));
        }
        RefreshConflicts();
    }

    private bool Register(int id, HotkeySpec spec)
    {
        if (_source == null) return false;
        bool ok = Native.RegisterHotKey(_source.Handle, id, (uint)spec.Modifiers | Native.MOD_NOREPEAT, spec.Vk);
        if (ok)
        {
            _registered.Add(id);
            ESLog.Log($"RegisterHotKey id={id} {spec.Serialize()} → OK");
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            ESLog.Log($"RegisterHotKey id={id} {spec.Serialize()} → FAILED error={err}");
        }
        return ok;
    }

    private void RefreshConflicts()
    {
        _conflicts.Clear();
        if (_suspended) return;
        foreach (var action in Actions)
            if (_handlers.ContainsKey(IdOf(action)) && !IsActive(action))
                _conflicts.Add(CaptureHotkey.Label(Preferences.Shared.GetHotkey(action)));
    }

    private void ReportConflictsOnce()
    {
        if (_conflicts.Count == 0 || _conflictReported) return;
        _conflictReported = true;
        Notifier.ShowMessage("FrameLine", Tr.Format(
            Tr.L("hotkey_conflict", "%@ is already used by another application. Close that application or change its shortcut, then restart FrameLine."),
            string.Join(", ", _conflicts)));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_HOTKEY) return IntPtr.Zero;
        int id = wParam.ToInt32();
        ESLog.Log($"HotKey PRESSED id={id}");
        if (_handlers.TryGetValue(id, out var action))
        {
            handled = true;
            // Leave the message handler before running the action (it may open modal UI).
            _source?.Dispatcher.BeginInvoke(action);
        }
        return IntPtr.Zero;
    }

    public void UnregisterAll()
    {
        if (_source == null) return;
        foreach (var id in _registered) Native.UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
        _handlers.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }
}
