import AppKit
import ScreenCaptureKit

/// Moteur de capture in-process via ScreenCaptureKit (comme BridgeShot) — remplace
/// /usr/sbin/screencapture. La responsabilité TCC est sans ambiguïté Easy Screen,
/// et SCK lève une erreur claire sans autorisation (pas de « fond d'écran seul » silencieux).
enum ScreenCapturer {

    /// Contenu partageable (écrans + fenêtres à l'écran).
    static func shareableContent() async throws -> SCShareableContent {
        try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
    }

    /// Snapshot complet d'un écran à sa résolution NATIVE (pixels), en excluant des fenêtres
    /// (typiquement notre propre overlay/galerie pour ne pas les capturer).
    static func captureDisplay(_ display: SCDisplay, excluding windows: [SCWindow] = []) async throws -> CGImage {
        let filter = SCContentFilter(display: display, excludingWindows: windows)
        let cfg = SCStreamConfiguration()
        cfg.showsCursor = false
        cfg.captureResolution = .best
        // Pixels = points de l'écran × échelle (Retina).
        let scale = NSScreen.screens.first { $0.es_displayID == display.displayID }?.backingScaleFactor ?? 2
        cfg.width = Int((CGFloat(display.width) * scale).rounded())
        cfg.height = Int((CGFloat(display.height) * scale).rounded())
        return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: cfg)
    }

    /// Écrit un CGImage sur disque au format voulu (png/jpg).
    @discardableResult
    static func write(_ image: CGImage, to url: URL) -> Bool {
        let rep = NSBitmapImageRep(cgImage: image)
        rep.size = NSSize(width: image.width, height: image.height)
        let isJPG = url.pathExtension.lowercased() == "jpg" || url.pathExtension.lowercased() == "jpeg"
        let props: [NSBitmapImageRep.PropertyKey: Any] = isJPG ? [.compressionFactor: 0.92] : [:]
        guard let data = rep.representation(using: isJPG ? .jpeg : .png, properties: props) else { return false }
        return (try? data.write(to: url)) != nil
    }
}

extension NSScreen {
    /// Identifiant CoreGraphics de l'écran (pour relier NSScreen ↔ SCDisplay).
    var es_displayID: CGDirectDisplayID {
        (deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value ?? 0
    }
}
