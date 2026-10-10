import AppKit
import AVFoundation
import ScreenCaptureKit

extension Notification.Name {
    static let recordingStateChanged = Notification.Name("FrameLine.recordingStateChanged")
}

/// Enregistrement vidéo de l'écran.
/// • macOS 15+ : moteur NATIF ScreenCaptureKit + `SCRecordingOutput` — écriture directe d'un .mov
///   avec vidéo + AUDIO SYSTÈME + MICRO (selon les réglages), curseur visible.
/// • macOS 14 : repli sur `/usr/sbin/screencapture -v` (vidéo seule, comme avant).
/// L'arrêt finalise proprement le fichier puis le route vers la galerie (parité avec les captures).
@MainActor
final class RecordingManager: NSObject {
    static let shared = RecordingManager()

    private(set) var currentURL: URL?
    private var _recording = false
    var isRecording: Bool { _recording }
    private var finalizing = false                 // anti double-finalisation
    // Sur le chemin SCK (macOS 15+), `_recording` ne passe à vrai qu'APRÈS ~1 s (son de démarrage +
    // startCapture). Ce drapeau, posé SYNCHRONEMENT sur le main actor avant tout `await`, ferme la
    // fenêtre de ré-entrance : un 2e déclenchement pendant le démarrage ne lance PAS un 2e SCStream
    // orphelin (qui enregistrerait sans jamais pouvoir être stoppé).
    private var starting = false
    // Arrêt demandé PENDANT le démarrage (bip en cours) : sans lui, le 2e appui était avalé (ni stream
    // ni process à arrêter) et l'enregistrement démarrait quand même ~1 s plus tard.
    private var cancelStart = false
    // Arrêt déjà lancé (stopCapture en vol) : un 2e appui ne relance ni un 2e stopCapture ni un 2e filet.
    private var stopping = false

    // ScreenCaptureKit (macOS 15+) — typés largement pour compiler sous la cible macOS 14.
    private var stream: SCStream?
    private var recordingOutput: NSObject?
    private var sckDelegate: NSObject?

    // Repli historique
    private let tool = URL(fileURLWithPath: "/usr/sbin/screencapture")
    private var process: Process?

    // MARK: - API publique

    func toggleFullScreen() { (isRecording || starting) ? stop() : startFullScreen() }

    func startFullScreen() {
        guard !isRecording, !starting else { return }
        // Sans autorisation : invite système / alerte Réglages (1×/session), puis badge — jamais muet.
        guard CaptureManager.shared.ensureScreenRecordingPermission() else {
            GlassConfirmBadge.show("Autorisation requise", symbol: "exclamationmark.triangle.fill"); return
        }
        if #available(macOS 15.0, *) { startSCK(rect: nil) }
        else { startLegacy(["-v", "-k", "-D", "1"]) }
    }

