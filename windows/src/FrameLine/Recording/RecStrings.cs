using FrameLine.Core;

namespace FrameLine.Recording;

/// The macOS recording and trim UI used hard-coded literals; these keys are new on Windows. Until
/// the Strings table carries them, Loc.L returns the key itself, so fall back to the English text
/// rather than showing "trim_done" to the user.
internal static class RecStrings
{
    public static string T(string key, string english)
    {
        var s = Loc.L(key);
        return string.IsNullOrEmpty(s) || s == key ? english : s;
    }

    public static string RecordingFailed => T("rec_failed", "Recording failed");
    public static string GifFailed => T("rec_gif_failed", "GIF export failed");
    public static string Unreadable => T("trim_unreadable", "Unreadable video");
    public static string TooShort => T("trim_too_short", "Selection too short");
    public static string Saved => T("trim_saved", "Saved");
    public static string ExportFailed => T("trim_export_failed", "Export failed");
    public static string SaveFailed => T("trim_save_failed", "Save failed");
    public static string Copied => T("trim_copied", "Copied");
    public static string FrameUnavailable => T("trim_frame_unavailable", "Frame unavailable");
    public static string Restart => T("trim_restart", "Back to start");
    public static string Play => T("trim_play", "Play");
    public static string Pause => T("trim_pause", "Pause");
    public static string Mute => T("trim_mute", "Mute");
    public static string Unmute => T("trim_unmute", "Unmute");
    public static string Loop => T("trim_loop", "Loop playback");
    public static string More => T("trim_more", "More");
    public static string Copy => T("trim_copy", "Copy");
    public static string Done => T("trim_done", "Done");
    public static string SaveCopy => T("trim_save_copy", "Save a Copy…");
    public static string SaveFrame => T("trim_save_frame", "Save Current Frame");
    public static string ResetSelection => T("trim_reset_selection", "Reset Selection");
    public static string ExcerptSuffix => T("trim_excerpt_suffix", "excerpt");
    public static string FrameSuffix => T("trim_frame_suffix", "frame");
    public static string WindowTitle(string fileName) => T("trim_window_title", "Preview — %@").Replace("%@", fileName);
}
