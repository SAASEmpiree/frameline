import AppKit
import AVFoundation
import UniformTypeIdentifiers

/// ÉDITEUR DE ROGNAGE VIDÉO — jumeau temporel de l'éditeur d'annotation d'image.
///
/// Quand une capture est une VIDÉO, le détail affiche cet éditeur : lecteur au centre (letterbox
/// sur charbon #131313), même DEUX capsules flottantes que l'éditeur d'image (barre du HAUT =
/// transport + actions Copy/Done ; barre du BAS = « pellicule » de rognage). On y ajuste le
/// début/fin pour ne garder qu'un extrait, puis « Done » enregistre l'extrait PAR-DESSUS l'original
/// (parité avec l'éditeur), « Enregistrer une copie… » conserve l'original.
///
/// La « pellicule » réutilise LE LANGAGE EXACT du masque de rognage de l'éditeur d'image
/// (`drawCropMask` : tout hors sélection assombri en noir 0.45 + cadre blanc 1pt) → un rognage
/// TEMPOREL ressemble à un rognage SPATIAL. Zéro couleur inventée : tout vient de la même DA.
///
/// On évite VOLONTAIREMENT AVKit / le `VideoPlayer` SwiftUI (plante en exécutable SwiftPM) : le
/// rendu passe par un `AVPlayerLayer` nu, tout le transport est à nous.

/// Charbon #131313 — fond partagé avec le détail et l'éditeur d'image.
private let flInk = NSColor(white: 19.0 / 255.0, alpha: 1)

private extension Double {
    func clamped(_ lo: Double, _ hi: Double) -> Double { Swift.max(lo, Swift.min(hi, self)) }
}

// MARK: - Vue lecteur (AVPlayerLayer nu, aucune chrome AVKit)

/// `NSView` adossée à un `AVPlayerLayer` : letterbox `.resizeAspect` sur fond charbon, aucune
/// commande native (on dessine nos propres capsules). La couche suit `bounds` toute seule.
final class PlayerLayerView: NSView {
    override func makeBackingLayer() -> CALayer {
        let l = AVPlayerLayer()
        l.videoGravity = .resizeAspect
        l.backgroundColor = flInk.cgColor
        return l
    }
    var playerLayer: AVPlayerLayer { layer as! AVPlayerLayer }
    var player: AVPlayer? {
        get { playerLayer.player }
        set { playerLayer.player = newValue }
    }
    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layerContentsRedrawPolicy = .duringViewResize
    }
    required init?(coder: NSCoder) { fatalError("no coder") }
}

// MARK: - Pellicule de rognage (le cœur)

/// Barre de rognage : bande de vignettes (pellicule) + zone conservée [in,out] éclairée alors que
/// les extrémités exclues sont assombries (MÊME masque que le rognage d'image), poignées blanches
/// « façon outil sélectionné », tête de lecture. Toute la géométrie est en points-vue ; les temps
/// sont convertis via `x(for:)` / `time(for:)`. Origine bas-gauche (vue non retournée).
final class TrimTrackView: NSView {

    // État piloté par le contrôleur.
    var duration: Double = 0 { didSet { needsDisplay = true } }
    var inTime: Double = 0 { didSet { refreshCursors(); needsDisplay = true } }
    var outTime: Double = 0 { didSet { refreshCursors(); needsDisplay = true } }
    var playhead: Double = 0 { didSet { needsDisplay = true } }
    var minDur: Double = 0.3
    var isExporting = false { didSet { needsDisplay = true } }
    var exportProgress: Float = 0 { didSet { if isExporting { needsDisplay = true } } }

    // Rappels d'interaction (le contrôleur applique + seek).
    var onScrubIn: ((Double) -> Void)?
    var onScrubOut: ((Double) -> Void)?
    var onScrubPlayhead: ((Double) -> Void)?
    var onEditingBegan: (() -> Void)?
    var onEditingEnded: (() -> Void)?
    // Clavier (la vue devient 1er répondant au clic).
    var onTogglePlay: (() -> Void)?
    var onStep: ((_ forward: Bool, _ coarse: Bool) -> Void)?
    var onSetIn: (() -> Void)?
    var onSetOut: (() -> Void)?
    var onCommitDone: (() -> Void)?

    // Pellicule (génélà async, cache par index).
    var assetURL: URL?
    var assetAspect: CGFloat = 1.6
    private var frames: [Int: NSImage] = [:]
    private var frameCount = 0
    private var generator: AVAssetImageGenerator?
    private var widthBucket = -1
    private var generation = 0               // jeton : ignore les images d'une génération périmée

    private let insetX: CGFloat = 9          // corners r6 → les extrémités du stade ne rognent pas la pellicule
    private var usableW: CGFloat { max(1, bounds.width - insetX * 2) }

    override var isFlipped: Bool { false }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    // MARK: Géométrie temps ↔ x

    private func x(for t: Double) -> CGFloat {
        insetX + CGFloat(t.clamped(0, max(duration, 0.0001)) / max(duration, 0.0001)) * usableW
    }
    private func time(for px: CGFloat) -> Double {
        Double((px - insetX) / usableW).clamped(0, 1) * duration
    }
    private func trackRect() -> NSRect { bounds.insetBy(dx: insetX, dy: 8) }

    // MARK: Pellicule

    override func layout() {
        super.layout()
        rebuildFilmstripIfNeeded()
    }
    override func setFrameSize(_ newSize: NSSize) { super.setFrameSize(newSize); needsDisplay = true }

    /// Force une régénération de la pellicule (après un rognage en place : durée changée mais largeur
    /// identique → le cache par bucket ne se relancerait pas tout seul).
    func reloadFilmstrip() {
        widthBucket = -1
        frames.removeAll()
        rebuildFilmstripIfNeeded()
    }

