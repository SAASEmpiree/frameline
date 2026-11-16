using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using FrameLine.Core;
using FrameLine.Editor;
using FrameLine.Library;
using FrameLine.Platform;
using FrameLine.Recording;
using Microsoft.Win32;

namespace FrameLine.Shell;

public sealed partial class AppCoordinator
{
    // MARK: - Pin from clipboard

    /// "Pin Image from Clipboard": the clipboard image is written to a temporary PNG and floats
    /// above everything. No image → a discreet warning.
    private void PinFromClipboardCore()
    {
        var image = ClipboardImage.ReadImage();
        if (image == null)
        {
            GlassConfirmBadge.Show(Tr.L("toast_no_clipboard_image", "No image in clipboard"), "exclamationmark.triangle.fill");
            return;
        }
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"FrameLine-Pin-{Guid.NewGuid().ToString().ToUpperInvariant()}.png");
            using (var fs = File.Create(path)) ClipboardImage.EncodePng(image).CopyTo(fs);
            PinnedShotWindow.Show(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            GlassConfirmBadge.Show(Tr.L("toast_pin_failed", "Pin failed"), "exclamationmark.triangle.fill");
        }
    }

    // MARK: - Actions on one capture (context menu, thumbnail, action bar)

    /// Opens the annotation editor; a recording opens the trim editor (its twin for videos).
    private void AnnotateCore(string path)
    {
        if (IsVideo(path)) VideoTrimmer.Open(path);
        else AnnotationEditor.Open(path);
    }

    /// "Open" from the thumbnail: the main window with this capture selected.
    private void OpenInAppCore(string path)
    {
        CaptureLibrary.Shared.Reload();
        MainLibraryWindow.ShowWindow(path);
    }

    /// File picker to import images / videos into the capture folder. Deferred one dispatcher turn:
    /// opening a modal dialog from inside a menu click handler is fragile.
    private void ImportImagesCore()
    {
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var exts = CaptureLibrary.ImageExtensions.Concat(CaptureLibrary.VideoExtensions).Select(e => "*" + e);
            var dialog = new OpenFileDialog
            {
                Multiselect = true,
                CheckFileExists = true,
                Filter = $"{Tr.L("import_filter", "Images and videos")}|{string.Join(";", exts)}",
            };
            using var owner = new ForegroundOwner();
            if (dialog.ShowDialog(owner.Window) != true) return;
            ImportFiles(dialog.FileNames);
        });
    }

    /// Port of CaptureLibrary.importFiles: copies under a unique name (Core), selects the last one.
    public int ImportFiles(IEnumerable<string> files)
    {
        IReadOnlyList<string> imported;
        try { imported = CaptureLibrary.Shared.ImportFiles(files); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ESLog.Log("import failed: " + e.GetType().Name);
            return 0;
        }
        if (imported.Count > 0) LibraryModel.Shared.SelectOnly(imported[^1]);
        return imported.Count;
    }

    private void OpenCapturesFolderCore()
    {
        var dir = Preferences.Shared.EnsureSaveDirectory();
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    /// Image + file on the clipboard (a video: the file).
    private void CopyImageCore(string path)
    {
        bool ok = IsVideo(path) ? ClipboardImage.SetFile(path) : ClipboardImage.SetImageAndFile(path);
        if (ok) GlassConfirmBadge.Show(Tr.L("toast_copied", "Copié"));
    }

    /// OCR → recognised text on the clipboard, with visible feedback either way.
    private void CopyTextCore(string path) => _ = CopyTextAsync(path);

    private async System.Threading.Tasks.Task CopyTextAsync(string path)
    {
        string text;
        try { text = await TextRecognizer.RecognizeAsync(path); }
        catch (Exception e) { ESLog.Log("ocr failed: " + e.Message); text = ""; }
        bool ok = !string.IsNullOrWhiteSpace(text) && ClipboardImage.SetText(text);
        GlassConfirmBadge.Show(ok ? Tr.L("toast_text_copied", "Texte copié") : Tr.L("toast_no_text", "Aucun texte détecté"),
            ok ? "checkmark.circle.fill" : "text.viewfinder");
    }

    private void RevealInExplorerCore(string path)
    {
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else
            OpenCapturesFolderCore();
    }

    /// Copies the capture to a place the user picks (only the source's file type is offered).
    private void ExportCaptureCore(string path)
    {
        var ext = Path.GetExtension(path);
        var dialog = new SaveFileDialog
        {
            FileName = Path.GetFileName(path),
            DefaultExt = ext,
            AddExtension = true,
            OverwritePrompt = true,          // the user confirms the replacement here
            Filter = $"{ext.TrimStart('.').ToUpperInvariant()}|*{ext}",
        };
        using var owner = new ForegroundOwner();
        if (dialog.ShowDialog(owner.Window) != true) return;
        var dest = dialog.FileName;
        if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return;
        bool ok;
        try { File.Copy(path, dest, overwrite: true); ok = true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ok = false; }
        if (ok) GlassConfirmBadge.Show(Tr.L("toast_saved", "Enregistré"));
        else GlassConfirmBadge.Show(Tr.L("toast_save_failed", "Échec de l'enregistrement"), "exclamationmark.triangle.fill");
    }

    /// "Move to Recycle Bin": reversible (unlike a permanent delete). A capture still being
    /// written is recycled once its write ends (EndPendingWrite).
    private void TrashCore(string path)
    {
        if (_pendingWrites.Contains(path)) { _trashedWhileWriting.Add(path); return; }
        RecycleBin.Send(path);
        CaptureLibrary.Shared.Reload();
    }

    /// Recycles a whole multi-selection at once, one reload at the end, then one toast.
    public void TrashMany(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        int trashed = 0;
        foreach (var p in paths)
        {
            if (_pendingWrites.Contains(p)) { _trashedWhileWriting.Add(p); trashed++; continue; }
            if (RecycleBin.Send(p)) trashed++;
        }
        CaptureLibrary.Shared.Reload();
        if (trashed == 0) return;
        GlassConfirmBadge.Show(trashed > 1
            ? Tr.Format(Tr.L("toast_captures_deleted", "%d captures supprimées"), trashed)
            : Tr.L("toast_capture_deleted", "Capture supprimée"), "trash");
    }

    private static bool IsVideo(string path) =>
        CaptureLibrary.VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
}
