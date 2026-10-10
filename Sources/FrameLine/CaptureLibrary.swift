import AppKit
import AVFoundation
import Combine
import ImageIO

/// Un élément capturé (image ou vidéo) présent dans le dossier de captures.
struct Capture: Identifiable, Equatable {
    let url: URL
    let date: Date
    let isVideo: Bool
    var id: URL { url }
    var name: String { url.lastPathComponent }
    // Égalité sur TOUS les champs (synthétisée) : SwiftUI compare les propriétés des vues avec `==`.
    // Une égalité sur la seule URL faisait juger « inchangée » une capture réécrite sur place
    // (annotations, fond ajouté → nouvelle date, nouvelles dimensions) → sous-titre et L×H figés.
    // L'identité de liste reste l'URL (`id`).
}

/// Bibliothèque des captures : scanne le dossier, surveille les nouveaux fichiers,
/// génère des vignettes (async), gère import / suppression / sélection.
@MainActor
final class CaptureLibrary: ObservableObject {
    static let shared = CaptureLibrary()

    @Published private(set) var captures: [Capture] = []
    @Published private(set) var thumbnails: [URL: NSImage] = [:]
    /// Point focal (dernière capture cliquée) : pilote le panneau de détail.
    @Published var selection: URL?
    /// Sélection MULTIPLE façon Finder : Cmd-clic pour (dé)sélectionner une carte, Maj-clic pour une plage.
    /// Contient toujours au moins `selection` quand une capture est focalisée. Pilote la surbrillance des
    /// cartes et la suppression groupée ; `selection` reste le point focal du détail.
    @Published var selectedURLs: Set<URL> = []
    /// Ancre du Maj-clic (point de départ de la plage), inchangée entre deux Maj-clics — comportement Finder.
    private var selectionAnchor: URL?
    /// Au TOUT premier scan on sélectionne par défaut la capture la plus récente ; ensuite on RESPECTE une
    /// sélection vidée par l'utilisateur (Cmd-clic du dernier élément) — un refresh bénin ne la ré-invente pas.
    private var hasLoadedOnce = false

    private var source: DispatchSourceFileSystemObject?
    private var refreshWork: DispatchWorkItem?

    /// File BORNÉE de génération des vignettes. Avant : un `DispatchQueue.global().async` PAR capture →
    /// avec ~2 400 captures, GCD saturait son pool (64 threads utility-qos bloqués en lecture disque /
    /// décodage PNG / verrou XMP d'ImageIO, cf. rapports de plantage) + des Go de décodage simultanés,
    /// au lancement et à chaque rechargement. Deux travaux à la fois suffisent : le disque est le goulot.
    private let thumbQueue: OperationQueue = {
        let q = OperationQueue()
        q.name = "FrameLine.thumbnails"
        q.maxConcurrentOperationCount = 2
        q.qualityOfService = .utility
        return q
    }()
    /// Travaux en vol par URL (anti-doublon + annulation). Le jeton distingue un résultat OBSOLÈTE
    /// (fichier réécrit / supprimé entre-temps) du résultat attendu.
    private struct ThumbJob { let token: UUID; let op: Operation }
    private var thumbJobs: [URL: ThumbJob] = [:]
    /// Vignettes prêtes, publiées PAR LOTS : une publication de `thumbnails` par vignette réévaluait
    /// toute la galerie (des milliers de lignes) des milliers de fois d'affilée sur le main thread.
    private var thumbBuffer: [URL: NSImage] = [:]
    private var thumbFlushScheduled = false

    private let imageExts: Set<String> = ["png", "jpg", "jpeg", "tiff", "gif", "heic", "bmp"]
    private let videoExts: Set<String> = ["mov", "mp4", "m4v"]

    var directory: URL { Preferences.shared.saveDirectory }

    var selectedCapture: Capture? {
        guard let sel = selection else { return nil }
        return captures.first { $0.url == sel }
    }

    /// Captures visées par l'action « Supprimer » : la sélection multiple si présente, sinon le focus.
    var actionableSelection: [URL] {
        if !selectedURLs.isEmpty { return Array(selectedURLs) }
        return [selection].compactMap { $0 }
    }

    // MARK: - Sélection (façon Finder)

    /// Clic simple : sélectionne UNIQUEMENT cette capture (remplace toute sélection en cours).
    func selectOnly(_ url: URL) {
        selection = url
        selectedURLs = [url]
        selectionAnchor = url
    }

