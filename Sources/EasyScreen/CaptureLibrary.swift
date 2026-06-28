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
    static func == (a: Capture, b: Capture) -> Bool { a.url == b.url }
}

/// Bibliothèque des captures : scanne le dossier, surveille les nouveaux fichiers,
/// génère des vignettes (async), gère import / suppression / sélection.
@MainActor
final class CaptureLibrary: ObservableObject {
    static let shared = CaptureLibrary()

    @Published private(set) var captures: [Capture] = []
    @Published private(set) var thumbnails: [URL: NSImage] = [:]
    @Published var selection: URL?

    private var source: DispatchSourceFileSystemObject?
    private var thumbAttempts: Set<URL> = []
    private var refreshWork: DispatchWorkItem?

    private let imageExts: Set<String> = ["png", "jpg", "jpeg", "tiff", "gif", "heic", "bmp"]
    private let videoExts: Set<String> = ["mov", "mp4", "m4v"]

    var directory: URL { Preferences.shared.saveDirectory }

    var selectedCapture: Capture? {
        guard let sel = selection else { return nil }
        return captures.first { $0.url == sel }
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
        // Conserve la sélection si toujours valide, sinon la première capture.
        if selection == nil || !items.contains(where: { $0.url == selection }) {
            selection = items.first?.url
        }
        // Purge le cache des vignettes orphelines (et réautorise un essai si le fichier réapparaît).
        let live = Set(items.map(\.url))
        thumbnails = thumbnails.filter { live.contains($0.key) }
        thumbAttempts = thumbAttempts.filter { live.contains($0) }
        generateThumbnails(for: items)
    }

    /// Réécriture sur place d'un fichier (ex. enregistrement d'annotations) : le
    /// watcher de dossier ne le détecte pas et le cache vignette est figé sur l'URL.
    /// On purge donc l'entrée de cache + l'essai « en vol » puis on rescanne pour
    /// régénérer la vignette et rebâtir la Capture (date de modif à jour).
    func reloadFile(_ url: URL) {
        thumbnails[url] = nil
        thumbAttempts.remove(url)
        refresh()
    }

    // MARK: - Mutations

    /// Ajoute une capture qui vient d'être créée et la sélectionne.
    func add(_ url: URL) {
        refresh()
        if captures.contains(where: { $0.url == url }) { selection = url }
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
        if imported > 0 { refresh(); if let last { selection = last } }
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
        for capture in items where thumbnails[capture.url] == nil && !thumbAttempts.contains(capture.url) {
            thumbAttempts.insert(capture.url)                 // anti-doublon « en vol »
            let url = capture.url, isVideo = capture.isVideo
            DispatchQueue.global(qos: .utility).async {
                let img = CaptureLibrary.makeThumbnail(url: url, isVideo: isVideo, maxDim: 256)
                DispatchQueue.main.async {
                    if let img {
                        self.thumbnails[url] = img            // succès → caché
                    } else {
                        self.thumbAttempts.remove(url)        // échec (ex. .mov en cours d'écriture) → réessai possible
                    }
                }
            }
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
