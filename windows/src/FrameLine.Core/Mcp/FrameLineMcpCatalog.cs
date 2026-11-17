using System.Text.Json.Nodes;

namespace FrameLine.Core.Mcp;

/// FrameLine's MCP tool catalog: names, descriptions, input schemas and annotations, identical to
/// macOS except where a sentence described macOS itself (permission, .mov, Apple Vision, points).
/// The handlers live in FrameLineMcpTools. Nothing here deletes anything.
public static class FrameLineMcpCatalog
{
    public const string Instructions =
        "FrameLine is a local Windows screenshot and screen-recording app. Every capture is saved as a " +
        "file in the user's capture folder and appears in FrameLine's library. Screen coordinates are " +
        "in physical pixels, global, with the origin at the top-left corner of the primary display and y " +
        "growing downward (the same space as list_windows bounds); displays to the left of or above the " +
        "primary display have negative coordinates. Paths passed to tools must point inside the " +
        "capture folder (use list_recent_captures). Nothing is uploaded and no tool deletes files.";

    private static JsonObject Schema(JsonObject properties, params string[] required)
    {
        var s = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Length > 0) s["required"] = new JsonArray(required.Select(r => (JsonNode)r!).ToArray());
        return s;
    }

    private static JsonObject PathProperty() => new()
    {
        ["type"] = "string",
        ["description"] = "Absolute path of a capture inside FrameLine's capture folder, or just its file name. Defaults to the most recent matching capture.",
    };

    private static JsonObject Prop(string type, string description, Action<JsonObject>? more = null)
    {
        var p = new JsonObject { ["type"] = type, ["description"] = description };
        more?.Invoke(p);
        return p;
    }

    private static JsonArray Enum(params string[] values) => new(values.Select(v => (JsonNode)v!).ToArray());

    private static McpToolAnnotations ReadOnly(string title) => new(title, true, false, true, false);

    private static McpToolAnnotations Action(string title, bool idempotent) => new(title, false, false, idempotent, false);

    private static JsonObject RegionProps(JsonObject o)
    {
        o["x"] = Prop("number", "region only: left edge in pixels.");
        o["y"] = Prop("number", "region only: top edge in pixels.");
        o["width"] = Prop("number", "region only: width in pixels.", p => p["exclusiveMinimum"] = 0);
        o["height"] = Prop("number", "region only: height in pixels.", p => p["exclusiveMinimum"] = 0);
        return o;
    }

    public static IReadOnlyList<McpTool> Tools { get; } = new[]
    {
        new McpTool(
            "take_screenshot",
            "Take a screenshot",
            "Takes a screenshot with FrameLine and saves it as an image file in the capture folder " +
            "(PNG or JPEG, following FrameLine's settings); it also appears in FrameLine's library. " +
            "Modes: \"full_screen\" captures a whole display (default: the display under the mouse " +
            "pointer, or `display`, 1 = primary display); \"window\" captures one window without user " +
            "interaction, chosen by `window_id` (from list_windows) or `app_name` (that app's " +
            "frontmost window), else the frontmost window of the foreground app; \"region\" " +
            "captures the rectangle x, y, width, height in pixels (top-left origin of the primary " +
            "display). Returns the saved file path and pixel size. Set include_image to also get a " +
            "downscaled PNG preview in the result. Never asks the user to click anything.",
            Schema(RegionProps(new JsonObject
            {
                ["mode"] = Prop("string", "What to capture. Default: full_screen.",
                                p => p["enum"] = Enum("full_screen", "window", "region")),
                ["display"] = Prop("integer", "full_screen only: display index, 1 = primary display.",
                                   p => p["minimum"] = 1),
                ["window_id"] = Prop("integer", "window only: window id (HWND) from list_windows.",
                                     p => p["minimum"] = 1),
                ["app_name"] = Prop("string", "window only: application name (case-insensitive), e.g. \"Notepad\"."),
                ["delay_seconds"] = Prop("integer", "Wait this many seconds before capturing. Default 0.",
                                         p => { p["minimum"] = 0; p["maximum"] = 10; }),
                ["include_image"] = Prop("boolean", "Also return a PNG preview (longest side 1568 px). Default false."),
            })),
            Action("Take a screenshot", idempotent: false)),
        new McpTool(
            "start_recording",
            "Start a screen recording",
            "Starts recording the screen to an .mp4 file in the capture folder (system audio and " +
            "microphone follow FrameLine's settings; a start sound plays). Mode \"full_screen\" " +
            "(default) records the primary display; mode \"region\" records the rectangle x, y, width, " +
            "height in pixels (top-left origin of the primary display). " +
            "Fails if a recording is already running. Returns once recording has actually started, " +
            "with the path the video will be written to. Call stop_recording to finish it.",
            Schema(RegionProps(new JsonObject
            {
                ["mode"] = Prop("string", "What to record. Default: full_screen.",
                                p => p["enum"] = Enum("full_screen", "region")),
            })),
            Action("Start a screen recording", idempotent: false)),
        new McpTool(
            "stop_recording",
            "Stop the screen recording",
            "Stops the screen recording in progress, waits for FrameLine to finish writing the file, " +
            "and returns the path of the saved .mp4 video (also added to FrameLine's library). Fails " +
            "if nothing is being recorded.",
            Schema(new JsonObject()),
            Action("Stop the screen recording", idempotent: false)),
        new McpTool(
            "get_status",
            "FrameLine status",
            "Read-only. Reports whether a screen recording is running (and its file path), whether " +
            "screen capture is allowed, the capture folder, and FrameLine's version.",
            Schema(new JsonObject()),
            ReadOnly("FrameLine status")),
        new McpTool(
            "list_recent_captures",
            "List recent captures",
            "Read-only. Lists the most recent screenshots and recordings in FrameLine's capture " +
            "folder, newest first, with absolute path, file name, kind (image or video), " +
            "modification date (ISO 8601) and size in bytes.",
            Schema(new JsonObject
            {
                ["limit"] = Prop("integer", "Maximum number of captures. Default 20.",
                                 p => { p["minimum"] = 1; p["maximum"] = 200; }),
                ["kind"] = Prop("string", "Filter by kind. Default all.", p => p["enum"] = Enum("all", "image", "video")),
            }),
            ReadOnly("List recent captures")),
        new McpTool(
            "list_windows",
            "List on-screen windows",
            "Read-only. Lists the normal application windows currently on screen, front to back, " +
            "with window_id, app name, pid, title and bounds (pixels, top-left origin of the primary " +
            "display). Use a window_id with take_screenshot mode \"window\".",
            Schema(new JsonObject
            {
                ["app_name"] = Prop("string", "Only windows of this app (case-insensitive)."),
            }),
            ReadOnly("List on-screen windows")),
        new McpTool(
            "ocr_capture",
            "Read the text of a capture",
            "Read-only. Recognizes the text in an image capture with Windows OCR, on device, and " +
            "returns it (one line per recognized line). Does not touch the clipboard. Defaults to " +
            "the most recent image capture.",
            Schema(new JsonObject { ["path"] = PathProperty() }),
            ReadOnly("Read the text of a capture")),
        new McpTool(
            "copy_capture_to_clipboard",
            "Copy a capture to the clipboard",
            "Puts a capture on the Windows clipboard: the image itself for a screenshot, the file for " +
            "a recording. Replaces the current clipboard content. Defaults to the most recent capture.",
            Schema(new JsonObject { ["path"] = PathProperty() }),
            Action("Copy a capture to the clipboard", idempotent: true)),
        new McpTool(
            "open_in_editor",
            "Show a capture to the user",
            "Shows a capture to the user: a screenshot opens in FrameLine's annotation editor, a " +
            "recording opens in FrameLine's window with the video selected (trimmer). Does not " +
            "modify the file. Defaults to the most recent capture.",
            Schema(new JsonObject { ["path"] = PathProperty() }),
            Action("Show a capture to the user", idempotent: false)),
    };
}