    /// (Re)génère la pellicule seulement quand la largeur change franchement (bucket ~40pt) → pas de
    /// régénération à chaque pixel de redimensionnement. Décodage HORS thread principal, cache par index.
    func rebuildFilmstripIfNeeded() {
        guard let assetURL, duration > 0.001 else { return }
        let r = trackRect()
        guard r.width > 20, r.height > 8 else { return }
        let bucket = Int(r.width / 40)
        guard bucket != widthBucket else { return }
        widthBucket = bucket

        generator?.cancelAllCGImageGeneration()
        frames.removeAll()

        let trackH = r.height
        let thumbW = max(24, trackH * assetAspect)
        let n = max(6, min(40, Int((r.width / thumbW).rounded())))
        frameCount = n

        let gen = AVAssetImageGenerator(asset: AVURLAsset(url: assetURL))
        gen.appliesPreferredTrackTransform = true
        gen.maximumSize = CGSize(width: thumbW * 2, height: trackH * 2)
        gen.requestedTimeToleranceBefore = CMTime(seconds: 0.6, preferredTimescale: 600)
        gen.requestedTimeToleranceAfter = CMTime(seconds: 0.6, preferredTimescale: 600)
        generator = gen

        generation += 1
        let gen_id = generation
        let dur = duration
        let times = (0..<n).map { NSValue(time: CMTime(seconds: dur * (Double($0) + 0.5) / Double(n),
                                                        preferredTimescale: 600)) }
        gen.generateCGImagesAsynchronously(forTimes: times) { [weak self] requested, cg, _, result, _ in
            guard result == .succeeded, let cg else { return }
            let img = NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
            DispatchQueue.main.async {
                // Ignore les callbacks d'une génération périmée (même `n` possible après un rognage :
                // des vignettes de l'ANCIENNE vidéo se glissaient dans la nouvelle pellicule).
                guard let self, self.generation == gen_id else { return }
                let idx = Int((requested.seconds / max(dur, 0.0001) * Double(n) - 0.5).rounded())
                self.frames[max(0, min(n - 1, idx))] = img
                self.needsDisplay = true
            }
        }
    }

    // MARK: Rendu

    override func draw(_ dirtyRect: NSRect) {
        let r = trackRect()
        guard duration > 0 else {
            flInk.setFill(); r.fill()
            return
        }

        NSGraphicsContext.saveGraphicsState()
        let clip = NSBezierPath(roundedRect: r, xRadius: 6, yRadius: 6)
        clip.addClip()

        // 1. Fond charbon (les cellules non chargées ne clignotent pas).
        flInk.setFill(); r.fill()

        // 2. Pellicule bord-à-bord (remplissage « cover » de chaque colonne).
        if frameCount > 0 {
            let cw = r.width / CGFloat(frameCount)
            for i in 0..<frameCount {
                guard let img = frames[i] else { continue }
                let cell = NSRect(x: r.minX + CGFloat(i) * cw, y: r.minY, width: cw.rounded(.up) + 1, height: r.height)
                drawCover(img, in: cell)
            }
        }

        // 3. Zone conservée [in,out] : LANGAGE EXACT de drawCropMask (assombrit tout autour + cadre blanc).
        let keep = NSRect(x: x(for: inTime), y: r.minY, width: x(for: outTime) - x(for: inTime), height: r.height)
        NSColor.black.withAlphaComponent(0.45).setFill()
        let mask = NSBezierPath(rect: r)
        mask.append(NSBezierPath(rect: keep))
        mask.windingRule = .evenOdd
        mask.fill()
        NSColor.white.setStroke()
        let frame = NSBezierPath(rect: keep); frame.lineWidth = 1; frame.stroke()

        // 4. Lavis d'export : la zone conservée se remplit de blanc selon la progression.
        if isExporting {
            NSColor.white.withAlphaComponent(0.55).setFill()
            NSRect(x: keep.minX, y: keep.minY, width: keep.width * CGFloat(exportProgress), height: keep.height).fill()
        }

        // 5. Tête de lecture : trait fin (≠ des poignées épaisses).
        if !isExporting {
            let px = x(for: playhead)
            NSColor.white.setStroke()
            let ph = NSBezierPath()
            ph.move(to: NSPoint(x: px, y: r.minY)); ph.line(to: NSPoint(x: px, y: r.maxY))
            ph.lineWidth = 2; ph.stroke()
            NSColor.white.withAlphaComponent(0.97).setFill()
            NSBezierPath(ovalIn: NSRect(x: px - 3.5, y: r.maxY - 3.5, width: 7, height: 7)).fill()
        }

        NSGraphicsContext.restoreGraphicsState()

        // 6. Poignées (hors clip → débordent ~3pt en haut/bas). Remplissage « outil sélectionné » (blanc 0.97).
        if !isExporting {
            drawHandle(atX: x(for: inTime), in: r)
            drawHandle(atX: x(for: outTime), in: r)
        }
    }

    private func drawHandle(atX cx: CGFloat, in r: NSRect) {
        let w: CGFloat = 11
        let cap = NSRect(x: cx - w / 2, y: r.minY - 3, width: w, height: r.height + 6)
        NSColor.white.withAlphaComponent(0.97).setFill()
        NSBezierPath(roundedRect: cap, xRadius: 6, yRadius: 6).fill()
        // Deux traits de grip (couleur glyphe « outil sélectionné » : blanc 0.10).
        NSColor(white: 0.10, alpha: 1).setStroke()
        for dx in [-2.0, 2.0] {
            let g = NSBezierPath()
            g.move(to: NSPoint(x: cx + dx, y: cap.midY - 5))
            g.line(to: NSPoint(x: cx + dx, y: cap.midY + 5))
            g.lineWidth = 1; g.stroke()
        }
    }

