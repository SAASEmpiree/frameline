import AppKit
import CoreGraphics

/// Captures d'écran via /usr/sbin/screencapture (moteur natif macOS), lancé en process direct.
/// ⚠️ La responsabilité TCC ne reste sur Easy Screen QUE si l'app est lancée via LaunchServices
/// (Finder / `open "Easy Screen.app"`). Lancée depuis un shell (`swift run`, ./.build/…) ou le
/// débogueur Xcode, la responsabilité est re-rootée sur le lanceur → capture du fond d'écran seul.
final class CaptureManager {
    static let shared = CaptureManager()
    private let tool = URL(fileURLWithPath: "/usr/sbin/screencapture")

    enum Mode { case region, window, fullScreen }

    /// Déclenche une capture. `toClipboard` force la copie au presse-papiers.
    /// `completion` reçoit l'URL du fichier (mode fichier) ou nil (annulé / presse-papiers / refus).
    func capture(_ mode: Mode, toClipboard forceClipboard: Bool = false, completion: ((URL?) -> Void)? = nil) {
        // Garde TCC : sans l'autorisation, screencapture renvoie une image du seul fond d'écran.
        guard ensureScreenRecordingPermission() else { completion?(nil); return }

        let prefs = Preferences.shared
        let toClipboard = forceClipboard || prefs.copyToClipboard
        let ext = toClipboard ? "png" : (prefs.imageFormat == "jpg" ? "jpg" : "png")

        // On silence TOUJOURS le son natif (-x) et on joue NOTRE déclic (CaptureSound) à la complétion :
        // le son d'obturateur est ainsi garanti (le son natif dépend de réglages système), et identique
        // pour tous les modes. CaptureSound respecte déjà la préférence « son ».
        var args: [String] = ["-x"]
        args.append(contentsOf: ["-t", ext])

        let interactive = (mode != .fullScreen)
        switch mode {
        case .region:
            args.append(contentsOf: ["-i", "-s"])                 // sélection de zone uniquement
        case .window:
            args.append(contentsOf: ["-i", "-w"])                 // sélection de fenêtre
            if !prefs.windowShadow { args.append("-o") }          // sans ombre
        case .fullScreen:
            if prefs.captureCursor { args.append("-C") }          // inclure le curseur (non interactif)
            // Écran SOUS LE CURSEUR (multi-écran) plutôt que toujours l'écran principal : sur un
            // setup 2 moniteurs, ⇧⌘9 depuis l'écran secondaire capturait le primaire (« mauvais
            // écran / rien »). Un SEUL fichier de sortie est conservé (la détection d'annulation par
            // existence du fichier reste valable). Repli sûr sur -m si l'écran est indéterminable.
            if let idx = Self.screencaptureDisplayIndex(forScreenAt: NSEvent.mouseLocation) {
                args.append(contentsOf: ["-D", "\(idx)"])
            } else {
                args.append("-m")
            }
        }

        // Capture TOUJOURS vers un fichier (même pour le presse-papiers) : un seul
        // signal d'annulation pour tous les modes = « le fichier n'a pas été créé ».
        let dest: URL = toClipboard
            ? FileManager.default.temporaryDirectory.appendingPathComponent("EasyScreen-\(UUID().uuidString).png")
            : prefs.newCaptureURL(ext: ext)
        args.append(dest.path)

        // Laisse le menu se refermer avant l'overlay système (sinon il peut figurer sur la capture).
        let delay = interactive ? 0.15 : 0.0
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
            self.run(args) { _ in
                // Échap = code de sortie 0 MAIS aucun fichier → on détecte l'annulation par l'existence.
                guard FileManager.default.fileExists(atPath: dest.path) else {
                    DispatchQueue.main.async { completion?(nil) }
                    return
                }
                if toClipboard {
                    let data = try? Data(contentsOf: dest)
                    DispatchQueue.main.async {
                        if let data = data {
                            let pb = NSPasteboard.general
                            pb.clearContents()
                            pb.setData(data, forType: .png)
                        }
                        try? FileManager.default.removeItem(at: dest)
                        CaptureSound.play()
                        completion?(nil)
                    }
                } else {
                    DispatchQueue.main.async {
                        CaptureSound.play()
                        Notifier.notifyCapture(url: dest, isVideo: false)
                        completion?(dest)
                    }
                }
            }
        }
    }

    /// Index d'écran (1-based ; écran principal = 1) pour `screencapture -D`, correspondant à l'écran
    /// qui contient `loc` (coordonnées globales Cocoa). nil si indéterminable → l'appelant retombe sur -m.
    /// CGGetActiveDisplayList place l'écran principal en tête, ce qui mappe directement sur l'index 1-based.
    private static func screencaptureDisplayIndex(forScreenAt loc: NSPoint) -> Int? {
        guard let screen = NSScreen.screens.first(where: { NSMouseInRect(loc, $0.frame, false) }) else { return nil }
        let target = screen.es_displayID
        guard target != 0 else { return nil }
        var count: UInt32 = 0
        guard CGGetActiveDisplayList(0, nil, &count) == .success, count > 0 else { return nil }
        var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
        guard CGGetActiveDisplayList(count, &ids, &count) == .success else { return nil }
        guard let i = ids.firstIndex(of: target) else { return nil }
        return i + 1
    }

    /// Capture une ZONE précise (rect en coords GLOBALES Cocoa, origine bas-gauche) via le moteur
    /// NATIF `screencapture -R` — fiable (même binaire que le plein écran). L'appelant (overlay de
    /// sélection) a déjà retiré son voile ; un court délai garantit qu'il a disparu de l'écran avant
    /// la capture. Son d'obturateur joué ici. `completion` reçoit l'URL (fichier) ou nil (presse-papiers
    /// / refus / échec), sur le main.
    func captureCocoaRect(_ rect: NSRect, completion: ((URL?) -> Void)? = nil) {
        guard ensureScreenRecordingPermission() else { completion?(nil); return }
        let prefs = Preferences.shared
        let toClipboard = prefs.copyToClipboard
        let ext = toClipboard ? "png" : (prefs.imageFormat == "jpg" ? "jpg" : "png")

        // Cocoa global (bas-gauche) → CG global (haut-gauche, pivot écran primaire) pour -R.
        let primaryH = NSScreen.screens.first { $0.frame.origin == .zero }?.frame.height
            ?? NSScreen.main?.frame.height ?? rect.maxY
        let x = Int(rect.minX.rounded())
        let y = Int((primaryH - rect.maxY).rounded())
        let w = max(1, Int(rect.width.rounded()))
        let h = max(1, Int(rect.height.rounded()))

        let dest: URL = toClipboard
            ? FileManager.default.temporaryDirectory.appendingPathComponent("EasyScreen-\(UUID().uuidString).png")
            : prefs.newCaptureURL(ext: ext)
        let args = ["-x", "-t", ext, "-R", "\(x),\(y),\(w),\(h)", dest.path]

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) {   // laisse le voile disparaître
            self.run(args) { _ in
                guard FileManager.default.fileExists(atPath: dest.path) else {
                    DispatchQueue.main.async { completion?(nil) }
                    return
                }
                if toClipboard {
                    let data = try? Data(contentsOf: dest)
                    DispatchQueue.main.async {
                        if let data = data {
                            let pb = NSPasteboard.general
                            pb.clearContents()
                            pb.setData(data, forType: .png)
                        }
                        try? FileManager.default.removeItem(at: dest)
                        CaptureSound.play()
                        completion?(nil)
                    }
                } else {
                    DispatchQueue.main.async {
                        CaptureSound.play()
                        Notifier.notifyCapture(url: dest, isVideo: false)
                        completion?(dest)
                    }
                }
            }
        }
    }

    private func run(_ args: [String], done: @escaping (Int32) -> Void) {
        DispatchQueue.global(qos: .userInitiated).async {
            let p = Process()
            p.executableURL = self.tool
            p.arguments = args
            p.terminationHandler = { proc in done(proc.terminationStatus) }
            do { try p.run() }
            catch { NSLog("Easy Screen — screencapture error: \(error.localizedDescription)"); done(-1) }
        }
    }

    /// Au-delà du 1er affichage, le NSAlert n'est plus montré qu'UNE fois par session (anti-harcèlement).
    private static var deniedAlertShownThisSession = false

    /// Garde d'autorisation. Garantit : accordé → AUCUN message ; 1er passage de toute la vie de l'app
    /// → SEULEMENT le prompt système natif (jamais empilé avec le NSAlert) ; ensuite → NSAlert au plus
    /// une fois par session. À appeler sur le main.
    @discardableResult
    func ensureScreenRecordingPermission() -> Bool {
        if Permissions.hasScreenRecording() {
            Self.deniedAlertShownThisSession = false             // ré-arme UNE invite si l'autorisation est révoquée plus tard
            return true                                          // chemin « accordé » : zéro message
        }
        // 1er passage de toute la vie de l'app : SEUL le prompt système natif (qui route déjà vers les Réglages).
        if !Preferences.shared.didRequestScreenRecording {
            Preferences.shared.didRequestScreenRecording = true
            Permissions.requestScreenRecording()                 // dialogue système si « indéterminé »
            return false
        }
        // Au-delà : le prompt système est devenu un no-op → NSAlert actionnable, au plus une fois/session.
        guard !Self.deniedAlertShownThisSession else { return false }
        Self.deniedAlertShownThisSession = true

        let alert = NSAlert()
        alert.messageText = "Autorisation requise"
        alert.informativeText = """
        Easy Screen a besoin de l'autorisation « Enregistrement de l'écran » pour capturer votre écran.

        Activez Easy Screen dans Réglages Système › Confidentialité et sécurité › Enregistrement de l'écran, puis relancez l'application.
        """
        alert.addButton(withTitle: "Ouvrir les Réglages")
        alert.addButton(withTitle: "Plus tard")
        NSApp.activate(ignoringOtherApps: true)
        if alert.runModal() == .alertFirstButtonReturn { Permissions.openScreenRecordingSettings() }
        return false
    }
}
