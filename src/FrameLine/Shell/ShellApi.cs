using System;

namespace FrameLine.Shell;

// OWNER: agent "shell". Contract stub. AppCoordinator is the hub every UI module calls.
public sealed class AppCoordinator
{
    public static AppCoordinator Shared { get; } = new();
    public void Start() { }
    public void CaptureRegion() { }
    public void CaptureWindow() { }
    public void CaptureFull() { }
    public void CaptureFullDelayed(int seconds) { }
    public void ToggleRecording() { }
    public void PinFromClipboard() { }
    public void Annotate(string path) { }
    public void OpenInApp(string path) { }
    public void ImportImages() { }
    public void OpenCapturesFolder() { }
    public void CopyImage(string path) { }
    public void CopyText(string path) { }
    public void PinToScreen(string path) { }
    public void RevealInExplorer(string path) { }
    public void ExportCapture(string path) { }
    public void Trash(string path) { }
    public void ShowLibrary() { }
    public void ToggleHistory() { }
    public void OpenPreferences() { }
    public void Quit() { }
}