    /// Dessine `img` en « cover » (remplit `cell`, recadré centré) — vue non retournée → `respectFlipped`.
    private func drawCover(_ img: NSImage, in cell: NSRect) {
        let s = img.size
        guard s.width > 0, s.height > 0, cell.height > 0 else { return }
        let ar = cell.width / cell.height
        var src = NSRect(origin: .zero, size: s)
        if s.width / s.height > ar {
            let cw = s.height * ar
            src = NSRect(x: (s.width - cw) / 2, y: 0, width: cw, height: s.height)
        } else {
            let ch = s.width / ar
            src = NSRect(x: 0, y: (s.height - ch) / 2, width: s.width, height: ch)
        }
        img.draw(in: cell, from: src, operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
    }

    // MARK: Souris

    private enum Drag { case none, dragIn, dragOut, scrub }
    private var drag: Drag = .none

    override func mouseDown(with event: NSEvent) {
        guard duration > 0, !isExporting else { return }
        window?.makeFirstResponder(self)
        let px = convert(event.locationInWindow, from: nil).x
        let li = x(for: inTime), lo = x(for: outTime)
        if abs(px - li) <= 8 { drag = .dragIn }
        else if abs(px - lo) <= 8 { drag = .dragOut }
        else { drag = .scrub }
        onEditingBegan?()
        handleDrag(px)
    }
    override func mouseDragged(with event: NSEvent) {
        guard drag != .none else { return }
        handleDrag(convert(event.locationInWindow, from: nil).x)
    }
    override func mouseUp(with event: NSEvent) {
        guard drag != .none else { return }
        drag = .none
        onEditingEnded?()
    }

    private func handleDrag(_ px: CGFloat) {
        let t = time(for: px)
        switch drag {
        case .dragIn:  onScrubIn?(t.clamped(0, outTime - minDur))
        case .dragOut: onScrubOut?(t.clamped(inTime + minDur, duration))
        case .scrub:   onScrubPlayhead?(t)
        case .none:    break
        }
    }

    // MARK: Curseur redimensionnement sur les poignées

    private func refreshCursors() { window?.invalidateCursorRects(for: self) }
    override func resetCursorRects() {
        guard duration > 0 else { return }
        for cx in [x(for: inTime), x(for: outTime)] {
            addCursorRect(NSRect(x: cx - 8, y: 0, width: 16, height: bounds.height), cursor: .resizeLeftRight)
        }
    }

    // MARK: Clavier (best-effort une fois la vue focalisée)

    override func keyDown(with event: NSEvent) {
        // Pendant un export, le transport est verrouillé (boutons grisés, souris ignorée) : le clavier
        // aussi — sinon Espace lançait la lecture d'un lecteur sans item (« Done ») qui repartait seule
        // au rechargement, et I/O déplaçaient les poignées pendant l'export.
        guard !isExporting else { super.keyDown(with: event); return }
        switch event.keyCode {
        case 49: onTogglePlay?()                                                   // Espace
        case 123: onStep?(false, event.modifierFlags.contains(.shift))             // ←
        case 124: onStep?(true, event.modifierFlags.contains(.shift))              // →
        case 36: onCommitDone?()                                                   // Entrée
        default:
            switch event.charactersIgnoringModifiers?.lowercased() {
            case "i": onSetIn?()          // point d'entrée à la tête de lecture (fonctionne en AZERTY : lit le caractère)
            case "o": onSetOut?()
            default: super.keyDown(with: event)
            }
        }
    }

    deinit { generator?.cancelAllCGImageGeneration() }
}

// MARK: - Exporteur (rognage + enregistrement)

/// Rogne [start,end] d'une source vidéo vers un fichier. Sans réencodage par défaut
/// (`AVAssetExportPresetPassthrough`) : SANS PERTE, ~10× plus rapide, conserve toutes les pistes
/// audio + l'orientation (`preferredTransform`) au bit près, taille identique. Repli AUTOMATIQUE et
/// UNIQUE en `HighestQuality` (réencodage) si le passthrough échoue (asset au conteneur incompatible).
/// La complétion et la progression sont livrées sur le thread principal.
final class VideoTrimExporter {
    enum TrimError: Error { case cannotCreateSession, failed }

    private var session: AVAssetExportSession?
    var isExporting: Bool { session != nil }
    var progress: Float { session?.progress ?? 0 }

    /// Type de fichier + extension temporaire à partir de l'extension SOURCE (un .mp4 reste un vrai .mp4).
    static func fileType(forExt ext: String) -> (AVFileType, String) {
        switch ext.lowercased() {
        case "mp4": return (.mp4, "mp4")
        case "m4v": return (.m4v, "m4v")
        default:    return (.mov, "mov")
        }
    }
    static func contentTypes(forExt ext: String) -> [UTType] {
        switch ext.lowercased() {
        case "mp4": return [.mpeg4Movie]
        case "m4v": return [.mpeg4Movie]
        default:    return [.quickTimeMovie]
        }
    }

    /// Exporte vers `dest` (qui NE doit PAS exister). `completion` sur le main.
    func export(source: URL, range: CMTimeRange, to dest: URL, fileType: AVFileType,
                completion: @escaping (Result<Void, Error>) -> Void) {
        run(source: source, range: range, dest: dest, fileType: fileType,
            preset: AVAssetExportPresetPassthrough, allowRetry: true, completion: completion)
    }

