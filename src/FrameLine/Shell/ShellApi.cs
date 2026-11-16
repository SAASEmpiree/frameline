using System;

namespace FrameLine.Shell;

/// Port of AppCoordinator.swift + AppDelegate.swift: the hub every UI module calls (tray menu,
/// hotkeys, thumbnail, library, MCP). All members are UI-thread only unless stated otherwise.
/// Split by concern: AppCoordinator.Startup.cs (launch, quit, reopen, frameline:// URLs),
/// AppCoordinator.Captures.cs (capture routing, pending writes), AppCoordinator.Actions.cs
/// (actions on one capture), AppCoordinator.Windows.cs (settings, onboarding, about).
public sealed partial class AppCoordinator
{
    public static AppCoordinator Shared { get; } = new();

    public void Start() => StartCore();
    public void CaptureRegion() => CaptureCore(Capture.CaptureMode.Region, "doRegion");
    public void CaptureWindow() => CaptureCore(Capture.CaptureMode.Window, "doWindow");
    public void CaptureFull() => CaptureCore(Capture.CaptureMode.FullScreen, "doFull");
    public void CaptureFullDelayed(int seconds) => CaptureFullDelayedCore(seconds);
    public void ToggleRecording() => Recording.RecordingManager.Shared.ToggleFullScreen();
    public void PinFromClipboard() => PinFromClipboardCore();
    public void Annotate(string path) => AnnotateCore(path);
    public void OpenInApp(string path) => OpenInAppCore(path);
    public void ImportImages() => ImportImagesCore();
    public void OpenCapturesFolder() => OpenCapturesFolderCore();
    public void CopyImage(string path) => CopyImageCore(path);
    public void CopyText(string path) => CopyTextCore(path);
    public void PinToScreen(string path) => Library.PinnedShotWindow.Show(path);
    /// "Restore to screen" and "Pin to screen" share the same entry point, like the macOS app.
    public void RestoreToScreen(string path) => Library.PinnedShotWindow.Show(path);
    public void RevealInExplorer(string path) => RevealInExplorerCore(path);
    public void ExportCapture(string path) => ExportCaptureCore(path);
    public void Trash(string path) => TrashCore(path);
    public void ShowLibrary() => Library.MainLibraryWindow.ShowWindow();
    public void ToggleHistory() => Library.CaptureHistoryPanel.Toggle();
    public void OpenPreferences() => SettingsWindow.Present();
    public void Quit() => QuitCore();
}
