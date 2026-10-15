import AppKit
import AVFoundation
import ImageIO
import UniformTypeIdentifiers

/// The app side of the MCP tools: each handler drives an EXISTING FrameLine feature
/// (CaptureManager / RecordingManager / AppCoordinator / TextRecognizer). Schemas and
/// descriptions live in FrameLineMCPCatalog. Nothing here deletes a file.
final class FrameLineMCPTools: MCPToolProvider {
    var tools: [MCPTool] { FrameLineMCPCatalog.tools }

    func call(_ name: String, arguments: [String: Any]) async -> MCPToolResult {
        switch name {
        case "take_screenshot": return await takeScreenshot(arguments)
        case "start_recording": return await startRecording(arguments)
        case "stop_recording": return await stopRecording()
        case "get_status": return await status()
        case "list_recent_captures": return await listRecent(arguments)
        case "list_windows": return listWindows(arguments)
        case "ocr_capture": return await ocr(arguments)
        case "copy_capture_to_clipboard": return await copyToClipboard(arguments)
        case "open_in_editor": return await openInEditor(arguments)
        default: return .error("Unknown tool: \(name)")
        }
    }

    // MARK: - Shared helpers

    struct ToolFailure: Error {
        let message: String
        init(_ message: String) { self.message = message }
    }

    static let permissionMessage = """
    FrameLine does not have the macOS Screen Recording permission, so it cannot capture the screen. \
    Ask the user to enable FrameLine in System Settings > Privacy & Security > Screen Recording, then \
    relaunch FrameLine and try again.
    """

    /// nil when granted. Otherwise starts FrameLine's own permission flow (system prompt the first
    /// time, then its "Ouvrir les Réglages" alert at most once per session) WITHOUT waiting for it,
    /// and returns the error for the client.
    @MainActor private func requireScreenPermission() -> MCPToolResult? {
        if Permissions.hasScreenRecording() { return nil }
        DispatchQueue.main.async { _ = CaptureManager.shared.ensureScreenRecordingPermission() }
        return .error(Self.permissionMessage)
    }

    @MainActor private var captureFolder: URL { Preferences.shared.saveDirectory }

    /// `path` argument → file inside the capture folder, or the newest capture of `kind`.
    @MainActor private func resolveCapture(_ args: [String: Any], kind: CaptureFolder.Kind) -> Result<URL, ToolFailure> {
        let dir = captureFolder
        if let raw = args["path"] {
            guard let path = raw as? String else { return .failure(ToolFailure("`path` must be a string.")) }
            guard let url = CaptureFolder.resolve(path, in: dir) else {
                return .failure(ToolFailure("No capture at \(path) inside FrameLine's capture folder (\(dir.path)). Use list_recent_captures to get valid paths."))
            }
            if kind != .all, CaptureFolder.kind(of: url) != kind {
                return .failure(ToolFailure("\(url.lastPathComponent) is not an \(kind.rawValue) capture."))
            }
            return .success(url)
        }
        guard let newest = CaptureFolder.list(directory: dir, kind: kind, limit: 1).first else {
            return .failure(ToolFailure("There is no \(kind == .all ? "" : kind.rawValue + " ")capture in \(dir.path) yet."))
        }
        return .success(newest.url)
    }

    /// x, y, width, height (points, top-left origin of the main display).
    private func region(_ args: [String: Any]) -> Result<CGRect, ToolFailure> {
        guard let x = MCPJSON.double(args["x"]), let y = MCPJSON.double(args["y"]),
              let w = MCPJSON.double(args["width"]), let h = MCPJSON.double(args["height"]) else {
            return .failure(ToolFailure("Mode \"region\" needs numeric x, y, width and height (points, top-left origin of the main display)."))
        }
        guard w >= 1, h >= 1 else { return .failure(ToolFailure("width and height must be at least 1 point.")) }
        return .success(CGRect(x: x, y: y, width: w, height: h))
    }