    private func run(source: URL, range: CMTimeRange, dest: URL, fileType: AVFileType,
                     preset: String, allowRetry: Bool,
                     completion: @escaping (Result<Void, Error>) -> Void) {
        let asset = AVURLAsset(url: source)
        guard let s = AVAssetExportSession(asset: asset, presetName: preset) else {
            DispatchQueue.main.async { completion(.failure(TrimError.cannotCreateSession)) }
            return
        }
        s.outputURL = dest
        s.outputFileType = fileType
        s.timeRange = range
        s.shouldOptimizeForNetworkUse = true
        session = s
        s.exportAsynchronously { [weak self] in
            let status = s.status
            let err = s.error
            DispatchQueue.main.async {
                guard let self else {
                    // Exporteur libéré (éditeur fermé / autre capture sélectionnée → `cancel()` du deinit) :
                    // personne ne nettoiera la sortie partielle (fichier caché dans le dossier de captures).
                    if status != .completed { try? FileManager.default.removeItem(at: dest) }
                    return
                }
                switch status {
                case .completed:
                    self.session = nil
                    completion(.success(()))
                case .failed where allRetryEligible(allowRetry, preset):
                    try? FileManager.default.removeItem(at: dest)
                    self.run(source: source, range: range, dest: dest, fileType: fileType,
                             preset: AVAssetExportPresetHighestQuality, allowRetry: false, completion: completion)
                default:
                    try? FileManager.default.removeItem(at: dest)
                    self.session = nil
                    completion(.failure(err ?? TrimError.failed))
                }
            }
        }
    }

    func cancel() {
        session?.cancelExport()
        session = nil
    }
}

/// Passthrough échoué et repli encore autorisé → on retente en réencodage.
private func allRetryEligible(_ allowRetry: Bool, _ preset: String) -> Bool {
    allowRetry && preset == AVAssetExportPresetPassthrough
}

// MARK: - Contrôleur

/// Éditeur de rognage complet, hébergeable dans le détail (comme `MarkupEditorViewController`) OU dans
/// une fenêtre flottante. Même contrat public : `onDone` / `defaultDone`.
final class VideoTrimmerViewController: NSViewController {

    private let url: URL
    private let player = AVPlayer()
    private let playerView = PlayerLayerView()
    private let track = TrimTrackView()
    private let exporter = VideoTrimExporter()

    private var playBtn: EditorToolButton!
    private var muteBtn: EditorToolButton!
    private var loopBtn: EditorToolButton!
    private var copyBtn: EditorPillButton!
    private var doneBtn: EditorPillButton!
    private var transportButtons: [EditorToolButton] = []
    private let curLabel = NSTextField(labelWithString: "0:00,00")
    private let durLabel = NSTextField(labelWithString: "0:00,00")

    private var duration: Double = 0
    private var inTime: Double = 0
    private var outTime: Double = 0
    private var frameRate: Double = 30
    private var isLooping = true

    private var timeObserver: Any?
    private var isSeeking = false
    private var pendingSeek: Double?
    private var progressTimer: Timer?
    private var winObserver: NSObjectProtocol?
    private var endObserver: NSObjectProtocol?

    /// Fenêtre flottante : appelé après « Done » → ferme. Inerte quand embarqué dans le détail.
    var onDone: (() -> Void)?
    /// Si vrai, Entrée déclenche « Done » (bouton par défaut).
    var defaultDone = false

    init(url: URL) {
        self.url = url
        super.init(nibName: nil, bundle: nil)
        player.replaceCurrentItem(with: AVPlayerItem(url: url))
    }
    required init?(coder: NSCoder) { fatalError("no coder") }

    // MARK: Cycle de vie

    override func loadView() {
        let root = NSView(frame: NSRect(x: 0, y: 0, width: 960, height: 640))
        root.wantsLayer = true
        root.layer?.backgroundColor = flInk.cgColor
        self.view = root
        buildUI(on: root)
        playerView.player = player
        installTimeObserver()
        wireTrackCallbacks()
        // Stoppe la lecture si la fenêtre hôte se ferme (fenêtre principale persistante, cf. VideoPlayerHost).
        winObserver = NotificationCenter.default.addObserver(
            forName: NSWindow.willCloseNotification, object: nil, queue: .main) { [weak self] note in
            guard let self, let w = note.object as? NSWindow, w == self.view.window else { return }
            self.player.pause()
        }
        // Fin de l'item : avec la sélection par défaut (out = durée), le lecteur s'arrête de lui-même à la
        // fin du fichier (rate → 0) AVANT que l'observateur périodique ne voie `t >= out` avec rate > 0 →
        // la boucle ne bouclait jamais et l'icône restait sur « pause » alors que la lecture était finie.
        endObserver = NotificationCenter.default.addObserver(
            forName: AVPlayerItem.didPlayToEndTimeNotification, object: nil, queue: .main) { [weak self] note in
            MainActor.assumeIsolated {
                guard let self, let item = note.object as? AVPlayerItem, item === self.player.currentItem else { return }
                if self.isLooping {
                    self.seekPrecise(self.inTime)
                    self.player.play()
                }
                self.updatePlayIcon()
            }
        }
        Task { await loadAsset() }
    }

