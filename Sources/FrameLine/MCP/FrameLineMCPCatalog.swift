import Foundation

// MARK: - FrameLine's MCP tool catalog (Foundation only, testable)
//
// Tool names, descriptions, input schemas and annotations, plus the pure helpers the tools
// share (capture-folder listing and path containment). The app-side handlers live in
// FrameLineMCPTools.swift. Nothing here deletes anything.

enum FrameLineMCPCatalog {
    static let instructions = """
    FrameLine is a local macOS screenshot and screen-recording app. Every capture is saved as a \
    file in the user's capture folder and appears in FrameLine's library. Screen coordinates are \
    in points, global, with the origin at the top-left corner of the main display and y growing \
    downward (the same space as list_windows bounds). Capturing needs the macOS Screen Recording \
    permission; when it is missing, tools return an error asking the user to grant it in System \
    Settings > Privacy & Security > Screen Recording. Paths passed to tools must point inside the \
    capture folder (use list_recent_captures). Nothing is uploaded and no tool deletes files.
    """

    private static func schema(_ properties: [String: Any], required: [String] = []) -> [String: Any] {
        var s: [String: Any] = ["type": "object", "properties": properties, "additionalProperties": false]
        if !required.isEmpty { s["required"] = required }
        return s
    }

    private static let pathProperty: [String: Any] = [
        "type": "string",
        "description": "Absolute path of a capture inside FrameLine's capture folder, or just its file name. Defaults to the most recent matching capture.",
    ]

    private static func readOnly(_ title: String) -> MCPToolAnnotations {
        MCPToolAnnotations(title: title, readOnlyHint: true, destructiveHint: false,
                           idempotentHint: true, openWorldHint: false)
    }

    private static func action(_ title: String, idempotent: Bool) -> MCPToolAnnotations {
        MCPToolAnnotations(title: title, readOnlyHint: false, destructiveHint: false,
                           idempotentHint: idempotent, openWorldHint: false)
    }

    static let tools: [MCPTool] = [
        MCPTool(
            name: "take_screenshot",
            title: "Take a screenshot",
            description: """
            Takes a screenshot with FrameLine and saves it as an image file in the capture folder \
            (PNG or JPEG, following FrameLine's settings); it also appears in FrameLine's library. \
            Modes: "full_screen" captures a whole display (default: the display under the mouse \
            pointer, or `display`, 1 = main display); "window" captures one window without user \
            interaction, chosen by `window_id` (from list_windows) or `app_name` (that app's \
            frontmost window), else the frontmost window of the frontmost other app; "region" \
            captures the rectangle x, y, width, height in points (top-left origin of the main \
            display). Returns the saved file path and pixel size. Set include_image to also get a \
            downscaled PNG preview in the result. Never asks the user to click anything.
            """,
            inputSchema: schema([
                "mode": ["type": "string", "enum": ["full_screen", "window", "region"],
                         "description": "What to capture. Default: full_screen."],
                "display": ["type": "integer", "minimum": 1,
                            "description": "full_screen only: display index, 1 = main display."],
                "window_id": ["type": "integer", "minimum": 1,
                              "description": "window only: CoreGraphics window id from list_windows."],
                "app_name": ["type": "string",
                             "description": "window only: application name (case-insensitive), e.g. \"Safari\"."],
                "x": ["type": "number", "description": "region only: left edge in points."],
                "y": ["type": "number", "description": "region only: top edge in points."],
                "width": ["type": "number", "exclusiveMinimum": 0, "description": "region only: width in points."],
                "height": ["type": "number", "exclusiveMinimum": 0, "description": "region only: height in points."],
                "delay_seconds": ["type": "integer", "minimum": 0, "maximum": 10,
                                  "description": "Wait this many seconds before capturing. Default 0."],
                "include_image": ["type": "boolean",
                                  "description": "Also return a PNG preview (longest side 1568 px). Default false."],
            ]),
            annotations: action("Take a screenshot", idempotent: false)),
        MCPTool(
            name: "start_recording",
            title: "Start a screen recording",
            description: """
            Starts recording the screen to a .mov file in the capture folder (system audio and \
            microphone follow FrameLine's settings; a start sound plays). Mode "full_screen" \
            (default) records the main display; mode "region" records the rectangle x, y, width, \
            height in points (top-left origin of the main display) and needs macOS 15 or later. \
            Fails if a recording is already running. Returns once recording has actually started, \
            with the path the video will be written to. Call stop_recording to finish it.
            """,
            inputSchema: schema([
                "mode": ["type": "string", "enum": ["full_screen", "region"],
                         "description": "What to record. Default: full_screen."],
                "x": ["type": "number", "description": "region only: left edge in points."],
                "y": ["type": "number", "description": "region only: top edge in points."],
                "width": ["type": "number", "exclusiveMinimum": 0, "description": "region only: width in points."],
                "height": ["type": "number", "exclusiveMinimum": 0, "description": "region only: height in points."],
            ]),
            annotations: action("Start a screen recording", idempotent: false)),
        MCPTool(
            name: "stop_recording",
            title: "Stop the screen recording",
            description: """
            Stops the screen recording in progress, waits for FrameLine to finish writing the file, \
            and returns the path of the saved .mov video (also added to FrameLine's library). Fails \
            if nothing is being recorded.
            """,
            inputSchema: schema([:]),
            annotations: action("Stop the screen recording", idempotent: false)),
        MCPTool(
            name: "get_status",
            title: "FrameLine status",
            description: """
            Read-only. Reports whether a screen recording is running (and its file path), whether \
            the Screen Recording permission is granted, the capture folder, and FrameLine's version.
            """,
            inputSchema: schema([:]),
            annotations: readOnly("FrameLine status")),
        MCPTool(
            name: "list_recent_captures",
            title: "List recent captures",
            description: """
            Read-only. Lists the most recent screenshots and recordings in FrameLine's capture \
            folder, newest first, with absolute path, file name, kind (image or video), \
            modification date (ISO 8601) and size in bytes.
            """,
            inputSchema: schema([
                "limit": ["type": "integer", "minimum": 1, "maximum": 200,
                          "description": "Maximum number of captures. Default 20."],
                "kind": ["type": "string", "enum": ["all", "image", "video"],
                         "description": "Filter by kind. Default all."],
            ]),
            annotations: readOnly("List recent captures")),
        MCPTool(
            name: "list_windows",
            title: "List on-screen windows",
            description: """
            Read-only. Lists the normal application windows currently on screen, front to back, \
            with window_id, app name, pid, title and bounds (points, top-left origin of the main \
            display). Use a window_id with take_screenshot mode "window". Titles may be empty when \
            the Screen Recording permission is not granted.
            """,
            inputSchema: schema([
                "app_name": ["type": "string", "description": "Only windows of this app (case-insensitive)."],
            ]),
            annotations: readOnly("List on-screen windows")),
        MCPTool(
            name: "ocr_capture",
            title: "Read the text of a capture",
            description: """
            Read-only. Recognizes the text in an image capture with Apple Vision, on device, and \
            returns it (one line per recognized line). Does not touch the clipboard. Defaults to \
            the most recent image capture.
            """,
            inputSchema: schema(["path": pathProperty]),
            annotations: readOnly("Read the text of a capture")),
        MCPTool(
            name: "copy_capture_to_clipboard",
            title: "Copy a capture to the clipboard",
            description: """
            Puts a capture on the macOS clipboard: the image itself for a screenshot, the file for \
            a recording. Replaces the current clipboard content. Defaults to the most recent capture.
            """,
            inputSchema: schema(["path": pathProperty]),
            annotations: action("Copy a capture to the clipboard", idempotent: true)),
        MCPTool(
            name: "open_in_editor",
            title: "Show a capture to the user",
            description: """
            Shows a capture to the user: a screenshot opens in FrameLine's annotation editor, a \
            recording opens in FrameLine's window with the video selected (trimmer). Does not \
            modify the file. Defaults to the most recent capture.
            """,
            inputSchema: schema(["path": pathProperty]),
            annotations: action("Show a capture to the user", idempotent: false)),
    ]
}

