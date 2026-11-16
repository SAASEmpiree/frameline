using System;
using System.Collections.Specialized;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FrameLine.Core;
using Microsoft.Win32;

namespace FrameLine.Recording;

/// Port of VideoTrimmerViewController: the temporal twin of the image editor. Player in the middle
/// (letterboxed on #131313), the same two floating capsules (top = transport + Copy/Done, bottom =
/// trim filmstrip). "Done" writes the excerpt over the original; "Save a Copy…" keeps it.
/// Hostable in a window (VideoTrimmerWindow) or in the library's detail pane.
public partial class VideoTrimmerView : UserControl, IDisposable
{
    private const string GlyphPlay = "\uE768", GlyphPause = "\uE769";
    private const string GlyphVolume = "\uE767", GlyphMute = "\uE74F";

    private readonly string path;
    private readonly VideoTrimExporter exporter = new();
    private readonly DispatcherTimer tick;

    private double duration, inTime, outTime, frameRate = 30;
    private double lastSeek;
    private bool isPlaying, isLooping = true, mediaReady, disposed;
    private int loadToken;

    /// Window host: invoked after a successful Done (closes). Inert when embedded.
    public Action? OnDone { get; set; }
    /// When true, Enter on the focused filmstrip triggers Done (default button in the window).
    public bool DefaultDone { get; set; }
    public bool IsExporting => exporter.IsExporting;
    public string VideoPath => path;