    private func buildUI(on content: NSView) {
        // ===== BARRE DU HAUT (transport + actions) — capsule charbon flottante, centrée =====
        let toolStack = NSStackView()
        toolStack.orientation = .horizontal
        toolStack.spacing = 3
        toolStack.alignment = .centerY

        let restartBtn = EditorToolButton(symbol: "backward.end.fill")
        restartBtn.toolTip = "Revenir au début"; restartBtn.target = self; restartBtn.action = #selector(restartAction)
        playBtn = EditorToolButton(symbol: "play.fill")
        playBtn.toolTip = "Lecture"; playBtn.target = self; playBtn.action = #selector(togglePlayAction)
        muteBtn = EditorToolButton(symbol: "speaker.wave.2.fill")
        muteBtn.toolTip = "Couper le son"; muteBtn.target = self; muteBtn.action = #selector(toggleMuteAction)
        transportButtons = [restartBtn, playBtn, muteBtn]
        toolStack.addArrangedSubview(restartBtn)
        toolStack.addArrangedSubview(playBtn)
        toolStack.addArrangedSubview(muteBtn)
        toolStack.addArrangedSubview(makeSeparator())

        loopBtn = EditorToolButton(symbol: "repeat")
        loopBtn.toolTip = "Lire en boucle"; loopBtn.target = self; loopBtn.action = #selector(toggleLoopAction)
        loopBtn.isSelectedTool = true                       // boucle ON par défaut → chrome « outil sélectionné »
        transportButtons.append(loopBtn)
        toolStack.addArrangedSubview(loopBtn)
        toolStack.addArrangedSubview(makeSeparator())

        let moreBtn = EditorToolButton(symbol: "ellipsis")
        moreBtn.toolTip = "Plus"; moreBtn.target = self; moreBtn.action = #selector(showMoreMenu(_:))
        transportButtons.append(moreBtn)
        toolStack.addArrangedSubview(moreBtn)
        toolStack.addArrangedSubview(makeSeparator())

        copyBtn = EditorPillButton(title: "Copy", symbol: "doc.on.doc", primary: false)
        copyBtn.target = self; copyBtn.action = #selector(copyAction)
        doneBtn = EditorPillButton(title: "Done", symbol: "checkmark", primary: true)
        doneBtn.target = self; doneBtn.action = #selector(doneAction)
        toolStack.addArrangedSubview(copyBtn)
        toolStack.addArrangedSubview(doneBtn)

        applySeparatorSpacing(toolStack, 7)
        let topCapsule = makeCapsule(toolStack, height: 42, radius: 21)

        // ===== BARRE DU BAS (pellicule de rognage) =====
        curLabel.alignment = .right
        durLabel.alignment = .left
        for l in [curLabel, durLabel] {
            l.font = .monospacedDigitSystemFont(ofSize: 11.5, weight: .medium)
            l.textColor = NSColor.white.withAlphaComponent(0.78)
            l.widthAnchor.constraint(equalToConstant: 52).isActive = true
        }
        track.translatesAutoresizingMaskIntoConstraints = false
        track.heightAnchor.constraint(equalToConstant: 44).isActive = true
        track.setContentHuggingPriority(.defaultLow, for: .horizontal)
        track.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        let bottomStack = NSStackView(views: [curLabel, makeSeparator(), track, makeSeparator(), durLabel])
        bottomStack.orientation = .horizontal
        bottomStack.spacing = 5
        bottomStack.alignment = .centerY
        bottomStack.distribution = .fill
        applySeparatorSpacing(bottomStack, 9)
        let bottomCapsule = makeCapsule(bottomStack, height: 60, radius: 30)

        content.addSubview(playerView)
        content.addSubview(topCapsule)
        content.addSubview(bottomCapsule)
        playerView.translatesAutoresizingMaskIntoConstraints = false

        let bottomWidth = bottomCapsule.widthAnchor.constraint(equalTo: content.widthAnchor, constant: -48)
        bottomWidth.priority = .defaultHigh
        let bottomMax = bottomCapsule.widthAnchor.constraint(lessThanOrEqualToConstant: 980)

        NSLayoutConstraint.activate([
            topCapsule.centerXAnchor.constraint(equalTo: content.centerXAnchor),
            topCapsule.topAnchor.constraint(equalTo: content.topAnchor, constant: 14),

            bottomCapsule.centerXAnchor.constraint(equalTo: content.centerXAnchor),
            bottomCapsule.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -12),
            bottomCapsule.leadingAnchor.constraint(greaterThanOrEqualTo: content.leadingAnchor, constant: 24),
            bottomCapsule.trailingAnchor.constraint(lessThanOrEqualTo: content.trailingAnchor, constant: -24),
            bottomWidth, bottomMax,

            playerView.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            playerView.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            playerView.topAnchor.constraint(equalTo: topCapsule.bottomAnchor, constant: 12),
            playerView.bottomAnchor.constraint(equalTo: bottomCapsule.topAnchor, constant: -10),
        ])
    }

    private func installTimeObserver() {
        timeObserver = player.addPeriodicTimeObserver(
            forInterval: CMTime(seconds: 1.0 / 30, preferredTimescale: 600), queue: .main) { [weak self] time in
            guard let self else { return }
            let t = time.seconds
            self.track.playhead = t
            self.curLabel.stringValue = self.fmt(t)
            if self.player.rate > 0, t >= self.outTime - 0.001 {
                if self.isLooping { self.seekPrecise(self.inTime) }
                else { self.player.pause(); self.updatePlayIcon(); self.seekPrecise(self.outTime) }
            }
        }
    }

    private func wireTrackCallbacks() {
        track.onEditingBegan = { [weak self] in
            guard let self else { return }
            self.player.pause(); self.updatePlayIcon()
        }
        track.onScrubIn = { [weak self] t in
            guard let self else { return }
            self.inTime = t; self.track.inTime = t; self.seekPrecise(t)
        }
        track.onScrubOut = { [weak self] t in
            guard let self else { return }
            self.outTime = t; self.track.outTime = t
            self.seekPrecise(max(self.inTime, t - 1.0 / self.frameRate))   // montre la dernière image conservée
        }
        track.onScrubPlayhead = { [weak self] t in self?.seekPrecise(t) }
        track.onTogglePlay = { [weak self] in self?.togglePlayAction() }
        track.onStep = { [weak self] fwd, coarse in
            guard let self else { return }
            let step = coarse ? 1.0 : 1.0 / self.frameRate
            self.seekPrecise((self.player.currentTime().seconds + (fwd ? step : -step)).clamped(0, self.duration))
        }
        track.onSetIn = { [weak self] in
            guard let self else { return }
            let t = self.player.currentTime().seconds.clamped(0, self.outTime - self.track.minDur)
            self.inTime = t; self.track.inTime = t
        }
        track.onSetOut = { [weak self] in
            guard let self else { return }
            let t = self.player.currentTime().seconds.clamped(self.inTime + self.track.minDur, self.duration)
            self.outTime = t; self.track.outTime = t
        }
        track.onCommitDone = { [weak self] in if self?.defaultDone == true { self?.doneAction() } }
    }