    /// Cmd-clic : (dé)sélectionne cette carte sans toucher aux autres (multi-sélection façon Finder).
    func toggleSelection(_ url: URL) {
        if selectedURLs.contains(url) {
            selectedURLs.remove(url)
            if selection == url { selection = focusAfterRemoving(url) }   // focus déterministe sur le voisin restant
        } else {
            selectedURLs.insert(url)
            selection = url
        }
        selectionAnchor = url
    }

    /// Maj-clic : sélectionne la PLAGE depuis l'ancre jusqu'à cette carte (ordre d'affichage).
    func extendSelection(to url: URL) {
        guard let anchor = selectionAnchor ?? selection,
              let a = captures.firstIndex(where: { $0.url == anchor }),
              let b = captures.firstIndex(where: { $0.url == url }) else { selectOnly(url); return }
        let lo = min(a, b), hi = max(a, b)
        selectedURLs = Set(captures[lo...hi].map(\.url))
        selection = url                                              // le focus suit le clic ; l'ancre reste fixe
    }

    /// Aiguillage depuis l'UI selon les modificateurs clavier au moment du clic.
    func handleClick(_ url: URL, command: Bool, shift: Bool) {
        if command { toggleSelection(url) }
        else if shift { extendSelection(to: url) }
        else { selectOnly(url) }
    }

    /// Vide toute sélection.
    func clearSelection() {
        selection = nil
        selectedURLs = []
        selectionAnchor = nil
    }

    /// Sélection classée dans l'ordre d'AFFICHAGE (déterministe, contrairement à `Set.first` qui suit
    /// l'ordre de hachage aléatoire → le focus sauterait sur un frère imprévisible).
    private var orderedSelection: [URL] { captures.compactMap { selectedURLs.contains($0.url) ? $0.url : nil } }

    /// Après retrait de `url`, choisit le focus sur le membre encore sélectionné le PLUS PROCHE
    /// (ordre d'affichage : voisin du bas puis du haut), plutôt qu'un élément arbitraire.
    private func focusAfterRemoving(_ url: URL) -> URL? {
        guard !selectedURLs.isEmpty else { return nil }
        guard let idx = captures.firstIndex(where: { $0.url == url }) else { return orderedSelection.first }
        var off = 1
        while idx + off < captures.count || idx - off >= 0 {
            if idx + off < captures.count, selectedURLs.contains(captures[idx + off].url) { return captures[idx + off].url }
            if idx - off >= 0, selectedURLs.contains(captures[idx - off].url) { return captures[idx - off].url }
            off += 1
        }
        return orderedSelection.first
    }

    // MARK: - Cycle de vie

    func start() {
        refresh()
        startWatching()
    }

    /// À appeler si l'utilisateur change le dossier de capture dans les réglages.
    func directoryChanged() {
        startWatching()
        refresh()
    }

    // MARK: - Lecture du dossier

    func refresh() {
        let fm = FileManager.default
        let dir = directory
        let urls = (try? fm.contentsOfDirectory(
            at: dir, includingPropertiesForKeys: [.contentModificationDateKey],
            options: [.skipsHiddenFiles])) ?? []

        let items: [Capture] = urls.compactMap { url in
            let ext = url.pathExtension.lowercased()
            let isVideo = videoExts.contains(ext)
            guard isVideo || imageExts.contains(ext) else { return nil }
            let date = (try? url.resourceValues(forKeys: [.contentModificationDateKey])
                .contentModificationDate) ?? .distantPast
            return Capture(url: url, date: date, isVideo: isVideo)
        }.sorted { $0.date > $1.date }

        captures = items
        let live = Set(items.map(\.url))
        // Purge la sélection multiple des fichiers disparus (corbeille, suppression externe…).
        selectedURLs = selectedURLs.filter { live.contains($0) }
        if let anchor = selectionAnchor, !live.contains(anchor) { selectionAnchor = nil }
        if let sel = selection, !live.contains(sel) {
            // Le fichier focalisé a disparu (suppression) → reprend le focus sur un membre restant de la
            // sélection, à défaut la 1re capture : le détail ne saute pas dans le vide après une suppression.
            selection = orderedSelection.first ?? items.first?.url
        } else if !hasLoadedOnce, selection == nil {
            // Tout premier scan : sélectionne par défaut la capture la plus récente.
            selection = items.first?.url
        }
        // …sinon (selection == nil hors 1er chargement) on RESPECTE une sélection vidée par l'utilisateur.
        hasLoadedOnce = true
        // Invariant : quand une capture est focalisée, elle appartient à la sélection (surbrillance cohérente).
        if let sel = selection, selectedURLs.isEmpty { selectedURLs = [sel] }
        // Purge le cache des vignettes orphelines (et réautorise un essai si le fichier réapparaît).
        // Les vignettes prêtes mais pas encore publiées sont versées d'abord (sinon re-générées pour rien).
        thumbnails.merge(thumbBuffer) { _, new in new }
        thumbBuffer = [:]
        thumbnails = thumbnails.filter { live.contains($0.key) }
        // Annule les travaux devenus sans objet (fichier supprimé / renommé / dossier vidé).
        for (url, job) in thumbJobs where !live.contains(url) {
            job.op.cancel()
            thumbJobs[url] = nil
        }
        generateThumbnails(for: items)
    }

