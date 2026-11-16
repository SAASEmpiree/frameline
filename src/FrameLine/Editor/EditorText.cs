using FrameLine.Core;

namespace FrameLine.Editor;

/// Editor strings. The macOS editor hard-coded them; on Windows they go through Loc with
/// "ed_*" keys, and the English text below is used until the key exists in the Strings table.
internal static class EditorText
{
    public static string T(string key, string english)
    {
        string s;
        try { s = Loc.L(key); }
        catch { s = key; }
        return string.IsNullOrEmpty(s) || s == key ? english : s;
    }

    public static string WindowTitle(string fileName) => T("ed_window_title", "Preview — %@").Replace("%@", fileName);
    public static string ToolSelect => T("ed_tool_select", "Selection");
    public static string ToolRectangle => T("ed_tool_rectangle", "Rectangle");
    public static string ToolArrow => T("ed_tool_arrow", "Arrow");
    public static string ToolPencil => T("ed_tool_pencil", "Pen");
    public static string ToolText => T("ed_tool_text", "Text");
    public static string ToolPixelate => T("ed_tool_pixelate", "Pixelate");
    public static string ToolCrop => T("ed_tool_crop", "Crop");
    public static string Color => T("ed_color", "Color");
    public static string Enhance => T("ed_enhance", "Enhance (AI)");
    public static string Undo => T("ed_undo", "Undo");
    public static string Redo => T("ed_redo", "Redo");
    public static string MagicEraser => T("ed_magic_eraser", "Magic eraser");
    public static string More => T("ed_more", "More");
    public static string Copy => T("ed_copy", "Copy");
    public static string Done => T("ed_done", "Done");
    public static string ZoomOut => T("ed_zoom_out", "Zoom out");
    public static string ZoomIn => T("ed_zoom_in", "Zoom in");
    public static string Fit => T("ed_fit", "Fit");
    public static string FitTip => T("ed_fit_tip", "Fit to window");
    public static string ActualSizeTip => T("ed_actual_size_tip", "Actual size");
    public static string OcrCopy => T("ed_ocr_copy", "Copy text (OCR)");
    public static string SaveCopy => T("ed_save_copy", "Save a copy…");
    public static string Pin => T("ed_pin", "Pin to screen");
    public static string ClearAnnotations => T("ed_clear_annotations", "Clear annotations");
    public static string TextCopied => T("ed_text_copied", "Text copied");
    public static string NoText => T("ed_no_text", "No text detected");
    public static string Saved => T("ed_saved", "Saved");
    public static string SaveFailed => T("ed_save_failed", "Save failed");
    public static string Copied => T("ed_copied", "Copied");
    public static string EnhanceSoon => T("ed_enhance_soon", "Enhance — coming soon");
    public static string EraserSoon => T("ed_eraser_soon", "Magic eraser — coming soon");
    public static string CustomColor => T("ed_custom_color", "Custom…");
    public static string CopyFileName(string baseName) => T("ed_copy_file_name", "%@ copy").Replace("%@", baseName);
    public static string PngFilter => T("ed_png_filter", "PNG image");
}