    private func loadAsset() async {
        let asset = AVURLAsset(url: url)
        do {
            let dur = try await asset.load(.duration)
            let seconds = dur.seconds.isFinite ? dur.seconds : 0
            guard seconds > 0.05 else { GlassConfirmBadge.show("Vidéo illisible", symbol: "exclamationmark.triangle.fill"); return }
            let videoTracks = try await asset.loadTracks(withMediaType: .video)
            var aspect: CGFloat = 1.6
            var fps: Double = 30
            if let vt = videoTracks.first {
                let (size, transform, rate) = try await vt.load(.naturalSize, .preferredTransform, .nominalFrameRate)
                let s = size.applying(transform)
                let w = abs(s.width), h = abs(s.height)
                if w > 0, h > 0 { aspect = max(0.2, min(2.2, w / h)) }
                // Cadence NOMINALE = nb d'images / durée : un enregistrement d'écran (cadence variable, SCK
                // n'écrit une image que si l'écran change) peut annoncer 0,5 i/s → pas d'1 image = 2 s et
                // durée mini = 10/fps = 20 s > durée de la vidéo : les poignées ne bougeaient plus.
                if rate > 0 { fps = Double(rate).clamped(10, 240) }
            }
            applyLoaded(duration: seconds, aspect: aspect, fps: fps)
        } catch {
            GlassConfirmBadge.show("Vidéo illisible", symbol: "exclamationmark.triangle.fill")
        }
    }

    private func applyLoaded(duration: Double, aspect: CGFloat, fps: Double) {
        self.duration = duration
        self.frameRate = fps
        self.inTime = 0
        self.outTime = duration
        durLabel.stringValue = fmt(duration)
        curLabel.stringValue = fmt(0)
        track.assetURL = url
        track.assetAspect = aspect
        track.minDur = min(max(0.30, 10.0 / fps), duration)   // jamais plus long que la vidéo (sinon out > durée)
        track.duration = duration
        track.inTime = 0
        track.outTime = duration
        track.playhead = 0
        track.needsLayout = true
        track.reloadFilmstrip()          // force (durée changée après un rognage → bucket identique)
        track.needsDisplay = true
        seekPrecise(0)
    }

    // MARK: Transport

    private func seekPrecise(_ t: Double) {
        let target = t.clamped(0, duration)
        guard !isSeeking else { pendingSeek = target; return }
        isSeeking = true
        player.seek(to: CMTime(seconds: target, preferredTimescale: 600),
                    toleranceBefore: .zero, toleranceAfter: .zero) { [weak self] _ in
            guard let self else { return }
            self.isSeeking = false
            if let p = self.pendingSeek { self.pendingSeek = nil; self.seekPrecise(p) }
        }
    }

    private func updatePlayIcon() {
        let playing = player.rate > 0
        let cfg = NSImage.SymbolConfiguration(pointSize: 13, weight: .medium)
        let img = NSImage(systemSymbolName: playing ? "pause.fill" : "play.fill", accessibilityDescription: nil)?
            .withSymbolConfiguration(cfg)
        img?.isTemplate = true
        playBtn.image = img
        playBtn.toolTip = playing ? "Pause" : "Lecture"
    }

    @objc private func togglePlayAction() {
        guard duration > 0 else { return }
        if player.rate > 0 {
            player.pause()
        } else {
            let ph = player.currentTime().seconds
            if ph >= outTime - 0.01 || ph < inTime { seekPrecise(inTime) }
            player.play()
        }
        updatePlayIcon()
    }
    @objc private func restartAction() { seekPrecise(inTime) }
    @objc private func toggleMuteAction() {
        player.isMuted.toggle()
        let cfg = NSImage.SymbolConfiguration(pointSize: 13, weight: .medium)
        let img = NSImage(systemSymbolName: player.isMuted ? "speaker.slash.fill" : "speaker.wave.2.fill",
                          accessibilityDescription: nil)?.withSymbolConfiguration(cfg)
        img?.isTemplate = true
        muteBtn.image = img
        muteBtn.toolTip = player.isMuted ? "Activer le son" : "Couper le son"
    }
    @objc private func toggleLoopAction() {
        loopBtn.isSelectedTool.toggle()
        isLooping = loopBtn.isSelectedTool
    }

    // MARK: Menu « … »

