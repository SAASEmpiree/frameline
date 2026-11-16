using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using FrameLine.Core;

namespace FrameLine.Library;

/// The UI-side library (port of the selection + gallery half of CaptureLibrary.swift): mirrors
/// CaptureLibrary.Shared.Items into an observable list, keeps the Finder-style selection
/// (focus + multi-selection + Shift anchor) and bounds the thumbnail memory. UI thread only.
internal sealed class LibraryModel
{
    private static LibraryModel? _shared;
    public static LibraryModel Shared => _shared ??= new LibraryModel();

    private const int MaxCachedCovers = 300;

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, CaptureEntry> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CaptureEntry> _coverLru = new();
    private readonly Dictionary<CaptureEntry, LinkedListNode<CaptureEntry>> _coverNodes = new();
    private string? _anchor;
    private string? _pendingFocus;
    private bool _hasLoadedOnce;
    private bool _syncScheduled;
    private DispatcherTimer? _clock;
    private int _clockUsers;

    public RangeObservableCollection<CaptureEntry> Items { get; } = new();

    /// Focal capture (last clicked): drives the detail panel.
    public string? Focus { get; private set; }
    public IReadOnlyCollection<string> SelectedPaths => _selected;

    /// Raised after any focus / multi-selection change.
    public event Action? SelectionChanged;
    /// Raised after the list content changed (count badge, empty states, history panel).
    public event Action? ItemsChanged;

