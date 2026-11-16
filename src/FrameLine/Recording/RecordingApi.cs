using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using FrameLine.Capture;
using FrameLine.Core;
using FrameLine.Library;
using ScreenRecorderLib;

namespace FrameLine.Recording;

/// Port of RecordingManager.swift: full-screen recording of the monitor under the cursor to an
/// H.264 MP4 with system audio and/or microphone, cursor visible. Every state field is touched on
/// the UI thread only; public calls from other threads (MCP) are marshalled there.
public sealed class RecordingManager
{
    public static RecordingManager Shared { get; } = new();

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopSafetyNet = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopHardLimit = TimeSpan.FromSeconds(60);

    private Recorder? recorder;
    private int generation;              // identifies the recorder whose callbacks are still welcome
    private string? currentPath;
    private volatile bool recording;
    // Set synchronously before the first await: a second trigger while the start cue plays must not
    // start a second, orphaned recorder that nobody can stop.
    private volatile bool starting;
    // Stop requested during the start cue: honoured when the start completes or aborts.
    private bool cancelStart;
    private bool stopping;                // Stop() in flight: a second press does nothing more
    private bool finalizing;              // routeFinished runs once per recording
    private TaskCompletionSource<string?>? finish;
    private readonly Stopwatch clock = new();

    public bool IsRecording => recording;
    /// Start in progress (cue + recorder warm-up); the MCP start_recording tool waits on it.
    public bool IsStarting => starting;
    public TimeSpan Elapsed => recording ? clock.Elapsed : TimeSpan.Zero;
    /// The .mp4 being written, or null.
    public string? CurrentPath => currentPath;

    /// Raised on the UI thread when recording starts/stops (tray icon, menu text).
    public event Action? StateChanged;
    /// Raised on the UI thread with the final .mp4 path (and the GIF is exported after, if enabled).
    public event Action<string>? Finished;

    public void ToggleFullScreen()
    {
        if (IsRecording || IsStarting) _ = StopAsync();
        else _ = StartAsync();
    }

    /// Plays the start cue, then starts recording. Returns the .mp4 path being written, or null when
    /// the start failed, was cancelled, or a recording is already running.
    public Task<string?> StartAsync() => OnUi(() => StartCoreAsync(null));

    /// Same, for a rectangle in physical virtual-screen pixels (MCP start_recording region mode). The
    /// rectangle is clipped to the monitor that holds its centre.
    public Task<string?> StartRegionAsync(Int32Rect physicalRect) => OnUi(() => StartCoreAsync(physicalRect));

    /// Stops and finalises. Returns the final .mp4 path, or null when nothing was recorded. A stop
    /// during the start cue cancels the start; calling it twice returns the same pending result.
    public Task<string?> StopAsync() => OnUi(StopCore);

    // MARK: start

    private async Task<string?> StartCoreAsync(Int32Rect? region)
    {
        if (recording || starting) return null;
        starting = true;
        cancelStart = false;
        // The thumbnail stack stays off screen for the WHOLE recording, otherwise the cards already
        // shown (and those of captures taken meanwhile) are filmed.
        HoldThumbnails(true);
        finish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var prefs = Preferences.Shared;
        string path;
        try { path = prefs.NewCapturePath("mp4"); }
        catch (Exception ex) { Log("capture path", ex); FailStart(showBadge: true); return null; }
        int fps = prefs.RecordFps;
        bool sys = prefs.RecordSystemAudio, mic = prefs.RecordMicrophone;

        // The cue is played AND finished before capture starts, so it is never in the audio track.
        await PlayStartCueAsync();
        if (cancelStart) { FailStart(showBadge: false); return null; }

        bool ok;
        try
        {
            ok = await TryStartRecorderAsync(path, fps, sys, mic, hardware: true, region);
            if (!ok && !cancelStart)
            {
                // Some GPUs refuse the hardware encoder for odd sizes or while another app holds it.
                ok = await TryStartRecorderAsync(path, fps, sys, mic, hardware: false, region);
            }
            if (!ok && mic && !cancelStart)
            {
                // Microphone blocked in Windows privacy settings (or unplugged mid-start): record without
                // it rather than not at all, like the macOS path when the mic is not authorised.
                ok = await TryStartRecorderAsync(path, fps, sys, microphone: false, hardware: true, region);
            }
        }
        catch (Exception ex)
        {
            // ScreenRecorderLib.dll itself failed to load (missing VC++ x64 runtime, file not next
            // to the exe): the type load surfaces here, not in Build. Without this the start lock
            // stayed taken and no recording was possible until relaunch.
            Log("load recorder", ex);
            ok = false;
        }
        if (!ok) { FailStart(showBadge: !cancelStart); return null; }

        currentPath = path;
        finalizing = false;
        stopping = false;
        recording = true;
        starting = false;
        clock.Restart();
        StateChanged?.Invoke();
        if (cancelStart) { cancelStart = false; _ = StopCore(); }
        return path;
    }

