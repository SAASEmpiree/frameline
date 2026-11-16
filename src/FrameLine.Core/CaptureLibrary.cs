namespace FrameLine.Core;

/// One capture (image or video) in the capture folder. `Created` holds the file's last-write time,
/// as the macOS library sorts on the modification date: a capture re-saved with annotations moves up.
/// Record equality covers every field, so a file rewritten in place compares as changed.
public sealed record CaptureItem(string Path, DateTime Created, bool IsVideo)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

// Platform-neutral part of CaptureLibrary.swift: lists captures in the save directory (images +
// videos), newest first, watches the folder, raises Changed. Selection and thumbnails live in the
// Library module.
public sealed class CaptureLibrary : IDisposable
{
    public static CaptureLibrary Shared { get; } = new();

    // macOS set (png jpg jpeg tiff gif heic bmp) plus the Windows spellings tif and webp.
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".tiff", ".tif", ".gif", ".bmp", ".heic", ".webp" };
    public static readonly string[] VideoExtensions = { ".mp4", ".mov", ".m4v" };

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

    private readonly Func<string> _directory;
    private readonly object _gate = new();
    private readonly Timer _debounce;
    private IReadOnlyList<CaptureItem> _items = Array.Empty<CaptureItem>();
    private bool _loaded;
    private FileSystemWatcher? _watcher;
    private string? _watchedDirectory;
    private bool _disposed;

    public CaptureLibrary() : this(() => Preferences.Shared.EnsureSaveDirectory())
    {
        Preferences.Shared.Changed += name =>
        {
            if (name == nameof(Preferences.SaveDirectory)) DirectoryChanged();
        };
    }

    /// For tests or a custom folder: the provider must return an existing (or creatable) folder.
    public CaptureLibrary(Func<string> directoryProvider)
    {
        _directory = directoryProvider;
        _debounce = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// Effective capture folder (falls back to the default folder when the chosen one is unusable).
    public string Directory => _directory();

    /// Snapshot, newest first. Loads on first access; afterwards updated by Reload and the watcher.
    public IReadOnlyList<CaptureItem> Items
    {
        get
        {
            if (!Volatile.Read(ref _loaded)) Reload();
            return Volatile.Read(ref _items);
        }
    }

    /// Raised on any thread when the folder content changes; UI must marshal to its dispatcher.
    public event Action? Changed;

    /// Initial scan + folder watching. Call once at app start.
    public void Start()
    {
        Reload();
        StartWatching();
    }

    /// The save folder setting changed: watch the new folder and rescan. Also wired automatically
    /// for Shared through Preferences.Changed.
    public void DirectoryChanged()
    {
        if (_disposed) return;
        if (_watcher != null) StartWatching();
        Reload();
    }

    /// Rescans the folder. Raises Changed when the list differs from the previous snapshot.
    public void Reload() => Reload(force: false);

    /// A file was rewritten in place (annotations saved): rescan and always raise Changed so the UI
    /// drops its cached thumbnail even if the listing looks the same.
    public void ReloadFile(string path) => Reload(force: true);

    private void Reload(bool force)
    {
        if (_disposed) return;
        bool changed;
        lock (_gate)
        {
            var items = Scan(Directory);
            changed = force || !_loaded || !items.SequenceEqual(_items);
            Volatile.Write(ref _items, items);
            Volatile.Write(ref _loaded, true);
        }
        if (changed) Changed?.Invoke();
    }

    /// Lists captures in `dir`, newest first (ties by name, descending, so the order is stable).
    public static IReadOnlyList<CaptureItem> Scan(string dir)
    {
        var list = new List<CaptureItem>();
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            });
            foreach (var f in files)
            {
                if (f.Name.StartsWith('.')) continue;   // macOS skipsHiddenFiles: dot files too
                var kind = Classify(f.Name);
                if (kind == null) continue;
                DateTime date;
                try { date = f.LastWriteTime; } catch (IOException) { date = DateTime.MinValue; }
                list.Add(new CaptureItem(f.FullName, date, kind.Value));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Missing or unreadable folder: empty library, like the Swift `?? []`.
        }
        list.Sort((a, b) =>
        {
            var c = b.Created.CompareTo(a.Created);
            return c != 0 ? c : string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    /// true = video, false = image, null = not a capture.
    public static bool? Classify(string path)
    {
        var ext = Path.GetExtension(path);
        if (VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return true;
        if (ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return false;
        return null;
    }

    /// Copies dropped files into the capture folder (images and videos only, " (n)" when the name is
    /// taken). Returns the destinations in order; the last one is the one to select.
    public IReadOnlyList<string> ImportFiles(IEnumerable<string> sources)
    {
        var imported = new List<string>();
        foreach (var src in sources)
        {
            if (Classify(src) == null) continue;
            try
            {
                var dest = UniqueDestination(Path.GetFileName(src));
                File.Copy(src, dest, overwrite: false);
                imported.Add(dest);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        if (imported.Count > 0) Reload();
        return imported;
    }

    /// "name.png", then "name (2).png", "name (3).png"... in the capture folder.
    public string UniqueDestination(string fileName)
    {
        var dir = Directory;
        System.IO.Directory.CreateDirectory(dir);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var dest = Path.Combine(dir, fileName);
        for (var n = 2; File.Exists(dest) || System.IO.Directory.Exists(dest); n++)
            dest = Path.Combine(dir, $"{baseName} ({n}){ext}");
        return dest;
    }

    // Watches entries only (create / delete / rename), like the macOS directory vnode source: content
    // writes are ignored, so a recording being written does not rescan the folder many times a second.
    // An in-place rewrite goes through ReloadFile.
    private void StartWatching()
    {
        lock (_gate)
        {
            if (_disposed) return;
            StopWatchingLocked();
            var dir = Directory;
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                var w = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                };
                w.Created += OnEntry;
                w.Deleted += OnEntry;
                w.Renamed += OnRenamed;
                w.Error += OnError;
                w.EnableRaisingEvents = true;
                _watcher = w;
                _watchedDirectory = dir;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            {
                _watchedDirectory = null;
            }
        }
    }

    private void StopWatchingLocked()
    {
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnEntry;
        _watcher.Deleted -= OnEntry;
        _watcher.Renamed -= OnRenamed;
        _watcher.Error -= OnError;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnEntry(object sender, FileSystemEventArgs e)
    {
        if (Classify(e.FullPath) != null) ScheduleReload();
    }

    // A recorder may write "x.tmp" then rename it to "x.mp4": either side counts.
    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (Classify(e.FullPath) != null || Classify(e.OldFullPath) != null) ScheduleReload();
    }

    // Buffer overflow, or the folder was deleted / moved: recreate it, re-watch and rescan.
    private void OnError(object sender, ErrorEventArgs e)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (_disposed) return;
            StartWatching();
            Reload();
        });
    }

    /// Coalesces a burst of watcher events into one rescan 200 ms after the last one.
    private void ScheduleReload()
    {
        if (_disposed) return;
        try { _debounce.Change(Debounce, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    internal string? WatchedDirectory => _watchedDirectory;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopWatchingLocked();
        }
        _debounce.Dispose();
    }
}
