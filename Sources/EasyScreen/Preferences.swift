import Foundation
import ServiceManagement

/// Réglages persistés (UserDefaults) + gestion du lancement au démarrage.
final class Preferences {
    static let shared = Preferences()
    private let d = UserDefaults.standard

    private enum K {
        static let saveDir = "saveDirectoryPath"
        static let format = "imageFormat"            // "png" | "jpg"
        static let copyClipboard = "copyToClipboard"
        static let playSound = "playSound"
        static let showPreview = "showPreview"
        static let captureCursor = "captureCursor"
        static let windowShadow = "windowShadow"
        static let recordFps = "recordFps"           // fps de capture vidéo
        static let gifFps = "gifFps"                 // fps du GIF exporté
        static let gifWidth = "gifWidth"             // largeur (px) du GIF exporté
        static let exportGIF = "exportGIF"           // exporter aussi un GIF après l'enregistrement
    }

    private init() {
        d.register(defaults: [
            K.format: "png",
            K.copyClipboard: false,
            K.playSound: true,
            K.showPreview: true,
            K.captureCursor: false,
            K.windowShadow: true,
            K.recordFps: 30,
            K.gifFps: 15,
            K.gifWidth: 900,
            K.exportGIF: true,
        ])
    }

    var saveDirectory: URL {
        get {
            if let p = d.string(forKey: K.saveDir), !p.isEmpty { return URL(fileURLWithPath: p) }
            return FileManager.default.homeDirectoryForCurrentUser
                .appendingPathComponent("Pictures/Easy Screen", isDirectory: true)
        }
        set { d.set(newValue.path, forKey: K.saveDir) }
    }
    var imageFormat: String { get { d.string(forKey: K.format) ?? "png" } set { d.set(newValue, forKey: K.format) } }
    var copyToClipboard: Bool { get { d.bool(forKey: K.copyClipboard) } set { d.set(newValue, forKey: K.copyClipboard) } }
    var playSound: Bool { get { d.bool(forKey: K.playSound) } set { d.set(newValue, forKey: K.playSound) } }
    var showPreview: Bool { get { d.bool(forKey: K.showPreview) } set { d.set(newValue, forKey: K.showPreview) } }
    var captureCursor: Bool { get { d.bool(forKey: K.captureCursor) } set { d.set(newValue, forKey: K.captureCursor) } }
    var windowShadow: Bool { get { d.bool(forKey: K.windowShadow) } set { d.set(newValue, forKey: K.windowShadow) } }
    var recordFps: Int { get { let v = d.integer(forKey: K.recordFps); return v > 0 ? v : 30 } set { d.set(newValue, forKey: K.recordFps) } }
    var gifFps: Int { get { let v = d.integer(forKey: K.gifFps); return v > 0 ? v : 15 } set { d.set(newValue, forKey: K.gifFps) } }
    var gifWidth: Int { get { let v = d.integer(forKey: K.gifWidth); return v > 0 ? v : 900 } set { d.set(newValue, forKey: K.gifWidth) } }
    var exportGIF: Bool { get { d.bool(forKey: K.exportGIF) } set { d.set(newValue, forKey: K.exportGIF) } }
    var onboardingCompleted: Bool { get { d.bool(forKey: "onboardingCompleted") } set { d.set(newValue, forKey: "onboardingCompleted") } }
    var theme: String { get { d.string(forKey: "theme") ?? "dark" } set { d.set(newValue, forKey: "theme") } }  // system | light | dark
    var openEditor: Bool { get { d.bool(forKey: "openEditor") } set { d.set(newValue, forKey: "openEditor") } }
    /// Vrai dès que l'invite système d'enregistrement d'écran a été présentée au moins une fois
    /// (onboarding OU 1re capture) → empêche d'empiler/répéter les invites de permission.
    var didRequestScreenRecording: Bool { get { d.bool(forKey: "didRequestScreenRecording") } set { d.set(newValue, forKey: "didRequestScreenRecording") } }

    /// Garantit le dossier de capture ; en cas d'échec (dossier supprimé/non inscriptible),
    /// repli automatique sur ~/Pictures/Easy Screen pour ne jamais perdre une capture en silence.
    @discardableResult
    func ensureSaveDirectory() -> URL {
        let dir = saveDirectory
        if (try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)) != nil {
            return dir
        }
        let fallback = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Pictures/Easy Screen", isDirectory: true)
        try? FileManager.default.createDirectory(at: fallback, withIntermediateDirectories: true)
        NSLog("Easy Screen — dossier de capture inaccessible, repli sur \(fallback.path)")
        return fallback
    }

    /// Construit un nom de fichier horodaté unique dans le dossier de capture effectif.
    func newCaptureURL(ext: String) -> URL {
        let dir = ensureSaveDirectory()
        let fmt = DateFormatter()
        fmt.locale = Locale(identifier: "fr_FR")
        fmt.dateFormat = "yyyy-MM-dd 'à' HH.mm.ss"
        let stamp = fmt.string(from: Date())           // calculé UNE fois → nommage cohérent
        var url = dir.appendingPathComponent("Easy Screen \(stamp).\(ext)")
        var n = 2
        while FileManager.default.fileExists(atPath: url.path) {
            url = dir.appendingPathComponent("Easy Screen \(stamp) (\(n)).\(ext)")
            n += 1
        }
        return url
    }

    // MARK: - Lancement au démarrage (SMAppService, macOS 13+)
    var launchAtLogin: Bool {
        get {
            if #available(macOS 13.0, *) { return SMAppService.mainApp.status == .enabled }
            return false
        }
        set {
            guard #available(macOS 13.0, *) else { return }
            let service = SMAppService.mainApp
            do {
                if newValue {
                    if service.status != .enabled { try service.register() }
                } else {
                    if service.status != .notRegistered { try service.unregister() }
                }
            } catch {
                NSLog("Easy Screen — launchAtLogin error: \(error.localizedDescription)")
            }
        }
    }
}