    private static async Task PlayStartCueAsync()
    {
        try
        {
            await CaptureSound.PlayRecordingStartAsync();
            await Task.Delay(60);   // small margin: the tail of the cue must not reach the loopback
        }
        catch (Exception ex) { Log("start cue", ex); }
    }

    /// Creates the recorder and waits until it reports Recording (or fails / times out).
    private async Task<bool> TryStartRecorderAsync(string path, int fps, bool sys, bool microphone, bool hardware, Int32Rect? region)
    {
        Recorder rec;
        int id = ++generation;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var options = RecorderSetup.Build(fps, sys, microphone, hardware, region);
            rec = Recorder.CreateRecorder(options);
        }
        catch (Exception ex)
        {
            // Typically a missing VC++ runtime or Media Foundation (Windows N without the Media Feature Pack).
            Log("create recorder", ex);
            return false;
        }

        var dispatcher = UiDispatcher;
        rec.OnStatusChanged += (_, e) => { if (e.Status == RecorderStatus.Recording) started.TrySetResult(true); };
        rec.OnRecordingFailed += (_, e) =>
        {
            started.TrySetResult(false);
            Post(dispatcher, () => OnRecorderFailed(id, e.Error));
        };
        rec.OnRecordingComplete += (_, e) => Post(dispatcher, () => OnRecorderComplete(id, e.FilePath));
        recorder = rec;

        try { rec.Record(path); }
        catch (Exception ex)
        {
            Log("record", ex);
            started.TrySetResult(false);
        }

        var winner = await Task.WhenAny(started.Task, Task.Delay(StartTimeout));
        bool ok = winner == started.Task && started.Task.Result;
        if (ok) return true;

