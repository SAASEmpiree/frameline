namespace FrameLine.Recording;

/// The recording and trimmer toasts, through the app-wide GlassConfirmBadge (one toast at a time).
internal static class RecBadge
{
    public const string GlyphCheck = "\uE930";     // CompletedSolid
    public const string GlyphWarning = "\uE7BA";   // Warning

    public static void Show(string text, string glyph = GlyphCheck) => Platform.GlassConfirmBadge.ShowGlyph(text, glyph);
}
