using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FrameLine.Capture;
using FrameLine.Core;
using FrameLine.Core.Mcp;
using FrameLine.Library;
using FrameLine.Platform;
using FrameLine.Recording;

namespace FrameLine.Shell;

public sealed partial class AppCoordinator
{
    private StatusBarController? _statusBar;
    private bool _started;
    private bool _quitting;
    private bool _servicesStopped;

    // MARK: - Launch (applicationDidFinishLaunching)

    private void StartCore()
    {
        if (_started) return;
        _started = true;
        ESLog.InstallObservers();
        ESLog.Snapshot("applicationDidFinishLaunching");
        ThemeManager.Apply();                                   // default theme: dark
        try { Preferences.Shared.EnsureSaveDirectory(); } catch (Exception e) { ESLog.Log("ensureSaveDirectory: " + e.Message); }
        Preferences.Shared.Changed += OnPreferenceChanged;

        _statusBar = new StatusBarController();
        WireMenu(_statusBar);
        RegisterHotKeys();
        RegisterWindowShortcuts();

        CaptureLibrary.Shared.Start();                          // first scan + folder watching
        // "System" display language follows a change of the Windows display language live
        // (WM_SETTINGCHANGE "intl"), as macOS re-read the preferred languages on every lookup.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        RegisterDetailEditors();
        RecordingManager.Shared.Finished += path => Notifier.NotifyCapture(path, isVideo: true);
        LaunchAtLogin.RefreshPathIfEnabled();
        UrlScheme.Register();

        // Local MCP server (127.0.0.1 + token): Coucou, Claude Code, Cursor… drive FrameLine.
        McpController.Shared.StartIfEnabled(McpHost.Shared);

        // First launch → setup guide. Otherwise a SILENT start: tray only, no window, no focus
        // stolen. The library opens only on an explicit action. Deferred one dispatcher turn, like
        // the macOS app (window creation during a cold boot-time start is fragile there too).
        if (!Preferences.Shared.OnboardingCompleted)
        {
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                () => OnboardingWindow.Present(onComplete: ShowLibrary));
        }
    }

    /// The library's detail panel hosts the real editors, like the macOS DetailView: the annotation
    /// editor for an image, the trimmer for a video. Both save / release their file on Dispose.
    private static void RegisterDetailEditors()
    {
        MainLibraryWindow.ImageDetailFactory = path => new Editor.MarkupEditorView(path);
        MainLibraryWindow.VideoDetailFactory = path => new VideoTrimmerView(path);
    }

    private static void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category == Microsoft.Win32.UserPreferenceCategory.Locale) I18n.Shared.RefreshSystemLanguage();
    }

    private void OnPreferenceChanged(string property)
    {
        if (property == nameof(Preferences.Theme))
            Application.Current?.Dispatcher.BeginInvoke(ThemeManager.Apply);
    }

    private void WireMenu(StatusBarController bar)
    {
        bar.OnRegion = CaptureRegion;
        bar.OnWindow = CaptureWindow;
        bar.OnFull = CaptureFull;
        bar.OnToggleRecord = ToggleRecording;
        bar.OnPreferences = OpenPreferences;
        bar.OnAbout = ShowAbout;
        bar.OnFullDelayed = CaptureFullDelayed;
        bar.OnPinFromClipboard = PinFromClipboard;
        bar.OnCaptureHistory = ToggleHistory;
        bar.OnAllCaptures = ShowLibrary;                       // "opens the app directly"
        bar.OnSetupGuide = () => OnboardingWindow.Present(onComplete: ShowLibrary);
        bar.OnCheckUpdates = Dialogs.ShowUpToDate;
        bar.OnQuit = Quit;
    }

    private void RegisterHotKeys() =>
        HotKeyManager.Shared.RegisterDefaults(
            region: CaptureRegion,
            window: CaptureWindow,
            full: CaptureFull,
            record: ToggleRecording);

    /// Ctrl+, (Settings) and Ctrl+Q (Quit), shown in the tray menu like ⌘, / ⌘Q, work in every
    /// FrameLine window, as the macOS main menu made them work whenever a window was key.
    private void RegisterWindowShortcuts()
    {
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent,
            new KeyEventHandler((_, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.Control) return;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.OemComma) { e.Handled = true; OpenPreferences(); }
                else if (key == Key.Q) { e.Handled = true; Quit(); }
            }));
    }

    /// "About" opens the About tab of Settings (same as Flow and Units on macOS).
    public void ShowAbout() => SettingsWindow.Present("about");

    // MARK: - Reopen (applicationShouldHandleReopen): a second launch of FrameLine.exe

    internal void HandleReopen()
    {
        ESLog.Snapshot("applicationShouldHandleReopen");
        // Never during a capture overlay: a stray reopen would steal the focus mid-selection.
        if (CaptureManager.Shared.IsBusy) return;
        ShowLibrary();
    }

    /// "open" from a frameline:// URL: the newest capture selected in the library, else the library.
    private void DoOpen()
    {
        var recent = CaptureLibrary.Shared.Items.FirstOrDefault();
        if (recent != null) OpenInApp(recent.Path);
        else ShowLibrary();
    }

    // MARK: - frameline:// URLs

    internal void HandleUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(UrlScheme.Scheme, StringComparison.OrdinalIgnoreCase)) return;
        // [SEC] Any web page or local process can open frameline://. Capture and recording actions
        // need an explicit confirmation, or a third party could film the screen silently through
        // FrameLine. Inert actions (open / settings) stay direct.
        switch (uri.Host.ToLowerInvariant())
        {
            case "region": if (Dialogs.ConfirmUrlTrigger(Tr.L("url_action_region", "la capture d'une région"))) CaptureRegion(); break;
            case "window": if (Dialogs.ConfirmUrlTrigger(Tr.L("url_action_window", "la capture d'une fenêtre"))) CaptureWindow(); break;
            case "full": if (Dialogs.ConfirmUrlTrigger(Tr.L("url_action_full", "une capture plein écran"))) CaptureFull(); break;
            case "record":
                // Stopping is harmless; starting needs the user's consent.
                if (RecordingManager.Shared.IsRecording || Dialogs.ConfirmUrlTrigger(Tr.L("url_action_record", "l'enregistrement de l'écran")))
                    ToggleRecording();
                break;
            case "open": DoOpen(); break;
            case "settings":
            case "preferences": OpenPreferences(); break;
        }
    }

    // MARK: - Quit (applicationShouldTerminate + applicationWillTerminate)

    private void QuitCore() => _ = QuitAsync();

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        // A recording in progress is finalised first, or the MP4 would be left without its index.
        if (RecordingManager.Shared.IsRecording || RecordingManager.Shared.IsStarting)
        {
            try { await Task.WhenAny(RecordingManager.Shared.StopAsync(), Task.Delay(TimeSpan.FromSeconds(5))); }
            catch (Exception e) { ESLog.Log("stop on quit: " + e.Message); }
        }
        StopServices();
        Application.Current?.Shutdown();
    }

    /// Windows logoff / shutdown: same finalisation, bounded by a nested dispatcher frame because
    /// the session-ending handler must return synchronously.
    internal void HandleSessionEnding()
    {
        if (RecordingManager.Shared.IsRecording || RecordingManager.Shared.IsStarting)
        {
            var stop = RecordingManager.Shared.StopAsync();
            var frame = new DispatcherFrame();
            _ = Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5)))
                .ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        StopServices();
    }

    /// Unregister hotkeys, remove the tray icon, stop MCP (removes mcp.json). Idempotent.
    internal void StopServices()
    {
        if (!_started || _servicesStopped) return;   // a forwarding second instance never started any
        _servicesStopped = true;
        try { HotKeyManager.Shared.Dispose(); } catch (Exception e) { ESLog.Log("hotkeys dispose: " + e.Message); }
        try { _statusBar?.Dispose(); } catch (Exception e) { ESLog.Log("tray dispose: " + e.Message); }
        _statusBar = null;
        try { McpController.Shared.Shutdown(); } catch (Exception e) { ESLog.Log("mcp shutdown: " + e.Message); }
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        try { CaptureLibrary.Shared.Dispose(); } catch (Exception e) { ESLog.Log("library dispose: " + e.Message); }
    }
}
