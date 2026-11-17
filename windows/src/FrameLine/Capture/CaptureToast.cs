namespace FrameLine.Capture;

/// The capture module's toasts ("Capture in 3s…", "Capture failed") go through the app-wide
/// GlassConfirmBadge, so a toast from another module never stacks on top of this one.
internal static class CaptureToast
{
    public const string Check = "\uE73E", Warning = "\uE7BA", Timer = "\uE916";

    public static void Show(string text, string icon = Check) => Platform.GlassConfirmBadge.ShowGlyph(text, icon);
}
