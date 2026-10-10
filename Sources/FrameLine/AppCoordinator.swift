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
    /// Captures en cours d'écriture, et celles mises à la corbeille avant la fin de leur écriture.
    private var pendingWrites: Set<URL> = []
    private var trashedWhileWriting: Set<URL> = []

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
        // La pile de vignettes disparaît DÈS L'ENTRÉE en mode capture (et pas seulement au déclenchement) :
        // elle ne gêne plus la sélection et ne peut pas se retrouver dans la capture. `captureCocoaRect`
        // imbrique sa propre session → la pile ne réapparaît qu'une fois le fichier écrit.
        // Autorisation vérifiée AVANT le voile : sinon l'utilisateur sélectionne sa zone et rien ne se passe.
        guard CaptureManager.shared.ensureScreenRecordingPermission() else { return }
        CaptureThumbnailController.shared.beginCaptureSession()
        RegionSelectOverlay.shared.begin { rect in
            if let rect {
                CaptureManager.shared.captureCocoaRect(rect) { [weak self] url in self?.handle(url) }
            }
            CaptureThumbnailController.shared.endCaptureSession()   // fin de la session « overlay » (annulation comprise)
        }
    }

    /// Conservé (non branché) : ancien overlay maison de capture zone. Voir captureRegion().
    func captureWindowCustom() {
        CaptureWindowOverlayController.shared.onSwitchMode = { [weak self] in self?.captureRegion() }
        CaptureThumbnailController.shared.beginCaptureSession()      // la pile sort de l'écran pendant la sélection
        CaptureWindowOverlayController.shared.beginWindow { [weak self] image in
            // L'image est déjà capturée : on rend la pile AVANT de router, pour que la nouvelle vignette
            // arrive (en glissant) sur une pile visible.
            CaptureThumbnailController.shared.endCaptureSession()
            if let self, let image { self.handleCapturedImage(image) }
        }
    }

    /// Capture d'une FENÊTRE via le moteur natif (/usr/sbin/screencapture -i -w) — même fiabilité,
    /// même son d'obturateur. handle() route le fichier produit.
    func captureWindow() {
        CaptureManager.shared.capture(.window) { [weak self] in self?.handle($0) }
    }

    /// Plein écran : reste sur le moteur screencapture (pas d'overlay interactif requis).
    func captureFull() { CaptureManager.shared.capture(.fullScreen) { [weak self] in self?.handle($0) } }

    /// Capture plein écran différée de `seconds` (menu « Capture Fullscreen in… 3/5/10 s », façon BridgeShot).
    /// Laisse le temps d'arranger l'écran avant le déclenchement.
    func captureFullDelayed(_ seconds: Int) {
        GlassConfirmBadge.show("Capture in \(seconds)s…", symbol: "timer")
        DispatchQueue.main.asyncAfter(deadline: .now() + .seconds(seconds)) { [weak self] in
            self?.captureFull()
        }
    }

    func toggleRecording() { RecordingManager.shared.toggleFullScreen() }

    // MARK: - Épingler depuis le presse-papiers

    /// « Pin Image from Clipboard » (façon BridgeShot) : lit une image du presse-papiers, l'écrit
    /// dans un fichier temporaire PNG, puis la fait flotter par-dessus tout (fenêtre épinglée).
    /// Sans image dans le presse-papiers → avertissement discret.
    func pinFromClipboard() {
        guard let image = NSImage(pasteboard: NSPasteboard.general),
              let tiff = image.tiffRepresentation,
              let rep = NSBitmapImageRep(data: tiff),
              let png = rep.representation(using: .png, properties: [:]) else {
            GlassConfirmBadge.show("No image in clipboard", symbol: "exclamationmark.triangle.fill")
            return
        }
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("FrameLine-Pin-\(UUID().uuidString).png")
        do {
            try png.write(to: url)
            PinnedShotWindow.show(imageURL: url)
        } catch {
            GlassConfirmBadge.show("Pin failed", symbol: "exclamationmark.triangle.fill")
        }
    }

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
        pendingWrites.insert(url)
        Task.detached(priority: .userInitiated) {
            let ok = ScreenCapturer.write(image, to: url)
            await MainActor.run {
                self.pendingWrites.remove(url)
                // « Corbeille » cliquée sur la vignette AVANT la fin de l'écriture : on termine la suppression
                // ici, sinon la capture « supprimée » réapparaissait dans la galerie.
                if self.trashedWhileWriting.remove(url) != nil {
                    if ok { try? FileManager.default.trashItem(at: url, resultingItemURL: nil) }
                    return
                }
                guard ok else {
                    // Échec disque (volume non inscriptible, plein, dossier supprimé entre-temps…) :
                    // ON NE RESTE PLUS SILENCIEUX — sinon la capture est perdue sans aucun signe
                    // (« je prends un screen et rien ne se passe »). On retire la vignette orpheline
                    // (qui pointerait vers un fichier inexistant) et on prévient l'utilisateur.
                    // [SEC] Ne pas divulguer le chemin complet de capture dans le journal unifié
                    // (lisible par tout process du même utilisateur, non protégé par TCC) : on ne
                    // journalise que le nom de fichier, pas le dossier de sauvegarde.
                    NSLog("FrameLine — échec de l'enregistrement de la capture (%@)", url.lastPathComponent)
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

    /// « Ouvrir » une capture depuis la vignette flottante → ouvre la FENÊTRE PRINCIPALE de l'app
    /// (sidebar + détail) avec cette capture SÉLECTIONNÉE, façon BridgeShot.
    func openInApp(_ url: URL) {
        library.selectOnly(url)
        MainWindowController.shared.show()
    }

    /// Ouvre un sélecteur de fichiers pour importer des images/vidéos dans la bibliothèque. Appelable de
    /// PARTOUT (bouton de la barre latérale OU item du menu de la barre des menus, accessible depuis
    /// n'importe quelle app) : on diffère d'un tour de run-loop (présenter `runModal()` en plein clic est
    /// avalé) et on pose le panneau sur le BUREAU (Space) + l'écran ACTIF → il apparaît là où on se trouve.
    func importImages() {
        DispatchQueue.main.async { [library] in
            let panel = NSOpenPanel()
            panel.canChooseFiles = true
            panel.canChooseDirectories = false
            panel.allowsMultipleSelection = true
            panel.allowedContentTypes = [.image, .movie]
            // Panneau MODAL : il s'affiche sur le bureau (Space) actif de l'app tout seul. On NE touche
            // PAS son `collectionBehavior` (le faire empêchait le panneau de s'ouvrir du tout).
            NSApp.activate(ignoringOtherApps: true)
            if panel.runModal() == .OK { _ = library.importFiles(panel.urls) }
        }
    }

    /// Ouvre le dossier de sauvegarde des captures dans le Finder (le Finder l'affiche sur le bureau actif).
    func openCapturesFolder() { NSWorkspace.shared.open(library.directory) }

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
        // Retour visible comme dans l'éditeur (PreviewWindowController.ocrCopy) : sans lui, « Copier le
        // texte » depuis la galerie ne donnait AUCUN signe, ni de succès ni d'absence de texte.
        Task {
            let ok = await TextRecognizer.copyText(from: url)
            GlassConfirmBadge.show(ok ? "Texte copié" : "Aucun texte détecté",
                                   symbol: ok ? "checkmark.circle.fill" : "text.viewfinder")
        }
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
            // Ne plus annoncer « Enregistré » quand la copie a échoué (source supprimée, volume plein…).
            let ok = (try? FileManager.default.copyItem(at: url, to: dest)) != nil
            Task { @MainActor in
                if ok { GlassConfirmBadge.show("Enregistré") }
                else { GlassConfirmBadge.show("Échec de l'enregistrement", symbol: "exclamationmark.triangle.fill") }
            }
        }
    }

    /// « Mettre à la corbeille » : déplacement RÉVERSIBLE (≠ suppression définitive de library.remove).
    /// Le watcher de dossier rafraîchira la galerie ; on force un refresh immédiat pour la réactivité UI.
    func trash(_ url: URL) {
        if pendingWrites.contains(url) { trashedWhileWriting.insert(url); return }   // fichier pas encore écrit
        do {
            try FileManager.default.trashItem(at: url, resultingItemURL: nil)
        } catch {
            NSLog("FrameLine — mise à la corbeille impossible: \(error.localizedDescription)")
        }
        library.refresh()
    }

    /// Met à la corbeille TOUTE la sélection en une fois (multi-sélection façon Finder), ou la capture
    /// focalisée à défaut. Réversible (corbeille) et sans confirmation — pensé pour « supprimer plus vite »
    /// plusieurs captures d'un coup. Un seul refresh à la fin (au lieu d'un par fichier).
    func trashSelection() {
        let urls = library.actionableSelection
        guard !urls.isEmpty else { return }
        var trashed = 0
        for url in urls {
            do { try FileManager.default.trashItem(at: url, resultingItemURL: nil); trashed += 1 }
            catch { NSLog("FrameLine — mise à la corbeille impossible: \(error.localizedDescription)") }
        }
        library.refresh()
        guard trashed > 0 else { return }
        GlassConfirmBadge.show(trashed > 1 ? "\(trashed) captures supprimées" : "Capture supprimée", symbol: "trash")
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
