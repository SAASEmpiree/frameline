namespace FrameLine.Library;

/// The library's toasts ("Copied", "Capture deleted"), through the app-wide GlassConfirmBadge.
internal static class LibToast
{
    public static void Show(string text, string glyph = "\uE73E") => Platform.GlassConfirmBadge.ShowGlyph(text, glyph);
}