    /// Réécriture sur place d'un fichier (ex. enregistrement d'annotations) : le
    /// watcher de dossier ne le détecte pas et le cache vignette est figé sur l'URL.
    /// On purge donc l'entrée de cache + l'essai « en vol » puis on rescanne pour
    /// régénérer la vignette et rebâtir la Capture (date de modif à jour).
    func reloadFile(_ url: URL) {
        invalidateThumbnail(url)
        refresh()
    }

    /// Oublie la vignette de `url` (cache, lot en attente, travail en vol annulé et rendu obsolète par
    /// son jeton) : un résultat lu sur l'ANCIEN contenu ne peut plus écraser la nouvelle vignette.
    private func invalidateThumbnail(_ url: URL) {
        thumbnails[url] = nil
        thumbBuffer[url] = nil
        thumbJobs[url]?.op.cancel()
        thumbJobs[url] = nil
    }

    // MARK: - Mutations

    /// Ajoute une capture qui vient d'être créée et la sélectionne.
    func add(_ url: URL) {
        // Le watcher a pu déclencher un refresh PENDANT l'écriture du fichier (écriture non atomique,
        // .mov pas encore finalisé) → vignette tronquée (moitié grise) cachée pour toujours, ou essai
        // vidéo encore en vol qui échouera sans réessai. Le fichier est complet ici : on repart de zéro.
        invalidateThumbnail(url)
        refresh()
        if captures.contains(where: { $0.url == url }) { selectOnly(url) }
    }

    func remove(_ capture: Capture) {
        try? FileManager.default.removeItem(at: capture.url)
        thumbnails[capture.url] = nil
        refresh()
    }

    func reveal(_ capture: Capture) {
        NSWorkspace.shared.activateFileViewerSelecting([capture.url])
    }

    func copyToPasteboard(_ capture: Capture) {
        guard !capture.isVideo else { return }
        let pb = NSPasteboard.general
        pb.clearContents()
        // PNG → octets d'origine (fidélité parfaite) ; autres formats (jpg/heic/gif/bmp/tiff)
        // → on passe par NSImage (writeObjects pose les bons types) pour ne pas déclarer
        // des octets non-TIFF comme .tiff (presse-papiers corrompu).
        if capture.url.pathExtension.lowercased() == "png", let data = try? Data(contentsOf: capture.url) {
            pb.setData(data, forType: .png)
        } else if let image = NSImage(contentsOf: capture.url) {
            pb.writeObjects([image])
        }
    }

    /// Importe des fichiers (glisser-déposer) dans le dossier de capture.
    @discardableResult
    func importFiles(_ urls: [URL]) -> Int {
        let fm = FileManager.default
        var imported = 0
        var last: URL?
        for src in urls {
            let ext = src.pathExtension.lowercased()
            guard imageExts.contains(ext) || videoExts.contains(ext) else { continue }
            let dest = uniqueDestination(for: src.lastPathComponent)
            if (try? fm.copyItem(at: src, to: dest)) != nil { imported += 1; last = dest }
        }
        if imported > 0 { refresh(); if let last { selectOnly(last) } }
        return imported
    }