    private LibraryModel()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        CaptureLibrary.Shared.Changed += () => _dispatcher.BeginInvoke(ScheduleSync);
        Sync();
    }

    public CaptureEntry? FocusedEntry => Focus != null && _byPath.TryGetValue(Focus, out var e) ? e : null;

    /// Captures targeted by "Delete": the multi-selection if any, else the focus.
    public IReadOnlyList<string> ActionableSelection =>
        _selected.Count > 0 ? OrderedSelection() : Focus != null ? new[] { Focus } : Array.Empty<string>();

    public CaptureEntry? Find(string path) => _byPath.TryGetValue(path, out var e) ? e : null;

    // MARK: - Selection (Finder-like)

    public void SelectOnly(string path)
    {
        if (!_byPath.ContainsKey(path))
        {
            // Asked before the folder scan saw the file (capture just written): select it as soon
            // as it shows up.
            _pendingFocus = path;
            RequestReload();
        }
        Focus = path;
        _selected.Clear();
        _selected.Add(path);
        _anchor = path;
        ApplySelection();
    }

    public void ToggleSelection(string path)
    {
        if (_selected.Contains(path))
        {
            _selected.Remove(path);
            if (string.Equals(Focus, path, StringComparison.OrdinalIgnoreCase)) Focus = FocusAfterRemoving(path);
        }
        else
        {
            _selected.Add(path);
            Focus = path;
        }
        _anchor = path;
        ApplySelection();
    }

    public void ExtendSelection(string path)
    {
        var anchor = _anchor ?? Focus;
        int a = anchor == null ? -1 : IndexOf(anchor), b = IndexOf(path);
        if (a < 0 || b < 0) { SelectOnly(path); return; }
        _selected.Clear();
        for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) _selected.Add(Items[i].Path);
        Focus = path;                                   // focus follows the click; anchor stays
        ApplySelection();
    }

    /// Ctrl = toggle (Cmd on macOS), Shift = range, plain click = only this one.
    public void HandleClick(string path, bool ctrl, bool shift)
    {
        if (ctrl) ToggleSelection(path);
        else if (shift) ExtendSelection(path);
        else SelectOnly(path);
    }

    /// Keyboard Up/Down in the sidebar (a Windows list convention; macOS has no equivalent).
    public void MoveFocus(int delta, bool extend)
    {
        if (Items.Count == 0) return;
        int i = Focus == null ? -1 : IndexOf(Focus);
        int next = Math.Clamp(i < 0 ? 0 : i + delta, 0, Items.Count - 1);
        if (extend) ExtendSelection(Items[next].Path); else SelectOnly(Items[next].Path);
    }

    public int IndexOf(string path)
    {
        for (int i = 0; i < Items.Count; i++)
            if (string.Equals(Items[i].Path, path, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private List<string> OrderedSelection() => Items.Where(e => _selected.Contains(e.Path)).Select(e => e.Path).ToList();

    /// After removing `path`, focus the nearest still-selected neighbour (below first, then above).
    private string? FocusAfterRemoving(string path)
    {
        if (_selected.Count == 0) return null;
        int idx = IndexOf(path);
        if (idx < 0) return OrderedSelection().FirstOrDefault();
        for (int off = 1; idx + off < Items.Count || idx - off >= 0; off++)
        {
            if (idx + off < Items.Count && _selected.Contains(Items[idx + off].Path)) return Items[idx + off].Path;
            if (idx - off >= 0 && _selected.Contains(Items[idx - off].Path)) return Items[idx - off].Path;
        }
        return OrderedSelection().FirstOrDefault();
    }

    private void ApplySelection()
    {
        foreach (var e in Items) e.IsSelected = _selected.Contains(e.Path);
        SelectionChanged?.Invoke();
    }

    // MARK: - Sync with the folder

    public void RequestReload() => Task.Run(() =>
    {
        try { CaptureLibrary.Shared.Reload(); } catch { }
    }).ContinueWith(_ => _dispatcher.BeginInvoke(ScheduleSync));

    private void ScheduleSync()
    {
        if (_syncScheduled) return;
        _syncScheduled = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _syncScheduled = false; Sync(); });
    }

    private void Sync()
    {
        IReadOnlyList<CaptureItem> source;
        try { source = CaptureLibrary.Shared.Items; } catch { source = Array.Empty<CaptureItem>(); }
        var ordered = source.OrderByDescending(i => i.Created).ToList();

        var known = new HashSet<string>(_byPath.Keys, StringComparer.OrdinalIgnoreCase);
        var desired = new List<CaptureEntry>(ordered.Count);
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in ordered)
        {
            if (!live.Add(it.Path)) continue;
            if (_byPath.TryGetValue(it.Path, out var e)) e.Update(it.Created, it.IsVideo);
            else _byPath[it.Path] = e = new CaptureEntry(it.Path, it.Created, it.IsVideo);
            e.ClearFailure();
            desired.Add(e);
        }
        foreach (var gone in _byPath.Keys.Where(k => !live.Contains(k)).ToList())
        {
            var e = _byPath[gone];
            ForgetCover(e);
            _byPath.Remove(gone);
        }
        ApplyOrder(desired);

        // Selection invariants, verbatim from CaptureLibrary.refresh().
        _selected.RemoveWhere(p => !live.Contains(p));
        if (_anchor != null && !live.Contains(_anchor)) _anchor = null;
        if (_pendingFocus != null && live.Contains(_pendingFocus))
        {
            var p = _pendingFocus;
            _pendingFocus = null;
            Focus = p; _selected.Clear(); _selected.Add(p); _anchor = p;
        }
        else if (_hasLoadedOnce && desired.Count > 0)
        {
            // A NEW file appeared (capture, recording, import): it becomes the selection, which is
            // what CaptureLibrary.add / importFiles do on macOS.
            var fresh = desired.FirstOrDefault(e => !known.Contains(e.Path));
            if (fresh != null && known.Count > 0)
            {
                Focus = fresh.Path; _selected.Clear(); _selected.Add(fresh.Path); _anchor = fresh.Path;
            }
        }
        if (_pendingFocus != null && !live.Contains(_pendingFocus) && !File.Exists(_pendingFocus)) _pendingFocus = null;
        bool focusPending = Focus != null && string.Equals(Focus, _pendingFocus, StringComparison.OrdinalIgnoreCase);
        if (Focus != null && !live.Contains(Focus) && !focusPending)
            Focus = OrderedSelection().FirstOrDefault() ?? desired.FirstOrDefault()?.Path;
        else if (!_hasLoadedOnce && Focus == null)
            Focus = desired.FirstOrDefault()?.Path;     // very first scan: newest capture
        if (desired.Count > 0) _hasLoadedOnce = true;
        if (Focus != null && _selected.Count == 0) _selected.Add(Focus);

        ItemsChanged?.Invoke();
        ApplySelection();
    }

    private void ApplyOrder(List<CaptureEntry> desired)
    {
        if (Items.Count == 0 || Math.Abs(Items.Count - desired.Count) > 200)
        {
            Items.ReplaceAll(desired);
            return;
        }
        var keep = new HashSet<CaptureEntry>(desired);
        for (int i = Items.Count - 1; i >= 0; i--)
            if (!keep.Contains(Items[i])) Items.RemoveAt(i);
        int ops = 0;
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < Items.Count && ReferenceEquals(Items[i], desired[i])) continue;
            if (++ops > 200) { Items.ReplaceAll(desired); return; }
            int j = -1;
            for (int k = i + 1; k < Items.Count; k++)
                if (ReferenceEquals(Items[k], desired[i])) { j = k; break; }
            if (j >= 0) Items.Move(j, i); else Items.Insert(i, desired[i]);
        }
        while (Items.Count > desired.Count) Items.RemoveAt(Items.Count - 1);
    }

    // MARK: - Thumbnail memory

    public void TouchCover(CaptureEntry e)
    {
        if (_coverNodes.TryGetValue(e, out var node)) _coverLru.Remove(node);
        _coverNodes[e] = _coverLru.AddFirst(e);
        var cur = _coverLru.Last;
        while (_coverLru.Count > MaxCachedCovers && cur != null)
        {
            var prev = cur.Previous;
            if (!cur.Value.IsVisible)
            {
                cur.Value.DropCover();
                _coverNodes.Remove(cur.Value);
                _coverLru.Remove(cur);
            }
            cur = prev;
        }
    }

    private void ForgetCover(CaptureEntry e)
    {
        if (_coverNodes.Remove(e, out var node)) _coverLru.Remove(node);
    }

    // MARK: - Relative-time clock ("5 min. ago" keeps moving while a library view is open)

    public void AddClockUser()
    {
        if (_clockUsers++ > 0) return;
        _clock ??= new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background,
            (_, _) => { foreach (var e in Items) e.RaiseSubtitles(); }, _dispatcher);
        _clock.Start();
    }

    public void RemoveClockUser()
    {
        if (--_clockUsers <= 0) { _clockUsers = 0; _clock?.Stop(); }
    }

    // MARK: - Actions that span the selection

    /// Port of AppCoordinator.trashSelection: every actionable capture to the Recycle Bin (through
    /// the coordinator, which knows captures still being written), one confirmation toast.
    public void TrashSelection()
    {
        var paths = ActionableSelection.ToList();
        if (paths.Count == 0) return;
        BeforeTrash?.Invoke(paths);
        int trashed = 0;
        foreach (var p in paths)
        {
            try { Shell.AppCoordinator.Shared.Trash(p); } catch { }
            if (!File.Exists(p)) trashed++;
        }
        RequestReload();
        if (trashed == 0) return;
        LibToast.Show(trashed > 1 ? LibText.F("lib_deleted_many", trashed) : LibText.T("lib_deleted_one"), Glyphs.Delete);
    }

    /// One capture to the Recycle Bin (context menu outside a multi-selection).
    public void TrashOne(string path)
    {
        BeforeTrash?.Invoke(new[] { path });
        try { Shell.AppCoordinator.Shared.Trash(path); } catch { }
        RequestReload();
    }

    /// Raised before files go to the Recycle Bin, so a view playing / editing one releases it
    /// first (an open file cannot be moved).
    public event Action<IReadOnlyList<string>>? BeforeTrash;

    /// Drag-and-drop import (CaptureLibrary.importFiles): copies images / videos into the capture
    /// folder (" (2)" when the name is taken) off the UI thread, then selects the last one.
    public void ImportFiles(IEnumerable<string> files)
    {
        var list = files.ToList();
        Task.Run(() =>
        {
            try { return CaptureLibrary.Shared.ImportFiles(list); }
            catch { return (IReadOnlyList<string>)Array.Empty<string>(); }
        }).ContinueWith(t =>
        {
            var imported = t.Result;
            if (imported.Count == 0) return;
            _dispatcher.BeginInvoke(() => SelectOnly(imported[^1]));
        });
    }
}

/// ObservableCollection with a single Reset for bulk loads (2,000 Add events would each relayout).
internal sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