    @objc private func showMoreMenu(_ sender: NSButton) {
        let menu = NSMenu()
        let copy = menu.addItem(withTitle: "Enregistrer une copie…", action: #selector(saveCopyAction), keyEquivalent: "")
        copy.image = NSImage(systemSymbolName: "square.and.arrow.down", accessibilityDescription: nil)
        let frame = menu.addItem(withTitle: "Enregistrer l'image courante", action: #selector(saveCurrentFrame), keyEquivalent: "")
        frame.image = NSImage(systemSymbolName: "camera", accessibilityDescription: nil)
        menu.addItem(.separator())
        let reset = menu.addItem(withTitle: "Réinitialiser la sélection", action: #selector(resetSelection), keyEquivalent: "")
        reset.image = NSImage(systemSymbolName: "arrow.uturn.backward", accessibilityDescription: nil)
        reset.isEnabled = inTime > 0.001 || outTime < duration - 0.001
        for it in menu.items { it.target = self }
        let origin = NSPoint(x: 0, y: sender.bounds.maxY + 6)
        menu.popUp(positioning: nil, at: origin, in: sender)
    }

    @objc private func resetSelection() {
        inTime = 0; outTime = duration
        track.inTime = 0; track.outTime = duration
        seekPrecise(0)
    }

    // MARK: Sélection validée

    private func validatedRange() -> CMTimeRange? {
        let lo = max(0, inTime), hi = min(duration, outTime)
        guard hi - lo >= 0.1 else { return nil }
        return CMTimeRange(start: CMTime(seconds: lo, preferredTimescale: 600),
                           end: CMTime(seconds: hi, preferredTimescale: 600))
    }

    // MARK: État export (verrouillage UI + lavis de progression)

    private func beginExportUI() {
        player.pause(); updatePlayIcon()
        track.exportProgress = 0
        track.isExporting = true
        setControlsEnabled(false)
        progressTimer?.invalidate()
        progressTimer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in
            guard let self else { return }
            self.track.exportProgress = self.exporter.progress
        }
    }
    private func endExportUI() {
        progressTimer?.invalidate(); progressTimer = nil
        track.isExporting = false
        setControlsEnabled(true)
    }
    private func setControlsEnabled(_ on: Bool) {
        for b in transportButtons { b.isEnabled = on }
        copyBtn.isEnabled = on
        doneBtn.isEnabled = on
    }

    // MARK: Done — enregistre l'extrait PAR-DESSUS l'original

    @objc private func doneAction() {
        guard !exporter.isExporting else { return }
        guard let range = validatedRange() else {
            GlassConfirmBadge.show("Sélection trop courte", symbol: "exclamationmark.triangle.fill"); return
        }
        // Libère le fichier AVANT le remplacement atomique (sinon replaceItemAt échoue / garde l'ancien inode).
        player.replaceCurrentItem(with: nil)
        playerView.player = nil

        let ext = url.pathExtension
        let (fileType, tmpExt) = VideoTrimExporter.fileType(forExt: ext)
        // Temp CACHÉ dans le MÊME dossier (même volume → remplacement atomique ; ignoré par le watcher car .fichier).
        let tmp = url.deletingLastPathComponent().appendingPathComponent(".\(UUID().uuidString).\(tmpExt)")
        beginExportUI()
        exporter.export(source: url, range: range, to: tmp, fileType: fileType) { [weak self] result in
            guard let self else { return }
            self.endExportUI()
            switch result {
            case .success:
                do {
                    _ = try FileManager.default.replaceItemAt(self.url, withItemAt: tmp,
                                                              backupItemName: nil, options: [.usingNewMetadataOnly])
                } catch {
                    try? FileManager.default.removeItem(at: tmp)
                    self.reattachPlayer(seekTo: self.inTime)
                    GlassConfirmBadge.show("Échec de l'enregistrement", symbol: "exclamationmark.triangle.fill")
                    return
                }
                CaptureLibrary.shared.reloadFile(self.url)
                self.reloadAfterTrim()
                GlassConfirmBadge.show("Enregistré", symbol: "checkmark.circle.fill")
                self.onDone?()
            case .failure:
                self.reattachPlayer(seekTo: self.inTime)
                GlassConfirmBadge.show("Échec de l'export", symbol: "exclamationmark.triangle.fill")
            }
        }
    }

    /// Reconstruit le lecteur sur le fichier courant. `seekTo` : position à restaurer (échec du rognage) ;
    /// nil pour un item neuf laissé à 0 (rechargement après rognage réussi → `loadAsset` repositionne).
    private func reattachPlayer(seekTo: Double? = nil) {
        player.replaceCurrentItem(with: AVPlayerItem(url: url))
        playerView.player = player
        updatePlayIcon()
        if let s = seekTo { seekPrecise(s) }
    }

    /// Après un rognage réussi en place : le fichier est désormais l'extrait → recharge tout (durée, pellicule).
    private func reloadAfterTrim() {
        reattachPlayer()
        track.assetURL = url
        Task { await loadAsset() }
    }

    // MARK: Enregistrer une copie…

    @objc private func saveCopyAction() {
        guard !exporter.isExporting, let range = validatedRange() else {
            if !exporter.isExporting { GlassConfirmBadge.show("Sélection trop courte", symbol: "exclamationmark.triangle.fill") }
            return
        }
        let ext = url.pathExtension
        let (fileType, tmpExt) = VideoTrimExporter.fileType(forExt: ext)
        let panel = NSSavePanel()
        panel.allowedContentTypes = VideoTrimExporter.contentTypes(forExt: ext)
        panel.nameFieldStringValue = url.deletingPathExtension().lastPathComponent + " extrait." + tmpExt
        panel.begin { [weak self] resp in
            guard let self, resp == .OK, let dest = panel.url else { return }
            // Le panneau n'est pas modal : un « Done »/« Copy » a pu démarrer entre-temps. Deux exports
            // simultanés se partageaient `session` (progression fausse, UI déverrouillée trop tôt).
            guard !self.exporter.isExporting else { return }
            try? FileManager.default.removeItem(at: dest)              // l'utilisateur a pu confirmer un écrasement
            self.beginExportUI()
            self.exporter.export(source: self.url, range: range, to: dest, fileType: fileType) { result in
                self.endExportUI()
                switch result {
                case .success:
                    if dest.deletingLastPathComponent().standardizedFileURL
                        == Preferences.shared.saveDirectory.standardizedFileURL {
                        CaptureLibrary.shared.add(dest)               // copie dans le dossier de captures → apparaît + sélectionnée
                    }
                    GlassConfirmBadge.show("Enregistré", symbol: "checkmark.circle.fill")
                case .failure:
                    GlassConfirmBadge.show("Échec de l'export", symbol: "exclamationmark.triangle.fill")
                }
            }
        }
    }

    // MARK: Copy — copie l'EXTRAIT (fichier) dans le presse-papiers

    @objc private func copyAction() {
        guard !exporter.isExporting, let range = validatedRange() else {
            if !exporter.isExporting { GlassConfirmBadge.show("Sélection trop courte", symbol: "exclamationmark.triangle.fill") }
            return
        }
        let ext = url.pathExtension
        let (fileType, tmpExt) = VideoTrimExporter.fileType(forExt: ext)
        // Hors du dossier surveillé → NSTemporaryDirectory (sinon le watcher l'ingérerait comme une capture).
        let tmp = FileManager.default.temporaryDirectory
            .appendingPathComponent("FrameLine-\(UUID().uuidString).\(tmpExt)")
        beginExportUI()
        exporter.export(source: url, range: range, to: tmp, fileType: fileType) { [weak self] result in
            guard let self else { return }
            self.endExportUI()
            switch result {
            case .success:
                let pb = NSPasteboard.general
                pb.clearContents()
                pb.writeObjects([tmp as NSURL])
                GlassConfirmBadge.show("Copié")
            case .failure:
                GlassConfirmBadge.show("Échec de l'export", symbol: "exclamationmark.triangle.fill")
            }
        }
    }

    // MARK: Enregistrer l'image courante (PNG de la tête de lecture)

    @objc private func saveCurrentFrame() {
        let at = CMTime(seconds: player.currentTime().seconds, preferredTimescale: 600)
        let srcURL = url
        Task.detached(priority: .userInitiated) {
            let gen = AVAssetImageGenerator(asset: AVURLAsset(url: srcURL))
            gen.appliesPreferredTrackTransform = true
            guard let cg = try? gen.copyCGImage(at: at, actualTime: nil) else {
                await MainActor.run { GlassConfirmBadge.show("Image indisponible", symbol: "exclamationmark.triangle.fill") }
                return
            }
            let rep = NSBitmapImageRep(cgImage: cg)
            let data = rep.representation(using: .png, properties: [:])
            await MainActor.run {
                guard let data else { return }
                let panel = NSSavePanel()
                panel.allowedContentTypes = [.png]
                panel.nameFieldStringValue = srcURL.deletingPathExtension().lastPathComponent + " image.png"
                panel.begin { resp in
                    guard resp == .OK, let dest = panel.url else { return }
                    do { try data.write(to: dest); GlassConfirmBadge.show("Enregistré", symbol: "checkmark.circle.fill") }
                    catch { GlassConfirmBadge.show("Échec de l'enregistrement", symbol: "exclamationmark.triangle.fill") }
                }
            }
        }
    }

    // MARK: Format temps « m:ss,cc » (virgule française)

    private func fmt(_ s: Double) -> String {
        let total = max(0, s)
        let m = Int(total) / 60
        let sec = Int(total) % 60
        let cc = Int((total - floor(total)) * 100)
        return String(format: "%d:%02d,%02d", m, sec, cc)
    }

    deinit {
        if let o = timeObserver { player.removeTimeObserver(o) }
        if let w = winObserver { NotificationCenter.default.removeObserver(w) }
        if let e = endObserver { NotificationCenter.default.removeObserver(e) }
        progressTimer?.invalidate()
        exporter.cancel()
        player.pause()
        player.replaceCurrentItem(with: nil)
    }

    // MARK: Briques d'UI (copiées de l'éditeur d'image — elles y sont privées)

    private func makeSeparator() -> NSView {
        let v = BarSeparator()
        v.wantsLayer = true
        v.layer?.backgroundColor = NSColor.white.withAlphaComponent(0.16).cgColor
        v.translatesAutoresizingMaskIntoConstraints = false
        v.widthAnchor.constraint(equalToConstant: 1).isActive = true
        v.heightAnchor.constraint(equalToConstant: 19).isActive = true
        return v
    }
    private func applySeparatorSpacing(_ stack: NSStackView, _ gap: CGFloat) {
        let subs = stack.arrangedSubviews
        for (i, v) in subs.enumerated() where v is BarSeparator {
            if i > 0 { stack.setCustomSpacing(gap, after: subs[i - 1]) }
            stack.setCustomSpacing(gap, after: v)
        }
    }
    private func makeCapsule(_ stack: NSStackView, height: CGFloat, radius: CGFloat) -> NSView {
        let c = NSView()
        c.wantsLayer = true
        c.layer?.backgroundColor = NSColor(white: 0.16, alpha: 0.97).cgColor
        c.layer?.cornerRadius = radius
        c.layer?.borderWidth = 1
        c.layer?.borderColor = NSColor.white.withAlphaComponent(0.10).cgColor
        c.shadow = NSShadow()
        c.layer?.shadowColor = NSColor.black.cgColor
        c.layer?.shadowOpacity = 0.35
        c.layer?.shadowRadius = 12
        c.layer?.shadowOffset = NSSize(width: 0, height: -3)
        c.translatesAutoresizingMaskIntoConstraints = false
        stack.translatesAutoresizingMaskIntoConstraints = false
        c.addSubview(stack)
        NSLayoutConstraint.activate([
            c.heightAnchor.constraint(equalToConstant: height),
            stack.leadingAnchor.constraint(equalTo: c.leadingAnchor, constant: 12),
            stack.trailingAnchor.constraint(equalTo: c.trailingAnchor, constant: -12),
            stack.centerYAnchor.constraint(equalTo: c.centerYAnchor),
        ])
        return c
    }
}
