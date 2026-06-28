import AppKit
import Combine
import UniformTypeIdentifiers   // UTType pour contraindre le NSSavePanel d'export

/// Pont entre l'UI SwiftUI et le moteur AppKit (capture, enregistrement, réglages, annotation).
/// Aucune propriété @Published → s'abonner à `coord` n'invalide jamais les vues.
///
/// Depuis la bascule « façon BridgeShot », les captures interactives (région / fenêtre) passent
/// par nos overlays ScreenCaptureKit in-process (CaptureOverlayController / CaptureWindowOverlayController)
/// plutôt que par /usr/sbin/screencapture : ces overlays ne renvoient qu'un CGImage brut, et c'est
/// ICI qu'on persiste, copie, notifie et route (galerie + vignette flottante + aperçu + éditeur).
@MainActor
final class AppCoordinator: ObservableObject {
    static let shared = AppCoordinator()

    let library = CaptureLibrary.shared

    var onOpenPreferences: (() -> Void)?
    var onAnnotate: ((URL) -> Void)?

    // MARK: - Captures

    /// Capture d'une RÉGION via le MOTEUR NATIF macOS (/usr/sbin/screencapture -i -s) — EXACTEMENT
    /// comme BridgeShot. C'est le MÊME binaire que la capture plein écran (qui fonctionne) → fiable à
    /// 100 %. L'overlay SYSTÈME apporte tout « façon BridgeShot » sans code fragile : voile sombre hors
    /// sélection, loupe « #hex · x,y », badge de dimensions, Espace = bascule fenêtre, Échap = annuler,
    /// et le SON d'obturateur natif. handle() route le fichier produit (galerie + vignette + éditeur).
    /// (L'overlay maison ScreenCaptureKit — CaptureOverlayController — est conservé dans les sources
    /// mais n'est plus branché : il était la cause du « ça ne prend pas le screen ».)
    func captureRegion() {
        // Overlay maison : VOILE SOMBRE + trou clair sur la sélection (demande explicite « zone non
        // capturée en sombre »), glisser suivi par POLLING matériel (fiable). La capture du rectangle
        // est ensuite faite par le moteur NATIF screencapture -R (fiable, + son d'obturateur).
        RegionSelectOverlay.shared.begin { [weak self] rect in
            guard let self, let rect else { return }   // nil = annulé / clic / trop petit
            CaptureManager.shared.captureCocoaRect(rect) { [weak self] url in self?.handle(url) }
        }
    }

    /// Conservé (non branché) : ancien overlay maison de capture zone. Voir captureRegion().
    func captureWindowCustom() {
        CaptureWindowOverlayController.shared.onSwitchMode = { [weak self] in self?.captureRegion() }
        CaptureWindowOverlayController.shared.beginWindow { [weak self] image in
            guard let self, let image else { return }
            self.handleCapturedImage(image)
        }
    }

    /// Capture d'une FENÊTRE via le moteur natif (/usr/sbin/screencapture -i -w) — même fiabilité,
    /// même son d'obturateur. handle() route le fichier produit.
    func captureWindow() {
        CaptureManager.shared.capture(.window) { [weak self] in self?.handle($0) }
    }

    /// Plein écran : reste sur le moteur screencapture (pas d'overlay interactif requis).
    func captureFull() { CaptureManager.shared.capture(.fullScreen) { [weak self] in self?.handle($0) } }

    func toggleRecording() { RecordingManager.shared.toggleFullScreen() }

    // MARK: - Routage commun des images capturées

