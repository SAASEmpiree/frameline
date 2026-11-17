using System;
using System.Collections.Generic;
using System.Globalization;
using FrameLine.Core;

namespace FrameLine.Library;

/// Strings of the library windows. The macOS sources hard-code most of these in French; here
/// every one goes through Loc.L with a "lib_*" key (listed in the module report so the core
/// owner adds them in the 10 languages). Until a key exists, the English text below is shown
/// instead of the raw key.
internal static class LibText
{
    private static readonly Dictionary<string, string> English = new()
    {
        // Quick preview
        ["lib_open"] = "Open",
        ["lib_annotate"] = "Annotate",
        ["lib_copy"] = "Copy",
        ["lib_enhance"] = "Enhance",
        ["lib_pin"] = "Pin",
        ["lib_show_in_explorer"] = "Show in Explorer",
        ["lib_move_to_trash"] = "Move to Recycle Bin",
        ["lib_close"] = "Close",
        ["lib_copied"] = "Copied",
        ["lib_video"] = "Video",
        // Sidebar
        ["lib_captures"] = "Captures",
        ["lib_no_captures"] = "No captures",
        ["lib_sidebar_empty_hint"] = "{0} to capture, or drop images here to import them.",
        ["lib_import_image"] = "Import an image",
        ["lib_open_folder"] = "Open the folder",
        ["lib_refresh"] = "Refresh",
        ["lib_settings"] = "Settings",
        ["lib_trash_none"] = "No capture selected",
        ["lib_trash_one"] = "Delete the selected capture",
        ["lib_trash_many"] = "Delete the {0} selected captures",
        ["lib_delete"] = "Delete",
        ["lib_delete_n"] = "Delete {0}",
        ["lib_delete_tip_one"] = "Move the selected capture to the Recycle Bin",
        ["lib_delete_tip_many"] = "Move the {0} selected captures to the Recycle Bin",
        ["lib_restore_to_screen"] = "Restore to Screen",
        ["lib_pin_to_screen"] = "Pin to Screen",
        ["lib_copy_image"] = "Copy Image",
        ["lib_copy_text"] = "Copy Text",
        ["lib_export"] = "Export…",
        ["lib_move_to_trash_n"] = "Move to Recycle Bin ({0})",
        ["lib_deleted_one"] = "Capture deleted",
        ["lib_deleted_many"] = "{0} captures deleted",
        // Relative time (sidebar, display language)
        ["lib_rel_now"] = "now",
        ["lib_rel_sec"] = "{0} sec. ago",
        ["lib_rel_min"] = "{0} min. ago",
        ["lib_rel_hour"] = "{0} hr. ago",
        ["lib_rel_day"] = "{0} day(s) ago",
        ["lib_rel_week"] = "{0} wk. ago",
        ["lib_rel_month"] = "{0} mo. ago",
        ["lib_rel_year"] = "{0} yr. ago",
        // Main window
        ["lib_toggle_sidebar"] = "Hide/show the sidebar",
        ["lib_minimize"] = "Minimize",
        ["lib_maximize"] = "Maximize",
        ["lib_restore"] = "Restore Down",
        ["lib_no_capture_selected"] = "No capture selected",
        ["lib_detail_empty_sub"] = "Captures appear here automatically.\nCapture a first area to get started.",
        ["lib_detail_unselected_sub"] = "Select a capture in the sidebar,\nor capture a new area.",
        ["lib_pill_window"] = "Window",
        ["lib_pill_fullscreen"] = "Full Screen",
        ["lib_play_pause"] = "Play / Pause",
        ["lib_trim"] = "Trim",
        // History panel (macOS keeps it in English; m_capture_history already exists)
        ["lib_history_empty"] = "No captures yet",
        ["lib_history_empty_sub"] = "Your recent captures will show up here.",
        // Pinned shot
        ["lib_opacity"] = "Opacity",
        ["lib_reset_zoom"] = "Reset Zoom",
    };

    public static string T(string key)
    {
        var s = Loc.L(key);
        if (!string.Equals(s, key, StringComparison.Ordinal)) return s;
        return English.TryGetValue(key, out var en) ? en : key;
    }

    public static string F(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key), args);

    /// Short relative time in the display language ("5 min. ago").
    public static string RelativeTime(DateTime date) => Relative(date, T);

    /// Fixed English relative time, like macOS's Capture History panel (BridgeShot's labels).
    public static string RelativeTimeEnglish(DateTime date) =>
        Relative(date, k => English.TryGetValue(k, out var v) ? v : k);

    private static string Relative(DateTime date, Func<string, string> text)
    {
        var local = date.Kind == DateTimeKind.Utc ? date.ToLocalTime() : date;
        var d = DateTime.Now - local;
        double s = Math.Max(0, d.TotalSeconds);
        string Fmt(string key, double v) => string.Format(CultureInfo.CurrentCulture, text(key), (int)Math.Floor(v));
        if (s < 1) return text("lib_rel_now");
        if (s < 60) return Fmt("lib_rel_sec", s);
        if (s < 3600) return Fmt("lib_rel_min", s / 60);
        if (s < 86400) return Fmt("lib_rel_hour", s / 3600);
        if (s < 7 * 86400) return Fmt("lib_rel_day", s / 86400);
        if (s < 30 * 86400) return Fmt("lib_rel_week", s / (7 * 86400));
        if (s < 365 * 86400) return Fmt("lib_rel_month", s / (30 * 86400));
        return Fmt("lib_rel_year", s / (365 * 86400));
    }

    // Hotkeys are fixed VK codes on Windows (RegisterHotKey), so labels never depend on the layout.
    public static readonly string[] RegionKeys = { "Ctrl", "Shift", "4" };
    public static readonly string[] WindowKeys = { "Ctrl", "Shift", "8" };
    public static readonly string[] FullScreenKeys = { "Ctrl", "Shift", "9" };
    public static string RegionLabel => string.Join("+", RegionKeys);
}
