using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using FrameLine.Platform;

namespace FrameLine.Shell;

/// Port of HotKeyManager.swift: system-wide shortcuts with RegisterHotKey on a message-only window
/// (no hook, no elevated rights, like Carbon's RegisterEventHotKey). A shortcut already owned by
/// another app is reported to the user once per session.
public sealed class HotKeyManager : IDisposable
{
    public static HotKeyManager Shared { get; } = new();

    private HwndSource? _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private readonly List<int> _registered = new();
    private bool _conflictReported;

    /// Labels of the shortcuts another application already owns (empty when all are ours).
    public IReadOnlyList<string> Conflicts => _conflicts;
    private readonly List<string> _conflicts = new();

    private HotKeyManager() { }

    /// The 4 default shortcuts (Ctrl+Shift+4 / 8 / 9 / E). Call on the UI thread.
    public void RegisterDefaults(Action region, Action window, Action full, Action record)
    {
        EnsureSource();
        Register(1, CaptureHotkey.Region, region);
        Register(2, CaptureHotkey.Window, window);
        Register(3, CaptureHotkey.FullScreen, full);
        Register(4, CaptureHotkey.Record, record);
        ReportConflictsOnce();
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

    private void Register(int id, uint vk, Action action)
    {
        if (_source == null) return;
        _handlers[id] = action;
        bool ok = Native.RegisterHotKey(_source.Handle, id, Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT, vk);
        if (ok)
        {
            _registered.Add(id);
            ESLog.Log($"RegisterHotKey id={id} vk=0x{vk:X2} → OK");
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            ESLog.Log($"RegisterHotKey id={id} vk=0x{vk:X2} → FAILED error={err}");
            _conflicts.Add(CaptureHotkey.Label(vk));
        }
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