    private func uniqueDestination(for name: String) -> URL {
        let dir = directory
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let base = (name as NSString).deletingPathExtension
        let ext = (name as NSString).pathExtension
        var dest = dir.appendingPathComponent(name)
        var n = 2
        while FileManager.default.fileExists(atPath: dest.path) {
            let newName = ext.isEmpty ? "\(base) (\(n))" : "\(base) (\(n)).\(ext)"
            dest = dir.appendingPathComponent(newName)
            n += 1
        }
        return dest
    }

    // MARK: - Vignettes (génération asynchrone + cache)

    private func generateThumbnails(for items: [Capture]) {
        // `items` est dans l'ordre d'affichage (plus récent d'abord) → la file FIFO traite le haut de la
        // galerie en premier ; les premières cartes passent DEVANT un arriéré déjà en file (nouvelle
        // capture prise pendant la génération initiale de milliers de vignettes).
        for (index, capture) in items.enumerated()
        where thumbnails[capture.url] == nil && thumbJobs[capture.url] == nil {
            let url = capture.url, isVideo = capture.isVideo
            let token = UUID()
            let op = BlockOperation { [weak self] in
                let img = CaptureLibrary.makeThumbnail(url: url, isVideo: isVideo, maxDim: 420)   // + net (l'aperçu s'affiche désormais en ENTIER, plus grand)
                DispatchQueue.main.async {
                    MainActor.assumeIsolated { self?.deliverThumbnail(img, for: url, token: token) }
                }
            }
            op.queuePriority = index < 24 ? .high : .normal
            thumbJobs[url] = ThumbJob(token: token, op: op)   // anti-doublon « en vol »
            thumbQueue.addOperation(op)
        }
    }

    /// Retour d'un travail (main thread). Ignoré s'il est obsolète (jeton remplacé ou purgé).
    private func deliverThumbnail(_ img: NSImage?, for url: URL, token: UUID) {
        guard thumbJobs[url]?.token == token else { return }
        thumbJobs[url] = nil                                  // échec (ex. .mov en cours d'écriture) → réessai au prochain refresh
        guard let img else { return }
        thumbBuffer[url] = img                                // succès → caché (publié avec le prochain lot)
        guard !thumbFlushScheduled else { return }
        thumbFlushScheduled = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) { [weak self] in
            guard let self else { return }
            self.thumbFlushScheduled = false
            guard !self.thumbBuffer.isEmpty else { return }
            self.thumbnails.merge(self.thumbBuffer) { _, new in new }
            self.thumbBuffer = [:]
        }
    }

    nonisolated private static func makeThumbnail(url: URL, isVideo: Bool, maxDim: CGFloat) -> NSImage? {
        if isVideo {
            let asset = AVURLAsset(url: url)
            let gen = AVAssetImageGenerator(asset: asset)
            gen.appliesPreferredTrackTransform = true
            gen.maximumSize = CGSize(width: maxDim * 2, height: maxDim * 2)
            let time = CMTime(seconds: 0.1, preferredTimescale: 600)
            guard let cg = try? gen.copyCGImage(at: time, actualTime: nil) else { return nil }
            return NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
        } else {
            // ImageIO = thread-safe et efficace (contrairement à NSImage.lockFocus hors main).
            guard let src = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
            let opts: [CFString: Any] = [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceThumbnailMaxPixelSize: maxDim * 2,
            ]
            guard let cg = CGImageSourceCreateThumbnailAtIndex(src, 0, opts as CFDictionary) else { return nil }
            return NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
        }
    }

    // MARK: - Surveillance du dossier (nouveaux fichiers → refresh auto)

    private func startWatching() {
        source?.cancel()
        source = nil
        let dir = directory
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let fd = open(dir.path, O_EVTONLY)
        guard fd >= 0 else { return }
        let src = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: fd, eventMask: [.write, .delete, .rename], queue: .main)
        src.setEventHandler { [weak self] in
            guard let self, let s = self.source else { return }
            let data = s.data
            self.scheduleRefresh()                            // coalesce les rafales d'événements
            if data.contains(.delete) || data.contains(.rename) {
                self.startWatching()                          // dossier supprimé/déplacé → recrée + re-surveille
            }
        }
        src.setCancelHandler { close(fd) }
        src.resume()
        source = src
    }

    /// Regroupe les événements du watcher (rafale d'écriture pendant un enregistrement, etc.) en un seul refresh.
    private func scheduleRefresh() {
        refreshWork?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.refresh() }
        refreshWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2, execute: work)
    }
}