    private func pixelSize(of url: URL) -> (Int, Int)? {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil),
              let props = CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any],
              let w = MCPJSON.int(props[kCGImagePropertyPixelWidth]),
              let h = MCPJSON.int(props[kCGImagePropertyPixelHeight]) else { return nil }
        return (w, h)
    }

    /// Downscaled PNG (longest side ≤ 1568 px) as an MCP image content block.
    private func previewImageContent(_ url: URL) -> [String: Any]? {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        let opts: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: 1568,
        ]
        guard let cg = CGImageSourceCreateThumbnailAtIndex(src, 0, opts as CFDictionary) else { return nil }
        let data = NSMutableData()
        guard let dest = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(dest, cg, nil)
        guard CGImageDestinationFinalize(dest) else { return nil }
        return ["type": "image", "data": (data as Data).base64EncodedString(), "mimeType": "image/png"]
    }

    // MARK: - Windows

    struct WindowInfo {
        let id: Int
        let app: String
        let pid: Int
        let title: String
        let bounds: CGRect

        var json: [String: Any] {
            ["window_id": id, "app": app, "pid": pid, "title": title,
             "bounds": ["x": bounds.minX, "y": bounds.minY, "width": bounds.width, "height": bounds.height]]
        }
    }

    /// Normal (layer 0) visible windows, front to back, FrameLine's own excluded.
    static func onScreenWindows() -> [WindowInfo] {
        let opts: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
        guard let list = CGWindowListCopyWindowInfo(opts, kCGNullWindowID) as? [[String: Any]] else { return [] }
        let me = Int(ProcessInfo.processInfo.processIdentifier)
        return list.compactMap { w in
            guard MCPJSON.int(w[kCGWindowLayer as String]) == 0,
                  let id = MCPJSON.int(w[kCGWindowNumber as String]),
                  let pid = MCPJSON.int(w[kCGWindowOwnerPID as String]), pid != me,
                  (MCPJSON.double(w[kCGWindowAlpha as String]) ?? 1) > 0,
                  let bd = w[kCGWindowBounds as String] as? NSDictionary,
                  let rect = CGRect(dictionaryRepresentation: bd),
                  rect.width >= 40, rect.height >= 40 else { return nil }
            return WindowInfo(id: id, app: w[kCGWindowOwnerName as String] as? String ?? "",
                              pid: pid, title: w[kCGWindowName as String] as? String ?? "", bounds: rect)
        }
    }

    private func listWindows(_ args: [String: Any]) -> MCPToolResult {
        var windows = Self.onScreenWindows()
        if let app = args["app_name"] as? String, !app.isEmpty {
            windows = windows.filter { $0.app.caseInsensitiveCompare(app) == .orderedSame }
        }
        return .object(["count": windows.count, "windows": windows.prefix(100).map(\.json)])
    }

    // MARK: - take_screenshot

    @MainActor private func takeScreenshot(_ args: [String: Any]) async -> MCPToolResult {
        if let denied = requireScreenPermission() { return denied }
        let mode = args["mode"] as? String ?? "full_screen"
        var modeArgs: [String] = []
        var detail: [String: Any] = ["mode": mode]

        switch mode {
        case "full_screen":
            var count: UInt32 = 0
            CGGetActiveDisplayList(0, nil, &count)
            if let display = MCPJSON.int(args["display"]) {
                guard display >= 1, display <= Int(count) else {
                    return .error("display must be between 1 and \(count) (1 = main display).")
                }
                modeArgs = ["-D", String(display)]
                detail["display"] = display
            } else if let idx = CaptureManager.shared.displayIndexUnderCursor() {
                modeArgs = ["-D", String(idx)]
                detail["display"] = idx
            } else {
                modeArgs = ["-m"]
                detail["display"] = 1
            }
            if Preferences.shared.captureCursor { modeArgs.append("-C") }
        case "window":
            let windows = Self.onScreenWindows()
            let target: WindowInfo?
            if let id = MCPJSON.int(args["window_id"]) {
                target = windows.first { $0.id == id }
                if target == nil { return .error("No on-screen window with window_id \(id). Call list_windows.") }
            } else if let app = args["app_name"] as? String, !app.isEmpty {
                target = windows.first { $0.app.caseInsensitiveCompare(app) == .orderedSame }
                if target == nil { return .error("No on-screen window of an app named \"\(app)\". Call list_windows.") }
            } else {
                let front = NSWorkspace.shared.frontmostApplication?.processIdentifier
                target = windows.first { Int32($0.pid) == front } ?? windows.first
                if target == nil { return .error("No application window is on screen.") }
            }
            guard let target else { return .error("No window to capture.") }
            modeArgs = ["-l", String(target.id)]
            if !Preferences.shared.windowShadow { modeArgs.append("-o") }
            detail["window"] = target.json
        case "region":
            switch region(args) {
            case .failure(let failure): return .error(failure.message)
            case .success(let r):
                modeArgs = ["-R", "\(Int(r.minX.rounded())),\(Int(r.minY.rounded())),\(Int(r.width.rounded())),\(Int(r.height.rounded()))"]
                detail["region"] = ["x": r.minX, "y": r.minY, "width": r.width, "height": r.height]
            }
        default:
            return .error("mode must be \"full_screen\", \"window\" or \"region\".")
        }

        if let delay = MCPJSON.int(args["delay_seconds"]) {
            guard (0...10).contains(delay) else { return .error("delay_seconds must be between 0 and 10.") }
            if delay > 0 { try? await Task.sleep(nanoseconds: UInt64(delay) * 1_000_000_000) }
        }

        let url: URL? = await withCheckedContinuation { cont in
            CaptureManager.shared.captureForAutomation(modeArgs: modeArgs) { cont.resume(returning: $0) }
        }
        guard let url else {
            return .error("The screenshot failed: macOS produced no image (window closed, region off screen, or permission revoked).")
        }
        AppCoordinator.shared.routeAutomationCapture(url)
        var out = detail
        out["path"] = url.path
        out["format"] = url.pathExtension.lowercased()
        if let (w, h) = pixelSize(of: url) { out["width"] = w; out["height"] = h }
        var extra: [[String: Any]] = []
        if MCPJSON.bool(args["include_image"]) == true, let image = previewImageContent(url) { extra.append(image) }
        return .object(out, extra: extra)
    }

    // MARK: - Recording

    @MainActor private func waitUntil(timeout: TimeInterval, _ done: @MainActor () -> Bool) async -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if done() { return true }
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
        return done()
    }

    @MainActor private func startRecording(_ args: [String: Any]) async -> MCPToolResult {
        let rec = RecordingManager.shared
        if rec.isRecording || rec.isStarting {
            return .error("A recording is already running\(rec.currentURL.map { " (\($0.path))" } ?? ""). Call stop_recording first.")
        }
        if let denied = requireScreenPermission() { return denied }
        let mode = args["mode"] as? String ?? "full_screen"
        switch mode {
        case "full_screen":
            rec.startFullScreen()
        case "region":
            switch region(args) {
            case .failure(let failure): return .error(failure.message)
            case .success(let r):
                // Top-left (CG) → Cocoa bottom-left, pivoting on the main display.
                let primaryH = NSScreen.screens.first { $0.frame.origin == .zero }?.frame.height
                    ?? NSScreen.main?.frame.height ?? r.maxY
                let cocoa = NSRect(x: r.minX, y: primaryH - r.maxY, width: r.width, height: r.height)
                guard NSScreen.screens.contains(where: { $0.frame.intersects(cocoa) }) else {
                    return .error("The region is not on any display.")
                }
                guard rec.startRegion(rect: cocoa) else {
                    return .error("Recording a region needs macOS 15 or later; use mode \"full_screen\".")
                }
            }
        default:
            return .error("mode must be \"full_screen\" or \"region\".")
        }
        _ = await waitUntil(timeout: 10) { rec.isRecording || !rec.isStarting }
        guard rec.isRecording, let url = rec.currentURL else {
            return .error("The recording could not start (FrameLine showed the reason on screen).")
        }
        return .object(["status": "recording", "mode": mode, "path": url.path])
    }

    @MainActor private func stopRecording() async -> MCPToolResult {
        let rec = RecordingManager.shared
        if rec.isStarting { _ = await waitUntil(timeout: 10) { !rec.isStarting } }
        guard rec.isRecording, let url = rec.currentURL else { return .error("No screen recording is running.") }
        rec.stop()
        _ = await waitUntil(timeout: 20) { !rec.isRecording && !rec.isStarting }
        guard !rec.isRecording else { return .error("The recording is still being finalized; call get_status in a moment.") }
        let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int) ?? 0
        guard size > 0 else { return .error("The recording stopped but no video was written.") }
        var out: [String: Any] = ["status": "stopped", "path": url.path, "size_bytes": size]
        if let duration = try? await AVURLAsset(url: url).load(.duration), duration.isNumeric {
            out["duration_seconds"] = (duration.seconds * 100).rounded() / 100
        }
        return .object(out)
    }

    // MARK: - Read-only

    @MainActor private func status() async -> MCPToolResult {
        let rec = RecordingManager.shared
        var out: [String: Any] = [
            "app": "FrameLine",
            "version": Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "?",
            "recording": rec.isRecording,
            "recording_starting": rec.isStarting,
            "screen_recording_permission": Permissions.hasScreenRecording(),
            "capture_folder": captureFolder.path,
            "image_format": Preferences.shared.imageFormat == "jpg" ? "jpg" : "png",
        ]
        if let url = rec.currentURL { out["recording_path"] = url.path }
        return .object(out)
    }

    @MainActor private func listRecent(_ args: [String: Any]) async -> MCPToolResult {
        let limit = min(200, max(1, MCPJSON.int(args["limit"]) ?? 20))
        let kindRaw = args["kind"] as? String ?? "all"
        guard let kind = CaptureFolder.Kind(rawValue: kindRaw) else {
            return .error("kind must be \"all\", \"image\" or \"video\".")
        }
        let dir = captureFolder
        let items = CaptureFolder.list(directory: dir, kind: kind, limit: limit)
        return .object(["capture_folder": dir.path, "count": items.count, "captures": items.map(\.json)])
    }

    @MainActor private func ocr(_ args: [String: Any]) async -> MCPToolResult {
        let url: URL
        switch resolveCapture(args, kind: .image) {
        case .failure(let failure): return .error(failure.message)
        case .success(let u): url = u
        }
        let text = await TextRecognizer.recognizeText(from: url)
        let lines = text.split(separator: "\n", omittingEmptySubsequences: true).count
        return .object(["path": url.path, "text": text, "line_count": lines])
    }

    // MARK: - Actions (visible to the user, nothing destructive)

    @MainActor private func copyToClipboard(_ args: [String: Any]) async -> MCPToolResult {
        let url: URL
        switch resolveCapture(args, kind: .all) {
        case .failure(let failure): return .error(failure.message)
        case .success(let u): url = u
        }
        if CaptureFolder.kind(of: url) == .video {
            let pb = NSPasteboard.general
            pb.clearContents()
            pb.writeObjects([url as NSURL])
            GlassConfirmBadge.show("Copié")
            return .object(["path": url.path, "copied": "file"])
        }
        AppCoordinator.shared.copyImage(url)
        return .object(["path": url.path, "copied": "image"])
    }

    @MainActor private func openInEditor(_ args: [String: Any]) async -> MCPToolResult {
        let url: URL
        switch resolveCapture(args, kind: .all) {
        case .failure(let failure): return .error(failure.message)
        case .success(let u): url = u
        }
        if CaptureFolder.kind(of: url) == .video {
            AppCoordinator.shared.openInApp(url)
            return .object(["path": url.path, "opened": "library"])
        }
        guard NSImage(contentsOf: url) != nil else { return .error("\(url.lastPathComponent) is not a readable image.") }
        AppCoordinator.shared.annotate(url)
        return .object(["path": url.path, "opened": "editor"])
    }
}