        // Failed or timed out: this recorder's late callbacks must not touch the next attempt.
        if (generation == id) generation++;
        recorder = null;
        // Wait for the release so the retry can reuse the path and the empty file can be deleted.
        await Task.WhenAny(DisposeRecorder(rec, stopFirst: true), Task.Delay(TimeSpan.FromSeconds(5)));
        DeleteIfEmpty(path);
        return false;
    }

    // MARK: stop

    private Task<string?> StopCore()
    {
        if (starting)
        {
            cancelStart = true;   // the start in progress cancels itself (or stops right after starting)
            return finish?.Task ?? Task.FromResult<string?>(null);
        }
        if (!recording || recorder == null) return Task.FromResult<string?>(null);
        var pending = finish?.Task ?? Task.FromResult<string?>(null);
        if (stopping) return pending;
        stopping = true;

        var rec = recorder;
        int id = generation;
        Task.Run(() =>
        {
            try { rec.Stop(); }
            catch (Exception ex) { Log("stop", ex); }
        });
        _ = StopSafetyNetAsync(id);
        return pending;
    }

    /// If the recorder never reports completion (no frame written, silent error), finalise anyway:
    /// otherwise IsRecording stayed true forever and no new recording was possible. While the file is
    /// still locked by the muxer we keep waiting, so a half-written MP4 is never handed out.
    private async Task StopSafetyNetAsync(int id)
    {
        await Task.Delay(StopSafetyNet);
        var waited = StopSafetyNet;
        while (generation == id && !finalizing && waited < StopHardLimit && IsLocked(currentPath))
        {
            await Task.Delay(500);
            waited += TimeSpan.FromMilliseconds(500);
        }
        if (generation == id && !finalizing) RouteFinished(failed: false);
    }

    // MARK: recorder callbacks (already on the UI thread)

    private void OnRecorderComplete(int id, string? filePath)
    {
        if (id != generation || starting) return;
        if (!string.IsNullOrEmpty(filePath)) currentPath = filePath;
        RouteFinished(failed: false);
    }

    private void OnRecorderFailed(int id, string? error)
    {
        if (id != generation) return;
        Debug.WriteLine($"FrameLine recording failed: {error}");
        if (starting) return;   // the start path reports it
        // Stopped by itself (display unplugged, GPU reset): keep whatever was written.
        RouteFinished(failed: true);
    }

    private void RouteFinished(bool failed)
    {
        if (finalizing) return;
        finalizing = true;
        var path = currentPath;
        var rec = recorder;
        recorder = null;
        generation++;
        currentPath = null;
        recording = false;
        starting = false;
        stopping = false;
        cancelStart = false;
        clock.Reset();
        if (rec != null) _ = DisposeRecorder(rec, stopFirst: false);
        HoldThumbnails(false);   // before Show below, so the new card is visible

        bool ok = path != null && FileLength(path) > 0;
        if (ok)
        {
            CaptureLibrary.Shared.Reload();
            if (Preferences.Shared.ShowPreview) CaptureThumbnail.Show(path!);
            Finished?.Invoke(path!);
        }
        else if (path != null)
        {
            DeleteIfEmpty(path);
        }
        if (failed || !ok) RecBadge.Show(RecStrings.RecordingFailed, RecBadge.GlyphWarning);

        StateChanged?.Invoke();
        var done = finish;
        finish = null;
        done?.TrySetResult(ok ? path : null);

        if (ok && Preferences.Shared.ExportGif) _ = ExportGifAsync(path!);
    }

    private void FailStart(bool showBadge)
    {
        var rec = recorder;
        recorder = null;
        generation++;
        currentPath = null;
        recording = false;
        starting = false;     // releases the start lock: a new attempt is possible
        stopping = false;
        cancelStart = false;
        if (rec != null) _ = DisposeRecorder(rec, stopFirst: true);
        HoldThumbnails(false);
        if (showBadge) RecBadge.Show(RecStrings.RecordingFailed, RecBadge.GlyphWarning);
        StateChanged?.Invoke();
        var done = finish;
        finish = null;
        done?.TrySetResult(null);
    }

    private static async Task ExportGifAsync(string videoPath)
    {
        var prefs = Preferences.Shared;
        var gif = await GifExporter.ExportAsync(videoPath, prefs.GifFps, prefs.GifWidth);
        if (gif != null) CaptureLibrary.Shared.Reload();
        else RecBadge.Show(RecStrings.GifFailed, RecBadge.GlyphWarning);
    }

    // MARK: helpers

    private bool holdingThumbnails;

    /// One Begin/EndCaptureSession pair per recording, whatever path ends it.
    private void HoldThumbnails(bool hold)
    {
        if (hold == holdingThumbnails) return;
        holdingThumbnails = hold;
        try
        {
            if (hold) CaptureThumbnail.BeginCaptureSession();
            else CaptureThumbnail.EndCaptureSession();
        }
        catch (Exception ex) { Log("thumbnail session", ex); }
    }

    /// Dispose may block until the capture threads exit; never on the UI thread, never inside one of
    /// the recorder's own callbacks.
    private static Task DisposeRecorder(Recorder rec, bool stopFirst) =>
        Task.Run(() =>
        {
            try { if (stopFirst) rec.Stop(); } catch { /* already stopped */ }
            try { rec.Dispose(); } catch (Exception ex) { Log("dispose", ex); }
        });

    private static long FileLength(string path)
    {
        try { var fi = new FileInfo(path); return fi.Exists ? fi.Length : 0; }
        catch { return 0; }
    }

    private static void DeleteIfEmpty(string path)
    {
        try { if (File.Exists(path) && new FileInfo(path).Length == 0) File.Delete(path); }
        catch { /* still locked: harmless empty file */ }
    }

    private static bool IsLocked(string? path)
    {
        if (path == null || !File.Exists(path)) return false;
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch { return false; }
    }

    private static Dispatcher? UiDispatcher => Application.Current?.Dispatcher;

    private static void Post(Dispatcher? d, Action a)
    {
        if (d == null) a();
        else d.BeginInvoke(a);
    }

    private static Task<T> OnUi<T>(Func<Task<T>> f)
    {
        var d = UiDispatcher;
        if (d == null || d.CheckAccess()) return f();
        return d.InvokeAsync(f).Task.Unwrap();
    }

    private static void Log(string what, Exception ex) => Debug.WriteLine($"FrameLine recording ({what}): {ex}");
}

public static class VideoTrimmer
{
    /// Opens the trim editor window for a recording (twin of the image editor).
    public static void Open(string videoPath) => VideoTrimmerWindow.Open(videoPath);
}