    /// Route commune pour une image fraîchement capturée par un overlay (région / fenêtre).
    /// Parité avec CaptureManager : si « copier au presse-papiers » est actif, l'image va
    /// UNIQUEMENT au presse-papiers (ni fichier, ni galerie, ni notification), sinon on écrit
    /// le fichier au format choisi puis on route comme une capture normale.
    private func handleCapturedImage(_ image: CGImage) {
        ESLog.snapshot("handleCapturedImage (copyToClipboard=\(Preferences.shared.copyToClipboard))")
        CaptureSound.play()                                          // ScreenCaptureKit est silencieux → déclic de capture ici
        if Preferences.shared.copyToClipboard {
            copyCGImageToPasteboard(image)
            GlassConfirmBadge.show("Copié")
            return
        }
        let ext = Preferences.shared.imageFormat == "jpg" ? "jpg" : "png"
        let url = Preferences.shared.newCaptureURL(ext: ext)
        // Aperçu INSTANTANÉ depuis l'image EN MÉMOIRE : on n'attend ni l'encodage PNG ni la relecture
        // disque (c'était le « délai de quelques secondes » après le relâchement, surtout en Retina).
        if Preferences.shared.showPreview {
            let ns = NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height))
            CaptureThumbnailController.shared.present(image: ns, fileURL: url)
        }
        // Encodage + écriture HORS du thread principal → l'UI ne se fige plus.
        Task.detached(priority: .userInitiated) {
            let ok = ScreenCapturer.write(image, to: url)
            await MainActor.run {
                guard ok else {
                    // Échec disque (volume non inscriptible, plein, dossier supprimé entre-temps…) :
                    // ON NE RESTE PLUS SILENCIEUX — sinon la capture est perdue sans aucun signe
                    // (« je prends un screen et rien ne se passe »). On retire la vignette orpheline
                    // (qui pointerait vers un fichier inexistant) et on prévient l'utilisateur.
                    NSLog("Easy Screen — échec de l'enregistrement de la capture: \(url.path)")
                    CaptureThumbnailController.shared.dismiss(forFailedURL: url)
                    GlassConfirmBadge.show("Échec de l'enregistrement", symbol: "exclamationmark.triangle.fill")
                    return
                }
                Notifier.notifyCapture(url: url, isVideo: false)
                self.library.add(url)
                if Preferences.shared.openEditor { self.onAnnotate?(url) }
            }
        }
    }

    /// Routage post-capture commun à tous les modes image (overlay + plein écran).
    /// Ajoute à la galerie, fait surgir la vignette flottante, puis applique les préférences
    /// d'aperçu et d'ouverture de l'éditeur.
    private func handle(_ url: URL?) {
        guard let url else { return }
        ESLog.snapshot("handle (showPreview=\(Preferences.shared.showPreview) openEditor=\(Preferences.shared.openEditor))")
        library.add(url)
        // Affordance post-capture FAÇON BRIDGESHOT : on n'affiche QUE la vignette flottante
        // (≈ « quick access overlay after capture »), pilotée par « Afficher l'aperçu rapide
        // après capture » (showPreview). On NE fait PLUS resurgir la fenêtre galerie à chaque
        // capture — BridgeShot ne réouvre jamais sa fenêtre principale (c'était le « ça me
        // réouvre le window » indésirable, et la source du switch de Space).
        if Preferences.shared.showPreview, let img = NSImage(contentsOf: url) {
            CaptureThumbnailController.shared.present(image: img, fileURL: url)
        }
        if Preferences.shared.openEditor { onAnnotate?(url) }                       // « ouvrir l'éditeur après capture »
    }

    // MARK: - Actions sur une capture (menu contextuel, vignette, barre d'actions)

    /// Ouvre l'éditeur d'annotation (câblé sur PreviewWindowController via onAnnotate).
    func annotate(_ url: URL) { onAnnotate?(url) }

    /// Copie l'image au presse-papiers. PNG → octets d'origine (fidélité parfaite) ;
    /// autres formats → on passe par NSImage (writeObjects pose les bons types).
    func copyImage(_ url: URL) {
        let pb = NSPasteboard.general
        pb.clearContents()
        if url.pathExtension.lowercased() == "png", let data = try? Data(contentsOf: url) {
            pb.setData(data, forType: .png)
        } else if let image = NSImage(contentsOf: url) {
            pb.writeObjects([image])
        }
        GlassConfirmBadge.show("Copié")
    }

    /// OCR (Vision) → copie le texte reconnu au presse-papiers. Asynchrone : l'OCR tourne
    /// hors du main thread, le module ne touche le presse-papiers que s'il a trouvé du texte.
    func copyText(_ url: URL) {
        Task { await TextRecognizer.copyText(from: url) }
    }

    /// « Restaurer à l'écran » et « Épingler à l'écran » partagent le même besoin : refaire
    /// flotter l'image par-dessus tout (fenêtre épinglée déplaçable). On route les deux vers le
    /// même point d'entrée.
    func restoreToScreen(_ url: URL) { PinnedShotWindow.show(imageURL: url) }
    func pinToScreen(_ url: URL) { PinnedShotWindow.show(imageURL: url) }

    /// Révèle le fichier dans le Finder (sélectionné).
    func revealInFinder(_ url: URL) {
        NSWorkspace.shared.activateFileViewerSelecting([url])
    }

    /// Exporte (copie) la capture vers un emplacement choisi par l'utilisateur.
    func exportCapture(_ url: URL) {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = url.lastPathComponent
        panel.canCreateDirectories = true
        if let type = UTType(filenameExtension: url.pathExtension) {
            panel.allowedContentTypes = [type]                  // n'autorise que le type du fichier source
        }
        NSApp.activate(ignoringOtherApps: true)                 // app accessoire : ramène le panneau au premier plan
        panel.begin { resp in
            guard resp == .OK, let dest = panel.url, dest != url else { return }
            try? FileManager.default.removeItem(at: dest)       // l'utilisateur a déjà confirmé le remplacement
            try? FileManager.default.copyItem(at: url, to: dest)
            Task { @MainActor in GlassConfirmBadge.show("Enregistré") }
        }
    }

    /// « Mettre à la corbeille » : déplacement RÉVERSIBLE (≠ suppression définitive de library.remove).
    /// Le watcher de dossier rafraîchira la galerie ; on force un refresh immédiat pour la réactivité UI.
    func trash(_ url: URL) {
        do {
            try FileManager.default.trashItem(at: url, resultingItemURL: nil)
        } catch {
            NSLog("Easy Screen — mise à la corbeille impossible: \(error.localizedDescription)")
        }
        library.refresh()
    }

    // MARK: - Réglages

    func openPreferences() { onOpenPreferences?() }

    /// Copie un CGImage au presse-papiers en PNG (utilisé quand copyToClipboard est actif).
    private func copyCGImageToPasteboard(_ image: CGImage) {
        let rep = NSBitmapImageRep(cgImage: image)
        guard let data = rep.representation(using: .png, properties: [:]) else { return }
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setData(data, forType: .png)
    }
}