// MARK: - Capture folder helpers

enum CaptureFolder {
    static let imageExtensions: Set<String> = ["png", "jpg", "jpeg", "tiff", "gif", "heic", "bmp"]
    static let videoExtensions: Set<String> = ["mov", "mp4", "m4v"]

    enum Kind: String { case all, image, video }

    struct Item {
        let url: URL
        let modified: Date
        let size: Int
        let isVideo: Bool

        var json: [String: Any] {
            let fmt = ISO8601DateFormatter()
            fmt.formatOptions = [.withInternetDateTime]
            return [
                "path": url.path,
                "name": url.lastPathComponent,
                "kind": isVideo ? "video" : "image",
                "modified": fmt.string(from: modified),
                "size_bytes": size,
            ]
        }
    }

    static func kind(of url: URL) -> Kind? {
        let ext = url.pathExtension.lowercased()
        if imageExtensions.contains(ext) { return .image }
        if videoExtensions.contains(ext) { return .video }
        return nil
    }

    /// Captures in `directory` (non recursive, hidden files skipped), newest first.
    static func list(directory: URL, kind: Kind = .all, limit: Int = 20) -> [Item] {
        let keys: [URLResourceKey] = [.contentModificationDateKey, .fileSizeKey, .isRegularFileKey]
        let urls = (try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: keys,
                                                                 options: [.skipsHiddenFiles])) ?? []
        let items: [Item] = urls.compactMap { url in
            guard let k = Self.kind(of: url), kind == .all || kind == k,
                  let values = try? url.resourceValues(forKeys: Set(keys)), values.isRegularFile == true
            else { return nil }
            return Item(url: url, modified: values.contentModificationDate ?? .distantPast,
                        size: values.fileSize ?? 0, isVideo: k == .video)
        }
        return Array(items.sorted { $0.modified > $1.modified }.prefix(max(0, limit)))
    }

    /// Resolves a user-supplied path (absolute, or a bare file name) to an existing capture file
    /// INSIDE `directory`. Symlinks and `..` are resolved before the containment check, so nothing
    /// outside the capture folder can be reached. nil when outside, missing or not a capture.
    static func resolve(_ raw: String, in directory: URL) -> URL? {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, !trimmed.contains("\0") else { return nil }
        let expanded = (trimmed as NSString).expandingTildeInPath
        let candidate = expanded.hasPrefix("/")
            ? URL(fileURLWithPath: expanded)
            : directory.appendingPathComponent(expanded)
        let file = candidate.standardizedFileURL.resolvingSymlinksInPath()
        let dir = directory.standardizedFileURL.resolvingSymlinksInPath()
        let fileParts = file.pathComponents, dirParts = dir.pathComponents
        guard fileParts.count == dirParts.count + 1,                  // directly in the folder, like the library
              Array(fileParts.prefix(dirParts.count)) == dirParts,
              kind(of: file) != nil else { return nil }
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: file.path, isDirectory: &isDir), !isDir.boolValue else { return nil }
        return file
    }
}
