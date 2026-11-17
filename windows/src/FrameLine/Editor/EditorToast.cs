namespace FrameLine.Editor;

/// The editor's toasts ("Copied", "Saved"...), through the app-wide GlassConfirmBadge.
internal static class EditorToast
{
    public enum Kind { Success, Info, Warning }

    public static void Show(string text, Kind kind = Kind.Success) =>
        Platform.GlassConfirmBadge.ShowGlyph(text, kind switch
        {
            Kind.Warning => "\uE7BA",   // Warning
            Kind.Info => "\uE946",      // Info
            _ => "\uE73E",              // CheckMark
        });
}