    func startRegion() {
        guard !isRecording, !starting else { return }
        // Sans autorisation : invite système / alerte Réglages (1×/session), puis badge — jamais muet.
        guard CaptureManager.shared.ensureScreenRecordingPermission() else {
            GlassConfirmBadge.show("Autorisation requise", symbol: "exclamationmark.triangle.fill"); return
        }
        if #available(macOS 15.0, *) {
            CaptureThumbnailController.shared.beginCaptureSession()      // pile hors écran dès la sélection
            RegionSelectOverlay.shared.begin { [weak self] rect in
                if let self, let rect { self.startSCK(rect: rect) }      // ouvre sa propre session (imbriquée)
                CaptureThumbnailController.shared.endCaptureSession()    // fin de la session « overlay »
            }
        } else {
            startLegacy(["-v", "-k", "-i"])
        }
    }

    func stop() {
        if starting { cancelStart = true; return }     // le démarrage en cours s'annulera de lui-même
        if #available(macOS 15.0, *), let stream {
            guard !stopping else { return }
            stopping = true
            let delegate = sckDelegate
            Task {
                try? await stream.stopCapture()        // → delegate didFinishRecording → routeFinished()
                // Filet : si l'output ne signale jamais sa fin (aucune image écrite, erreur muette),
                // on finalise quand même — sinon `_recording` restait vrai pour toujours et plus aucun
                // enregistrement n'était possible jusqu'au redémarrage de l'app.
                // Attente large (10 s) : un long enregistrement peut finaliser lentement ; on sort dès que
                // le vrai rappel a routé le fichier, pour ne jamais livrer un .mov encore en écriture.
                for _ in 0..<20 {
                    try? await Task.sleep(nanoseconds: 500_000_000)
                    guard let delegate, self.sckDelegate === delegate else { return }
                }
                self.routeFinished()
            }
        } else {
            process?.interrupt()                        // SIGINT → screencapture finalise le .mov
        }
    }

    // MARK: - ScreenCaptureKit (macOS 15+)

    @available(macOS 15.0, *)
    private func startSCK(rect: NSRect?) {
        guard !_recording, !starting else { return }   // ferme la ré-entrance AVANT le moindre `await`
        starting = true
        cancelStart = false
        // La pile de vignettes reste HORS ÉCRAN pendant TOUT l'enregistrement : sinon les cartes déjà
        // affichées (et celles des captures prises pendant l'enregistrement) sont filmées dans le .mov.
        CaptureThumbnailController.shared.beginCaptureSession()
        let url = Preferences.shared.newCaptureURL(ext: "mov")
        let wantMic = Preferences.shared.recordMicrophone
        let wantSystemAudio = Preferences.shared.recordSystemAudio
        let fps = Preferences.shared.recordFps

        Task {   // hérite de @MainActor (créé dans un contexte isolé MainActor)
            await self.playStartCueAndWait()       // bip de démarrage JOUÉ + TERMINÉ avant la capture
            if wantMic { await self.ensureMicPermission() }
            if self.cancelStart { self.failStart(showBadge: false); return }   // arrêté pendant le bip
            do {
                let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
                // Écran à filmer : pour une région, celui qui en contient la plus grande part (la sélection
                // peut être faite sur un écran secondaire — `displays.first` filmait alors le MAUVAIS écran,
                // avec un sourceRect hors de ses bornes) ; en plein écran, l'écran principal (barre des menus).
                let targetScreen: NSScreen? = rect.flatMap { r in
                    NSScreen.screens.max { a, b in
                        let ia = a.frame.intersection(r), ib = b.frame.intersection(r)
                        return ia.width * ia.height < ib.width * ib.height
                    }
                }
                let wantedID = targetScreen?.es_displayID ?? CGMainDisplayID()
                guard let display = content.displays.first(where: { $0.displayID == wantedID })
                        ?? content.displays.first else { self.failStart(); return }
                let screen = NSScreen.screens.first { $0.es_displayID == display.displayID }
                let scale = screen?.backingScaleFactor ?? 2

                let cfg = SCStreamConfiguration()
                cfg.showsCursor = true
                cfg.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(max(1, fps)))
                cfg.capturesAudio = wantSystemAudio
                if wantMic, AVCaptureDevice.authorizationStatus(for: .audio) == .authorized {
                    cfg.captureMicrophone = true       // micro mixé dans l'enregistrement
                }

                if let fullRect = rect, let screen {
                    // La sélection peut déborder sur un écran voisin : on la borne à l'écran filmé.
                    let rect = fullRect.intersection(screen.frame)
                    guard rect.width >= 1, rect.height >= 1 else { self.failStart(); return }
                    // Région : sourceRect en POINTS, repère haut-gauche de l'écran (NSRect = bas-gauche).
                    cfg.sourceRect = CGRect(x: rect.minX - screen.frame.minX,
                                            y: screen.frame.maxY - rect.maxY,
                                            width: rect.width, height: rect.height)
                    cfg.width = Int((rect.width * scale).rounded())
                    cfg.height = Int((rect.height * scale).rounded())
                } else {
                    cfg.width = Int((CGFloat(display.width) * scale).rounded())
                    cfg.height = Int((CGFloat(display.height) * scale).rounded())
                }

                let filter = SCContentFilter(display: display, excludingWindows: [])

                let recCfg = SCRecordingOutputConfiguration()
                recCfg.outputURL = url
                recCfg.outputFileType = .mov
                recCfg.videoCodecType = .h264

                let delegate = SCKRecordingDelegate(manager: self)
                let output = SCRecordingOutput(configuration: recCfg, delegate: delegate)
                let s = SCStream(filter: filter, configuration: cfg, delegate: delegate)
                try s.addRecordingOutput(output)
                if self.cancelStart { self.failStart(showBadge: false); return }   // arrêté pendant la préparation
                try await s.startCapture()

                self.stream = s
                self.recordingOutput = output
                self.sckDelegate = delegate
                self.currentURL = url
                self.finalizing = false
                self.stopping = false
                self._recording = true
                self.starting = false                  // démarrage terminé → la ré-entrance est désormais couverte par _recording
                // PAS de son d'obturateur au démarrage d'un enregistrement : c'est une affordance de
                // capture d'image, et avec l'audio système activé il se retrouvait gravé dans la piste
                // audio du .mov. (Le son reste joué pour les captures d'image, via AppCoordinator.)
                NotificationCenter.default.post(name: .recordingStateChanged, object: nil)
                if self.cancelStart { self.cancelStart = false; self.stop() }   // arrêt demandé pendant startCapture
            } catch {
                NSLog("FrameLine — SCK record start error: \(error.localizedDescription)")
                self.failStart()
            }
        }
    }

    /// Joue l'indicatif de démarrage et SUSPEND jusqu'à sa fin (+ petite marge) — ainsi le son est
    /// totalement terminé avant `startCapture()` et n'apparaît pas dans l'audio de l'enregistrement.
    /// (`Task.sleep` suspend sans bloquer le thread principal.)
    private func playStartCueAndWait() async {
        let dur = CaptureSound.playRecordingStart()
        guard dur > 0 else { return }
        try? await Task.sleep(nanoseconds: UInt64((dur + 0.06) * 1_000_000_000))
    }

    private func ensureMicPermission() async {
        if AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined {
            _ = await AVCaptureDevice.requestAccess(for: .audio)
        }
    }

    /// Appelé par le delegate SCK (thread arbitraire) à la fin / l'échec de l'enregistrement.
    /// `delegate` identifie le stream émetteur : un rappel d'un stream qui n'est PAS l'enregistrement
    /// courant (démarrage avorté, rappel tardif d'un enregistrement déjà routé) est ignoré — sinon il
    /// « terminait » l'enregistrement suivant (stream mis à nil alors qu'il tourne → impossible à stopper)
    /// et refermait une session de vignettes de trop.
    nonisolated func scRecordingFinished(from delegate: NSObject) {
        Task { @MainActor in
            guard self.sckDelegate === delegate else { return }
            self.routeFinished()
        }
    }

    /// Le stream s'est arrêté de lui-même (écran débranché, « Arrêter le partage » du système…). Le
    /// `SCRecordingOutput` finalise encore le .mov : on lui laisse le temps de signaler sa fin (qui route
    /// tout de suite) ; ce délai n'est qu'un filet. Router immédiatement envoyait à la galerie un fichier
    /// pas encore finalisé (« Vidéo illisible », vignette vide).
    nonisolated func scStreamStopped(from delegate: NSObject) {
        Task { @MainActor in
            guard self.sckDelegate === delegate else { return }
            try? await Task.sleep(nanoseconds: 2_000_000_000)
            guard self.sckDelegate === delegate else { return }
            self.routeFinished()
        }
    }

    private func routeFinished() {
        guard !finalizing else { return }
        finalizing = true
        let url = currentURL
        stream = nil
        recordingOutput = nil
        sckDelegate = nil
        currentURL = nil
        _recording = false
        starting = false
        stopping = false
        CaptureThumbnailController.shared.endCaptureSession()   // la pile revient (avant la vignette du .mov)

        if let url {
            let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int ?? 0) ?? 0
            if FileManager.default.fileExists(atPath: url.path), size > 0 {
                Notifier.notifyCapture(url: url, isVideo: true)
                CaptureLibrary.shared.add(url)        // l'enregistrement atterrit dans la galerie + sélection
                presentThumbnail(for: url)            // vignette flottante, comme après une capture d'image
            }
        }
        NotificationCenter.default.post(name: .recordingStateChanged, object: nil)
    }

    /// Fait surgir la vignette flottante post-enregistrement — MÊME affordance que les captures d'image
    /// (coin choisi dans les Réglages, actions au survol, glisser-déposer du fichier). On ne rouvre PLUS
    /// la fenêtre principale : parité exacte avec une capture d'écran. Piloté par « Afficher l'aperçu ».
    /// La poster-frame est générée HORS du thread principal (décodage vidéo), puis la vignette est présentée.
    private func presentThumbnail(for url: URL) {
        guard Preferences.shared.showPreview else { return }
        Task.detached(priority: .userInitiated) {
            let poster = RecordingManager.posterFrame(url: url)
            await MainActor.run {
                // Repli si la poster-frame échoue (fichier fraîchement finalisé) : image vide → le fond
                // noir de la carte + le badge « lecture » suffisent à signaler la vidéo.
                let image = poster ?? NSImage(size: NSSize(width: 320, height: 200))
                CaptureThumbnailController.shared.present(image: image, fileURL: url, isVideo: true)
            }
        }
    }

    /// Image d'aperçu (≈ 0,1 s) d'un enregistrement. `nonisolated` : appelée depuis une tâche détachée
    /// (AVAssetImageGenerator est thread-safe et ne doit pas figer le thread principal).
    nonisolated private static func posterFrame(url: URL) -> NSImage? {
        let asset = AVURLAsset(url: url)
        let gen = AVAssetImageGenerator(asset: asset)
        gen.appliesPreferredTrackTransform = true
        gen.maximumSize = CGSize(width: 512, height: 512)
        let time = CMTime(seconds: 0.1, preferredTimescale: 600)
        guard let cg = try? gen.copyCGImage(at: time, actualTime: nil) else { return nil }
        return NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
    }

    private func failStart(showBadge: Bool = true) {
        stream = nil
        recordingOutput = nil
        sckDelegate = nil
        currentURL = nil
        _recording = false
        starting = false                               // libère le verrou de démarrage → un nouvel essai est possible
        stopping = false
        cancelStart = false
        CaptureThumbnailController.shared.endCaptureSession()   // démarrage avorté → la pile revient
        if showBadge { GlassConfirmBadge.show("Recording failed", symbol: "exclamationmark.triangle.fill") }
        NotificationCenter.default.post(name: .recordingStateChanged, object: nil)
    }

    // MARK: - Repli historique (macOS 14) : screencapture -v

    private func startLegacy(_ baseArgs: [String]) {
        guard CaptureManager.shared.ensureScreenRecordingPermission() else { return }
        CaptureSound.playRecordingStart()              // bip de démarrage (screencapture -v ne capte pas l'audio)
        CaptureThumbnailController.shared.beginCaptureSession()   // pile hors écran pendant l'enregistrement
        let url = Preferences.shared.newCaptureURL(ext: "mov")
        let p = Process()
        p.executableURL = tool
        p.arguments = baseArgs + [url.path]
        p.terminationHandler = { [weak self] _ in
            Task { @MainActor in self?.legacyFinished(url: url) }
        }
        do {
            try p.run()
            process = p
            currentURL = url
            _recording = true
            NotificationCenter.default.post(name: .recordingStateChanged, object: nil)
        } catch {
            NSLog("FrameLine — recording error: \(error.localizedDescription)")
            process = nil
            CaptureThumbnailController.shared.endCaptureSession()
        }
    }

    private func legacyFinished(url: URL) {
        process = nil
        currentURL = nil
        _recording = false
        CaptureThumbnailController.shared.endCaptureSession()
        let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int ?? 0) ?? 0
        if FileManager.default.fileExists(atPath: url.path), size > 0 {
            Notifier.notifyCapture(url: url, isVideo: true)
            CaptureLibrary.shared.add(url)
            presentThumbnail(for: url)            // vignette flottante, comme après une capture d'image
        }
        NotificationCenter.default.post(name: .recordingStateChanged, object: nil)
    }
}

// MARK: - Delegate SCK (séparé : protocoles disponibles seulement à partir de macOS 15)

@available(macOS 15.0, *)
private final class SCKRecordingDelegate: NSObject, SCStreamDelegate, SCRecordingOutputDelegate {
    weak var manager: RecordingManager?
    init(manager: RecordingManager) { self.manager = manager }

    // SCRecordingOutputDelegate
    func recordingOutputDidStartRecording(_ recordingOutput: SCRecordingOutput) {}
    func recordingOutput(_ recordingOutput: SCRecordingOutput, didFailWithError error: any Error) {
        NSLog("FrameLine — recording failed: \(error.localizedDescription)")
        manager?.scRecordingFinished(from: self)
    }
    func recordingOutputDidFinishRecording(_ recordingOutput: SCRecordingOutput) {
        manager?.scRecordingFinished(from: self)
    }

    // SCStreamDelegate
    func stream(_ stream: SCStream, didStopWithError error: any Error) {
        NSLog("FrameLine — stream stopped: \(error.localizedDescription)")
        manager?.scStreamStopped(from: self)
    }
}