    public VideoTrimmerView(string videoPath)
    {
        path = videoPath;
        InitializeComponent();

        ApplyStrings();

        tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1.0 / 30) };
        tick.Tick += (_, _) => OnTick();

        Player.MediaOpened += (_, _) => { mediaReady = true; ShowFrame(lastSeek); };
        Player.MediaFailed += (_, _) => RecBadge.Show(RecStrings.Unreadable, RecBadge.GlyphWarning);
        Player.MediaEnded += (_, _) => OnMediaEnded();

        WireTrack();
        Loaded += (_, _) =>
        {
            if (disposed) return;
            tick.Start();
            I18n.Shared.LanguageChanged -= OnLanguageChanged;
            I18n.Shared.LanguageChanged += OnLanguageChanged;
            ApplyStrings();   // the language may have changed while detached
        };
        // Stop playback when the host goes away (window closed, other capture selected).
        Unloaded += (_, _) => { tick.Stop(); PausePlayer(); I18n.Shared.LanguageChanged -= OnLanguageChanged; };

        AttachPlayer();
        _ = LoadAssetAsync();
    }

    // MARK: player

    private void AttachPlayer()
    {
        mediaReady = false;
        Player.Source = new Uri(path, UriKind.Absolute);
        // Play+Pause renders the first frame; ScrubbingEnabled keeps the picture in sync with seeks.
        Player.Play();
        Player.Pause();
        isPlaying = false;
        UpdatePlayIcon();
    }

    /// Releases the file (MediaElement keeps it open while a source is set).
    private void DetachPlayer()
    {
        isPlaying = false;
        try { Player.Stop(); Player.Close(); } catch { /* not opened */ }
        Player.Source = null;
        mediaReady = false;
        UpdatePlayIcon();
    }

    private void PausePlayer()
    {
        if (!isPlaying) return;
        lastSeek = Player.Position.TotalSeconds;
        Player.Pause();
        isPlaying = false;
        UpdatePlayIcon();
    }

    private void Seek(double t)
    {
        lastSeek = Math.Clamp(t, 0, Math.Max(0, duration));
        if (mediaReady) Player.Position = TimeSpan.FromSeconds(lastSeek);
        Track.Playhead = lastSeek;
        CurLabel.Text = Fmt(lastSeek);
    }

    private void ShowFrame(double t)
    {
        if (!mediaReady) return;
        Player.Position = TimeSpan.FromSeconds(Math.Clamp(t, 0, Math.Max(0, duration)));
    }

    private double CurrentTime => isPlaying && mediaReady ? Player.Position.TotalSeconds : lastSeek;

    private void OnTick()
    {
        if (!isPlaying || !mediaReady) return;
        double t = Player.Position.TotalSeconds;
        Track.Playhead = t;
        CurLabel.Text = Fmt(t);
        if (t >= outTime - 0.001)
        {
            if (isLooping) { Seek(inTime); }
            else
            {
                Player.Pause();
                isPlaying = false;
                UpdatePlayIcon();
                Seek(outTime);
            }
        }
    }

    /// With the default selection (out = duration) the player reaches the end of the file before the
    /// tick sees t >= out, so the end event is what loops (or stops) there.
    private void OnMediaEnded()
    {
        if (isLooping && isPlaying)
        {
            Seek(inTime);
            Player.Play();
        }
        else
        {
            isPlaying = false;
            Player.Pause();
            lastSeek = Math.Min(duration, outTime);
        }
        UpdatePlayIcon();
    }

    // The language can change while the trimmer sits in the library detail panel.
    private void OnLanguageChanged() =>
        Dispatcher.BeginInvoke(() => { if (!disposed) ApplyStrings(); });

    private void ApplyStrings()
    {
        RestartButton.ToolTip = RecStrings.Restart;
        PlayButton.ToolTip = isPlaying ? RecStrings.Pause : RecStrings.Play;
        MuteButton.ToolTip = Player.IsMuted ? RecStrings.Unmute : RecStrings.Mute;
        LoopButton.ToolTip = RecStrings.Loop;
        MoreButton.ToolTip = RecStrings.More;
        CopyLabel.Text = RecStrings.Copy;
        DoneLabel.Text = RecStrings.Done;
    }

    private void UpdatePlayIcon()
    {
        PlayButton.Content = isPlaying ? GlyphPause : GlyphPlay;
        PlayButton.ToolTip = isPlaying ? RecStrings.Pause : RecStrings.Play;
    }

    // MARK: track wiring

    private void WireTrack()
    {
        Track.EditingBegan = PausePlayer;
        Track.ScrubIn = t => { inTime = t; Track.InTime = t; Seek(t); };
        Track.ScrubOut = t =>
        {
            outTime = t; Track.OutTime = t;
            Seek(Math.Max(inTime, t - 1.0 / frameRate));   // shows the last kept frame
        };
        Track.ScrubPlayhead = Seek;
        Track.TogglePlay = TogglePlay;
        Track.Step = (fwd, coarse) =>
        {
            PausePlayer();
            double step = coarse ? 1.0 : 1.0 / frameRate;
            Seek(Math.Clamp(CurrentTime + (fwd ? step : -step), 0, duration));
        };
        Track.SetIn = () =>
        {
            double t = Math.Clamp(CurrentTime, 0, Math.Max(0, outTime - Track.MinDur));
            inTime = t; Track.InTime = t;
        };
        Track.SetOut = () =>
        {
            double t = Math.Clamp(CurrentTime, Math.Min(duration, inTime + Track.MinDur), duration);
            outTime = t; Track.OutTime = t;
        };
        Track.CommitDone = () => { if (DefaultDone) _ = DoneAsync(); };
    }

    // MARK: loading

    private async Task LoadAssetAsync()
    {
        int token = ++loadToken;
        VideoInfo info;
        try
        {
            info = await Task.Run(async () => (await VideoFrames.OpenAsync(path)).Info);
        }
        catch (Exception)
        {
            if (token == loadToken && !disposed) RecBadge.Show(RecStrings.Unreadable, RecBadge.GlyphWarning);
            return;
        }
        if (token != loadToken || disposed) return;
        double seconds = info.Duration.TotalSeconds;
        if (!(seconds > 0.05) || !double.IsFinite(seconds))
        {
            RecBadge.Show(RecStrings.Unreadable, RecBadge.GlyphWarning);
            return;
        }
        double aspect = info.HasVideo ? Math.Clamp(info.Aspect, 0.2, 2.2) : 1.6;
        // Nominal rate of a screen recording can be tiny (variable frame rate): clamped, otherwise one
        // frame step would be seconds long and the minimum selection longer than the video.
        double fps = info.FrameRate > 0 && double.IsFinite(info.FrameRate) ? Math.Clamp(info.FrameRate, 10, 240) : 30;
        ApplyLoaded(seconds, aspect, fps);
    }

    private void ApplyLoaded(double seconds, double aspect, double fps)
    {
        duration = seconds;
        frameRate = fps;
        inTime = 0;
        outTime = seconds;
        DurLabel.Text = Fmt(seconds);
        CurLabel.Text = Fmt(0);
        Track.AssetPath = path;
        Track.AssetAspect = aspect;
        Track.MinDur = Math.Min(Math.Max(0.30, 10.0 / fps), seconds);   // never longer than the video
        Track.Duration = seconds;
        Track.InTime = 0;
        Track.OutTime = seconds;
        Track.Playhead = 0;
        Track.ReloadFilmstrip();   // forced: after a trim the width bucket is unchanged
        Seek(0);
    }

    // MARK: transport actions

    private void TogglePlay()
    {
        if (duration <= 0 || IsExporting) return;
        if (isPlaying)
        {
            PausePlayer();
            return;
        }
        double ph = CurrentTime;
        if (ph >= outTime - 0.01 || ph < inTime) Seek(inTime);
        Player.Play();
        isPlaying = true;
        UpdatePlayIcon();
    }

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();
    private void Restart_Click(object sender, RoutedEventArgs e) => Seek(inTime);

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        Player.IsMuted = !Player.IsMuted;
        MuteButton.Content = Player.IsMuted ? GlyphMute : GlyphVolume;
        MuteButton.ToolTip = Player.IsMuted ? RecStrings.Unmute : RecStrings.Mute;
    }

    private void Loop_Click(object sender, RoutedEventArgs e) => isLooping = LoopButton.IsChecked == true;

    // MARK: "…" menu

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Bottom, VerticalOffset = 6 };
        menu.Items.Add(MakeItem(RecStrings.SaveCopy, "\uE74E", () => _ = SaveCopyAsync()));
        menu.Items.Add(MakeItem(RecStrings.SaveFrame, "\uE722", () => _ = SaveCurrentFrameAsync()));
        menu.Items.Add(new Separator());
        var reset = MakeItem(RecStrings.ResetSelection, "\uE7A7", ResetSelection);
        reset.IsEnabled = inTime > 0.001 || outTime < duration - 0.001;
        menu.Items.Add(reset);
        menu.IsOpen = true;
    }

    private static MenuItem MakeItem(string header, string glyph, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13 },
        };
        item.Click += (_, _) => action();
        return item;
    }

    private void ResetSelection()
    {
        inTime = 0; outTime = duration;
        Track.InTime = 0; Track.OutTime = duration;
        Seek(0);
    }

    // MARK: export state (UI lock + progress wash)

    private (TimeSpan Start, TimeSpan End)? ValidatedRange()
    {
        double lo = Math.Max(0, inTime), hi = Math.Min(duration, outTime);
        if (hi - lo < 0.1) return null;
        return (TimeSpan.FromSeconds(lo), TimeSpan.FromSeconds(hi));
    }

    private void BeginExportUI()
    {
        PausePlayer();
        Track.ExportProgress = 0;
        Track.IsExporting = true;
        SetControlsEnabled(false);
    }

    private void EndExportUI()
    {
        Track.IsExporting = false;
        SetControlsEnabled(true);
    }

    private void SetControlsEnabled(bool on)
    {
        RestartButton.IsEnabled = PlayButton.IsEnabled = MuteButton.IsEnabled = on;
        LoopButton.IsEnabled = MoreButton.IsEnabled = on;
        CopyButton.IsEnabled = DoneButton.IsEnabled = on;
    }

    private IProgress<double> ExportProgress() => new Progress<double>(p => Track.ExportProgress = p);

    // MARK: Done: writes the excerpt over the original

    private void Done_Click(object sender, RoutedEventArgs e) => _ = DoneAsync();

    private async Task DoneAsync()
    {
        if (exporter.IsExporting || disposed) return;
        if (ValidatedRange() is not { } range) { RecBadge.Show(RecStrings.TooShort, RecBadge.GlyphWarning); return; }

        // Release the file before replacing it (MediaElement and the thumbnailer hold it open).
        DetachPlayer();
        Track.CancelFilmstrip();
        var dir = Path.GetDirectoryName(path)!;
        // Same folder (same volume: atomic replace), under an extension the library ignores.
        var tmp = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + ".tmp");
        BeginExportUI();
        try
        {
            await exporter.ExportAsync(path, range.Start, range.End, tmp, ExportProgress());
        }
        catch (Exception)
        {
            if (disposed) return;
            EndExportUI();
            AttachPlayer();
            Seek(inTime);
            RecBadge.Show(RecStrings.ExportFailed, RecBadge.GlyphWarning);
            return;
        }
        if (disposed) { TryDelete(tmp); return; }
        EndExportUI();

        if (!await ReplaceWithRetryAsync(tmp, path))
        {
            TryDelete(tmp);
            AttachPlayer();
            Seek(inTime);
            RecBadge.Show(RecStrings.SaveFailed, RecBadge.GlyphWarning);
            return;
        }
        CaptureLibrary.Shared.ReloadFile(path);   // same name, new content: thumbnails must refresh
        ReloadAfterTrim();
        RecBadge.Show(RecStrings.Saved, RecBadge.GlyphCheck);
        OnDone?.Invoke();
    }

    /// File.Replace is atomic on one volume. WinRT media objects release their handle on finalisation,
    /// so a sharing violation gets a collection and a few retries before giving up.
    private static async Task<bool> ReplaceWithRetryAsync(string tmp, string dest)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                File.Replace(tmp, dest, null, ignoreMetadataErrors: true);
                return true;
            }
            catch (IOException)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(200);
            }
            catch (UnauthorizedAccessException) { return false; }
        }
        return false;
    }

    /// After a successful in-place trim the file is the excerpt: reload duration and filmstrip.
    private void ReloadAfterTrim()
    {
        AttachPlayer();
        _ = LoadAssetAsync();
    }

    // MARK: Save a Copy…

    private async Task SaveCopyAsync()
    {
        if (exporter.IsExporting) return;
        if (ValidatedRange() is not { } range) { RecBadge.Show(RecStrings.TooShort, RecBadge.GlyphWarning); return; }

        var dlg = new SaveFileDialog
        {
            Filter = "MPEG-4 (*.mp4)|*.mp4",
            DefaultExt = ".mp4",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Path.GetDirectoryName(path),
            FileName = Path.GetFileNameWithoutExtension(path) + " " + RecStrings.ExcerptSuffix + ".mp4",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var dest = dlg.FileName;
        if (exporter.IsExporting || disposed) return;
        if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            await DoneAsync();   // overwriting the source is exactly Done
            return;
        }
        TryDelete(dest);   // the user confirmed the overwrite
        BeginExportUI();
        try
        {
            await exporter.ExportAsync(path, range.Start, range.End, dest, ExportProgress());
        }
        catch (Exception)
        {
            if (!disposed) { EndExportUI(); RecBadge.Show(RecStrings.ExportFailed, RecBadge.GlyphWarning); }
            return;
        }
        if (disposed) return;
        EndExportUI();
        var saveDir = Preferences.Shared.SaveDirectory;
        if (!string.IsNullOrEmpty(saveDir) && string.Equals(
                Path.GetFullPath(Path.GetDirectoryName(dest)!).TrimEnd('\\'),
                Path.GetFullPath(saveDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            CaptureLibrary.Shared.Reload();   // copy inside the captures folder shows up in the library
        RecBadge.Show(RecStrings.Saved, RecBadge.GlyphCheck);
    }

    // MARK: Copy: the excerpt FILE on the clipboard

    private void Copy_Click(object sender, RoutedEventArgs e) => _ = CopyAsync();

    private async Task CopyAsync()
    {
        if (exporter.IsExporting || disposed) return;
        if (ValidatedRange() is not { } range) { RecBadge.Show(RecStrings.TooShort, RecBadge.GlyphWarning); return; }
        // Outside the watched folder, otherwise the library would ingest it as a capture.
        var tmp = Path.Combine(Path.GetTempPath(), "FrameLine-" + Guid.NewGuid().ToString("N") + ".mp4");
        BeginExportUI();
        try
        {
            await exporter.ExportAsync(path, range.Start, range.End, tmp, ExportProgress());
        }
        catch (Exception)
        {
            if (!disposed) { EndExportUI(); RecBadge.Show(RecStrings.ExportFailed, RecBadge.GlyphWarning); }
            return;
        }
        if (disposed) return;
        EndExportUI();
        try
        {
            Clipboard.SetFileDropList(new StringCollection { tmp });
            RecBadge.Show(RecStrings.Copied, RecBadge.GlyphCheck);
        }
        catch (Exception)
        {
            RecBadge.Show(RecStrings.ExportFailed, RecBadge.GlyphWarning);
        }
    }

    // MARK: Save Current Frame (PNG of the playhead)

    private async Task SaveCurrentFrameAsync()
    {
        double at = CurrentTime;
        byte[]? png = null;
        try
        {
            png = await Task.Run(async () =>
            {
                var (comp, info) = await VideoFrames.OpenAsync(path);
                if (!info.HasVideo) return null;
                var frame = await VideoFrames.ThumbnailAsync(comp, TimeSpan.FromSeconds(at), info.Width, info.Height);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(VideoFrames.ToBitmapSource(frame)));
                using var ms = new MemoryStream();
                enc.Save(ms);
                return ms.ToArray();
            });
        }
        catch (Exception) { png = null; }
        if (disposed) return;
        if (png == null) { RecBadge.Show(RecStrings.FrameUnavailable, RecBadge.GlyphWarning); return; }

        var dlg = new SaveFileDialog
        {
            Filter = "PNG (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Path.GetDirectoryName(path),
            FileName = Path.GetFileNameWithoutExtension(path) + " " + RecStrings.FrameSuffix + ".png",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            await File.WriteAllBytesAsync(dlg.FileName, png);
            RecBadge.Show(RecStrings.Saved, RecBadge.GlyphCheck);
        }
        catch (Exception) { RecBadge.Show(RecStrings.SaveFailed, RecBadge.GlyphWarning); }
    }

    // MARK: helpers

    /// "m:ss,cc" with the French comma, like the macOS editor.
    private static string Fmt(double s)
    {
        double total = Math.Max(0, double.IsFinite(s) ? s : 0);
        int whole = (int)Math.Floor(total);
        int cc = (int)((total - whole) * 100);
        return $"{whole / 60}:{whole % 60:00},{cc:00}";
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        I18n.Shared.LanguageChanged -= OnLanguageChanged;
        loadToken++;
        tick.Stop();
        exporter.Cancel();
        Track.CancelFilmstrip();
        DetachPlayer();
    }
}
