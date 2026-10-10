import AppKit
import QuartzCore

/// VIGNETTE FLOTTANTE POST-CAPTURE (façon BridgeShot, coin bas-gauche).
///
/// Après chaque capture, une petite carte (~240×170) apparaît en bas-à-gauche de
/// l'écran qui contient la souris, affiche l'image et propose des actions rapides
/// au survol. Elle SUIT la souris (un timer la repositionne sur l'écran courant)
/// pour rester sous les yeux de l'utilisateur multi-écrans, puis disparaît seule
/// au bout de ~6 s — délai annulé tant qu'on la survole, pour laisser le temps de
/// cliquer une action.
///
/// Une SEULE vignette vit à la fois : `present(...)` remplace la précédente.
/// Les actions délèguent toutes à `AppCoordinator.shared` (câblé par l'orchestrateur).

/// Actions de la barre flottante. `enhance` (« Améliorer ») retombe pour l'instant
/// sur l'annotation faute de pipeline dédié — voir `ThumbnailPanel.handle`.
private enum ThumbAction { case open, annotate, copy, enhance, pin, finder, trash, close }

// MARK: - Point d'entrée public

/// Contrôleur partagé : seul objet que l'orchestrateur manipule.
@MainActor
final class CaptureThumbnailController {
    static let shared = CaptureThumbnailController()
    private init() {}

    /// PILE de vignettes (façon BridgeShot) — index 0 = la PLUS RÉCENTE (coin bas-gauche), les plus
    /// anciennes s'alignent vers la droite. On en ACCUMULE jusqu'à `maxCount` ; au-delà, la plus ancienne
    /// est retirée.
    private var panels: [ThumbnailPanel] = []
    private let maxCount = 6
    private var anchorScreen: NSScreen?
    private var followTimer: Timer?

    /// Profondeur des « sessions de capture » ouvertes (overlay de zone + moteur screencapture s'imbriquent).
    /// > 0 → la pile est retirée de l'écran.
    private var captureDepth = 0
    private var isSuspended: Bool { captureDepth > 0 }

    /// Écran SOUS la souris à l'instant T (repli : écran principal).
    private func screenUnderMouse() -> NSScreen? {
        let loc = NSEvent.mouseLocation
        return NSScreen.screens.first { NSMouseInRect(loc, $0.frame, false) } ?? NSScreen.main
    }

    /// Ajoute une vignette pour `image`/`fileURL` à la pile (sans remplacer les précédentes).
    /// `isVideo` : la capture est un enregistrement (.mov) → carte adaptée (badge lecture + barre d'actions
    /// pertinente pour une vidéo, cf. `ThumbnailRootView`).
    func present(image: NSImage, fileURL: URL, isVideo: Bool = false) {
        anchorScreen = screenUnderMouse()

        let p = ThumbnailPanel(image: image, fileURL: fileURL, isVideo: isVideo)
        p.onClosed = { [weak self, weak p] in
            guard let self, let p else { return }
            self.panels.removeAll { $0 === p }
            self.relayout(animated: true)                 // les autres se resserrent vers le coin
            self.syncFollowing()                          // plus de vignette → on arrête le suivi
        }
        panels.insert(p, at: 0)                            // la nouvelle arrive au coin
        // Capture prise PENDANT une session (ex. screenshot en cours d'enregistrement vidéo) : la vignette
        // est positionnée mais reste hors écran jusqu'à la fin de la session, comme le reste de la pile.
        if isSuspended { p.setSuspended(true) }
        if panels.count > maxCount {
            panels.removeLast().dismissImmediately()       // au-delà de 6 : on retire la plus ancienne
        }
        relayout(animated: true, appearing: p)
        syncFollowing()                                    // (re)démarre le suivi de l'écran du curseur
    }

    /// La pile de vignettes SUIT l'écran du CURSEUR : tant qu'une vignette est visible, un timer
    /// vérifie l'écran sous la souris et, s'il change, déplace toute la pile au coin bas-gauche de
    /// ce nouvel écran (multi-écrans). S'arrête dès qu'il n'y a plus de vignette.
    private func syncFollowing() {
        if panels.isEmpty || !Preferences.shared.previewFollowsCursor {
            followTimer?.invalidate()
            followTimer = nil
        } else if followTimer == nil {
            // .common → continue de tirer même pendant le suivi d'événements (menus, drag…).
            let t = Timer(timeInterval: 0.2, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.followMouse() }   // timer sur la runloop main
            }
            RunLoop.main.add(t, forMode: .common)
            followTimer = t
        }
    }

    /// À appeler quand la préférence « la vignette suit le curseur » change (depuis les Réglages) :
    /// démarre/arrête le suivi à chaud, même si une vignette est déjà affichée.
    func followingPreferenceChanged() { syncFollowing() }

    /// À appeler quand la préférence « coin de l'aperçu » change (depuis les Réglages) : déplace
    /// immédiatement la pile vers le nouveau coin, même si une vignette est déjà affichée.
    func previewCornerChanged() { relayout(animated: true) }

    private func followMouse() {
        guard !panels.isEmpty, Preferences.shared.previewFollowsCursor else { syncFollowing(); return }
        // Glisser-déposer EN COURS depuis une vignette : le curseur qui passe sur l'autre écran n'est pas
        // un « changement d'écran » — sans cette garde, toute la pile filait au coin de l'écran visé et
        // pouvait se placer SOUS le point de dépôt (panneau flottant sans type accepté → dépôt raté).
        guard !panels.contains(where: { $0.isDraggingFile }) else { return }
        guard let s = screenUnderMouse() else { return }
        if s.es_displayID != anchorScreen?.es_displayID {   // le curseur a changé d'écran
            anchorScreen = s
            relayout(animated: true)                        // toute la pile glisse vers le nouvel écran
        }
    }

    /// Surcharge pratique quand l'appelant n'a que l'URL du fichier capturé.
    func present(fileURL: URL) {
        guard let img = NSImage(contentsOf: fileURL) else { return }
        present(image: img, fileURL: fileURL)
    }

    /// Retire la vignette correspondant à `url` (ex. écriture disque échouée → vignette orpheline).
    func dismiss(forFailedURL url: URL) {
        guard let p = panels.first(where: { $0.capturedURL == url }) else { return }
        panels.removeAll { $0 === p }
        p.dismissImmediately()
        relayout(animated: true)
        syncFollowing()
    }

    /// Empile les vignettes en COLONNE au coin choisi (Réglages → previewCorner) de l'écran d'ancrage :
    /// la plus récente est AU COIN, les anciennes s'empilent vers l'intérieur (vers le haut pour un coin
    /// bas, vers le bas pour un coin haut). `appearing` glisse depuis le bord horizontal le plus proche.
    private func relayout(animated: Bool, appearing: ThumbnailPanel? = nil) {
        guard let screen = anchorScreen ?? NSScreen.main else { return }
        let vf = screen.visibleFrame
        let margin: CGFloat = 19, gap: CGFloat = 12
        let cardH = ThumbnailPanel.cardHeight, cardW = ThumbnailPanel.cardWidth, pad = ThumbnailPanel.padding
        let corner = Preferences.shared.previewCorner
        let isRight = corner == "bottomRight" || corner == "topRight"
        let isTop   = corner == "topLeft"    || corner == "topRight"
        let x = isRight ? (vf.maxX - margin - cardW - pad) : (vf.minX + margin - pad)
        for (i, p) in panels.enumerated() {
            let stack = CGFloat(i) * (cardH + gap)                       // empilement VERTICAL vers l'intérieur
            let y = isTop ? (vf.maxY - margin - cardH - pad - stack)
                          : (vf.minY + margin - pad + stack)
            let origin = NSPoint(x: x, y: y)
            if p === appearing { p.appear(at: origin, fromRight: isRight) }
            else { p.relocate(to: origin, animated: animated) }
        }
    }

    // MARK: - Session de capture (la pile ne doit JAMAIS figurer sur la capture suivante)

    /// Ouvre une session de capture : la pile de vignettes est RETIRÉE de l'écran (sans être détruite ni
    /// oubliée) le temps de prendre la capture / l'enregistrement — sinon les vignettes déjà affichées se
    /// retrouvent gravées dans le screenshot suivant. Ré-entrant : `begin`/`end` s'appairent (l'overlay de
    /// sélection de zone ouvre sa session, puis `captureCocoaRect` en imbrique une seconde).
    ///
    /// Renvoie `true` si CET appel a effectivement fait disparaître des vignettes — l'appelant s'en sert
    /// pour laisser au serveur de fenêtres le temps de recomposer l'écran avant de déclencher la capture.
    @discardableResult
    func beginCaptureSession() -> Bool {
        captureDepth += 1
        guard captureDepth == 1, !panels.isEmpty else { return false }
        panels.forEach { $0.setSuspended(true) }
        return true
    }

    /// Referme une session de capture ; à la dernière, la pile revient à l'écran (les vignettes créées
    /// pendant la session — capture prise en cours d'enregistrement — apparaissent alors elles aussi).
    func endCaptureSession() {
        guard captureDepth > 0 else { return }
        captureDepth -= 1
        guard captureDepth == 0 else { return }
        panels.forEach { $0.setSuspended(false) }
    }
}

// MARK: - Fenêtre de la vignette

/// Panneau borderless flottant non-activant : il s'affiche par-dessus toutes les
/// fenêtres (y compris en plein écran) sans voler le focus de l'app de l'utilisateur.
private final class ThumbnailPanel: NSPanel {
    private let fileURL: URL
    /// URL de la capture affichée (pour qu'un appelant puisse cibler CETTE vignette).
    var capturedURL: URL { fileURL }
    /// Un glisser-déposer du fichier part de cette vignette (le contrôleur gèle alors le suivi d'écran).
    var isDraggingFile: Bool { rootView.isDraggingFile }
    private let rootView: ThumbnailRootView

    /// Appelé une fois la vignette réellement retirée de l'écran.
    var onClosed: (() -> Void)?

    private var dismissTimer: Timer?          // (auto-disparition désactivée — conservé en no-op)
    private var isDismissing = false          // garde-fou anti double-fermeture
    private var isSuspended = false           // retirée de l'écran le temps d'une capture (cf. setSuspended)

    static let cardWidth: CGFloat = 240       // taille VISIBLE de la carte (le contrôleur s'en sert pour la rangée)
    static let cardHeight: CGFloat = 170
    static let padding: CGFloat = 10          // marge TRANSPARENTE autour de la carte : la fenêtre ne se
                                              // redimensionne jamais ; l'agrandissement au survol se fait
                                              // par transform GPU de la carte DANS cette marge (fluide).
    private var cardSize: NSSize { NSSize(width: Self.cardWidth, height: Self.cardHeight) }
    private var pad: CGFloat { Self.padding }
    private var winSize: NSSize { NSSize(width: cardSize.width + pad * 2, height: cardSize.height + pad * 2) }

    init(image: NSImage, fileURL: URL, isVideo: Bool) {
        self.fileURL = fileURL
        self.rootView = ThumbnailRootView(image: image, fileURL: fileURL, isVideo: isVideo)
        super.init(contentRect: NSRect(x: 0, y: 0, width: 260, height: 190),   // = cardSize 240×170 + pad 10×2
                   styleMask: [.borderless, .nonactivatingPanel],
                   backing: .buffered, defer: false)

        isFloatingPanel = true
        becomesKeyOnlyIfNeeded = true
        level = .floating                                 // au-dessus des fenêtres normales
        backgroundColor = .clear                          // coins arrondis visibles (transparence)
        isOpaque = false
        hasShadow = true
        isMovableByWindowBackground = false               // repositionnée par le timer, pas à la main
        isReleasedWhenClosed = false                      // cycle de vie géré par le contrôleur + timers
        hidesOnDeactivate = false
        // Visible sur tous les Spaces et par-dessus le plein écran ; ignorée par le
        // cycleur de fenêtres (cmd-²) et par Exposé (reste « scotchée » en place).
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]

        // Conteneur TRANSPARENT à la taille (padée) de la fenêtre ; la carte est centrée DEDANS avec
        // une marge `pad`. La fenêtre ne change jamais de taille → l'agrandissement au survol se fait
        // par transform de la couche de la carte (GPU, fluide), en grossissant dans cette marge.
        let container = NSView(frame: NSRect(origin: .zero, size: winSize))
        rootView.frame = NSRect(x: pad, y: pad, width: cardSize.width, height: cardSize.height)
        rootView.autoresizingMask = []
        container.addSubview(rootView)
        contentView = container
        rootView.onHover = { [weak self] inside in self?.setHover(inside) }
        rootView.onAction = { [weak self] action in self?.handle(action) }
    }

    required init?(coder: NSCoder) { fatalError("no coder") }

    // Borderless : ne jamais devenir key/main pour ne pas perturber l'app active.
    // Les clics sur les boutons fonctionnent quand même via `acceptsFirstMouse`.
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    // MARK: Présentation / fermeture

    /// Apparition « façon BridgeShot » : GLISSE depuis le bord horizontal (hors-champ) + fondu, jusqu'à
    /// `origin`. Pour un coin de DROITE, on glisse depuis la droite ; sinon depuis la gauche.
    func appear(at origin: NSPoint, fromRight: Bool = false) {
        // Créée pendant une session de capture : on la place, mais on ne la montre PAS (elle sortirait
        // dans le screenshot en cours). Elle apparaîtra à la levée de la suspension.
        guard !isSuspended else {
            setFrame(NSRect(origin: origin, size: frame.size), display: false)
            alphaValue = 1
            return
        }
        let off = fromRight ? (frame.width + 28) : -(frame.width + 28)
        setFrameOrigin(NSPoint(x: origin.x + off, y: origin.y))
        alphaValue = 0
        orderFrontRegardless()                            // s'affiche même app inactive
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.34
            ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)
            animator().alphaValue = 1
            animator().setFrame(NSRect(origin: origin, size: frame.size), display: true)
        }
    }

    /// Repositionne la vignette en douceur (la rangée se décale quand on ajoute/retire une carte).
    /// NB : on anime via `setFrame` (et NON `setFrameOrigin`) — seul `frame` est animable sur une
    /// NSWindow ; `animator().setFrameOrigin` ne bougeait PAS → toutes les cartes restaient au coin.
    func relocate(to origin: NSPoint, animated: Bool) {
        guard frame.origin != origin else { return }
        let target = NSRect(origin: origin, size: frame.size)
        guard animated else { setFrame(target, display: true); return }
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.3
            ctx.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            animator().setFrame(target, display: true)
        }
    }

    /// Fermeture immédiate (utilisée quand une nouvelle vignette remplace celle-ci).
    func dismissImmediately() { dismiss(animated: false) }

    /// Retire / remet la vignette à l'écran le temps d'une capture. Le retrait est INSTANTANÉ (aucun fondu :
    /// le déclenchement suit immédiatement) et n'affecte ni la position, ni le cycle de vie, ni les timers —
    /// la carte revient exactement où elle était. Sans effet sur une vignette déjà en cours de fermeture.
    func setSuspended(_ suspended: Bool) {
        guard isSuspended != suspended else { return }
        // En cours de fermeture (fondu de 0,16 s après la croix / la corbeille) : elle doit QUAND MÊME
        // sortir de l'écran si une capture démarre, sinon la carte à demi transparente est gravée dans
        // le screenshot. On ne la remet jamais à l'écran (elle est en train de partir).
        if isDismissing {
            if suspended { isSuspended = true; orderOut(nil) }
            return
        }
        isSuspended = suspended
        if suspended {
            rootView.setImageZoom(false)                  // annule un survol en cours : elle revient au repos
            rootView.setChromeVisible(false)
            orderOut(nil)
        } else {
            alphaValue = 1
            orderFrontRegardless()
        }
    }

    private func dismiss(animated: Bool) {
        guard !isDismissing else { return }
        isDismissing = true
        invalidateTimers()
        guard animated else { finishClose(); return }
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.16
            ctx.timingFunction = CAMediaTimingFunction(name: .easeIn)
            self.animator().alphaValue = 0
        }, completionHandler: { [weak self] in
            self?.finishClose()
        })
    }

    private func finishClose() {
        orderOut(nil)
        onClosed?()
        onClosed = nil
    }

    private func invalidateTimers() {
        dismissTimer?.invalidate(); dismissTimer = nil
    }

    // MARK: Survol

    private func setHover(_ inside: Bool) {
        // Survol « façon BridgeShot » : MINI zoom fluide de l'IMAGE SEULE (transform GPU) — surtout PAS de
        // toute la carte, sinon la croix et la barre se décaleraient de leur zone cliquable (hitTest ignore
        // les transforms de calque). + révélation douce du chrome (croix + barre d'actions).
        rootView.setImageZoom(inside)
        rootView.setChromeVisible(inside)
    }

    // MARK: Routage des actions → AppCoordinator

    private func handle(_ action: ThumbAction) {
        let url = fileURL
        switch action {
        case .close:
            dismiss(animated: true)                          // SEULE la croix referme explicitement
        case .open:
            AppCoordinator.shared.openInApp(url)             // clic sur la carte = OUVRIR dans l'app (sidebar + détail)
        case .annotate, .enhance:
            // « Améliorer » mène à l'annotation tant qu'aucun pipeline dédié n'existe.
            AppCoordinator.shared.annotate(url)              // la vignette RESTE ouverte
        case .copy:
            // Capture d'overlay (zone/fenêtre) : la vignette surgit AVANT la fin de l'écriture disque
            // (encodage en tâche de fond). Fichier pas encore là → `copyImage` viderait le presse-papiers
            // et afficherait « Copié » sans rien copier : on copie alors l'image EN MÉMOIRE.
            if FileManager.default.fileExists(atPath: url.path) {
                AppCoordinator.shared.copyImage(url)         // la vignette RESTE ouverte
            } else {
                let pb = NSPasteboard.general
                pb.clearContents()
                pb.writeObjects([rootView.image])
                GlassConfirmBadge.show("Copié")
            }
        case .pin:
            AppCoordinator.shared.pinToScreen(url)           // la vignette RESTE ouverte
        case .finder:
            AppCoordinator.shared.revealInFinder(url)        // la vignette RESTE ouverte
        case .trash:
            AppCoordinator.shared.trash(url)
            dismiss(animated: true)                          // fichier à la corbeille → on referme
        }
    }
}

// MARK: - Vue racine (image + chrome au survol)

/// Contenu de la vignette : image en couverture, croix de fermeture et barre
/// d'actions, ces deux derniers révélés au survol. Gère le suivi de survol et le
/// clic sur l'image (= annoter).
private final class ThumbnailRootView: NSView {
    var onAction: ((ThumbAction) -> Void)?
    var onHover: ((Bool) -> Void)?

    private var barView: BarView?
    private var closeButton: IconButton?
    private var contentImageView: ThumbnailImageView?     // calque zoomé au survol (l'image SEULE)
    private let barMargin: CGFloat = 10       // marge de la capsule d'actions (sert aussi au glissé de survol)

    /// Fichier capturé + son image : servent au GLISSÉ natif (déposer le fichier ailleurs).
    private let fileURL: URL
    private let sourceImage: NSImage
    /// Image en mémoire (repli « Copier » tant que le fichier n'est pas encore écrit).
    var image: NSImage { sourceImage }
    /// Glisser-déposer du fichier en cours (entre `beginFileDrag` et `draggingSession(_:endedAt:…)`).
    private(set) var isDraggingFile = false
    /// Garde la fenêtre (donc cette vue, source du glissé) EN VIE jusqu'à la fin de la session : si la
    /// vignette est retirée pendant le glissé (7e capture qui évince la plus ancienne, échec d'écriture),
    /// AppKit rappellerait sinon une source de glissé libérée.
    private var dragKeepAlive: NSWindow?
    /// La capture est un enregistrement vidéo (.mov) → badge lecture + barre d'actions adaptée.
    private let isVideo: Bool
    /// Position (coords fenêtre) du clic enfoncé, tant qu'on n'a pas tranché clic vs glissé.
    private var pressOrigin: NSPoint?
    /// Seuil de déplacement (points) au-delà duquel un appui devient un GLISSÉ (et non un clic).
    private let dragThreshold: CGFloat = 4

    init(image: NSImage, fileURL: URL, isVideo: Bool) {
        self.fileURL = fileURL
        self.sourceImage = image
        self.isVideo = isVideo
        super.init(frame: NSRect(x: 0, y: 0, width: 240, height: 170))
        wantsLayer = true
        layer?.cornerRadius = 12                             // coins arrondis
        layer?.masksToBounds = true                          // clippe l'image + la barre aux coins
        layer?.borderWidth = 1                               // fin liseré
        layer?.borderColor = NSColor.white.withAlphaComponent(0.18).cgColor
        buildUI(image: image)
    }
    required init?(coder: NSCoder) { fatalError("no coder") }

    private func buildUI(image: NSImage) {
        // Image en mode « couverture » (remplit la carte, déborde clippé).
        let imageView = ThumbnailImageView(image: image)
        imageView.frame = bounds
        imageView.autoresizingMask = [.width, .height]
        imageView.wantsLayer = true                          // calque dédié → on zoome SEULEMENT l'image au survol
        addSubview(imageView)
        self.contentImageView = imageView

        // Badge « lecture » centré pour un enregistrement vidéo (comme la galerie). Purement visuel :
        // il n'implémente AUCUN handler souris, donc les clics/glissés remontent à la carte (responder
        // chain) → ouvrir au clic et glisser-déposer le .mov restent possibles. Enfant de l'image → il
        // suit le mini-zoom au survol.
        if isVideo {
            let cfg = NSImage.SymbolConfiguration(pointSize: 40, weight: .semibold)
            let play = NSImageView()
            play.image = NSImage(systemSymbolName: "play.circle.fill", accessibilityDescription: "Vidéo")?
                .withSymbolConfiguration(cfg)
            play.contentTintColor = NSColor.white.withAlphaComponent(0.95)
            play.imageScaling = .scaleNone
            play.wantsLayer = true
            play.layer?.shadowColor = NSColor.black.cgColor
            play.layer?.shadowOpacity = 0.45
            play.layer?.shadowRadius = 4
            play.layer?.shadowOffset = .zero
            let side: CGFloat = 48
            play.frame = NSRect(x: (bounds.width - side) / 2, y: (bounds.height - side) / 2, width: side, height: side)
            play.autoresizingMask = [.minXMargin, .maxXMargin, .minYMargin, .maxYMargin]
            imageView.addSubview(play)
        }

        // Icônes — l'icône GARDE sa taille (12 pt) ; on RESSERRE en réduisant la largeur de cellule
        // ET la capsule (demande : « case plus petite / plus resserrée, sans changer la taille des icônes »).
        // Vidéo : annoter/copier/améliorer/épingler n'ont pas de sens sur un .mov → barre réduite
        // (ouvrir / Finder / corbeille) ; le glissé du fichier reste possible pour l'exporter ailleurs.
        let defs: [(symbol: String, tip: String, action: ThumbAction, red: Bool)] = isVideo ? [
            ("play.circle", "Ouvrir", .open, false),
            ("folder", "Afficher dans le Finder", .finder, false),
            ("trash", "Mettre à la corbeille", .trash, true),
        ] : [
            ("pencil.tip.crop.circle", "Annoter", .annotate, false),
            ("doc.on.doc", "Copier", .copy, false),
            ("sparkles", "Améliorer", .enhance, false),
            ("pin", "Épingler", .pin, false),
            ("folder", "Afficher dans le Finder", .finder, false),
            ("trash", "Mettre à la corbeille", .trash, true),
        ]
        let barH: CGFloat = 36
        let chipW: CGFloat = 29                              // cellule plus étroite → icônes plus rapprochées
        let innerPad: CGFloat = 6                            // marge gauche/droite DANS la capsule
        let barW = CGFloat(defs.count) * chipW + innerPad * 2

        // CAPSULE sombre flottante (façon BridgeShot), CENTRÉE et resserrée, masquée hors survol.
        let bar = BarView()
        bar.material = .hudWindow
        bar.blendingMode = .withinWindow
        bar.state = .active
        bar.appearance = NSAppearance(named: .darkAqua)      // fond sombre constant → icônes blanches lisibles
        bar.wantsLayer = true
        bar.layer?.cornerRadius = barH / 2                   // pilule (capsule)
        bar.layer?.masksToBounds = true
        bar.layer?.borderWidth = 1
        bar.layer?.borderColor = NSColor.white.withAlphaComponent(0.28).cgColor
        bar.frame = NSRect(x: ((bounds.width - barW) / 2).rounded(), y: barMargin, width: barW, height: barH)
        bar.autoresizingMask = [.minXMargin, .maxXMargin, .maxYMargin]   // reste centrée en bas
        bar.alphaValue = 0
        bar.interactive = false                              // masquée → laisse passer le clic « annoter » vers l'image
        addSubview(bar)

        // Assombrissement : le matériau frosté seul n'est pas assez « noir » → calque noir SOUS les icônes.
        let darken = NSView()
        darken.wantsLayer = true
        darken.layer?.backgroundColor = NSColor.black.withAlphaComponent(0.55).cgColor
        darken.frame = bar.bounds
        darken.autoresizingMask = [.width, .height]
        bar.addSubview(darken)

        for (i, d) in defs.enumerated() {
            let chip = IconChip(symbol: d.symbol, tip: d.tip,
                                circleColor: d.red ? NSColor.systemRed.withAlphaComponent(0.32)
                                                   : NSColor.white.withAlphaComponent(0.20),
                                iconHoverTint: d.red ? .systemRed : .white,
                                pointSize: 12)
            let action = d.action
            chip.onClick = { [weak self] in self?.onAction?(action) }
            chip.frame = NSRect(x: innerPad + CGFloat(i) * chipW, y: 0, width: chipW, height: barH)
            chip.autoresizingMask = [.maxXMargin]
            bar.addSubview(chip)
        }

        // Croix de fermeture, en haut-droite, dans une pastille sombre translucide.
        let close = IconButton()
        close.bezelStyle = .regularSquare
        close.isBordered = false
        close.imagePosition = .imageOnly
        let xCfg = NSImage.SymbolConfiguration(pointSize: 10, weight: .bold)
        let xImg = NSImage(systemSymbolName: "xmark", accessibilityDescription: "Fermer")?
            .withSymbolConfiguration(xCfg)
        xImg?.isTemplate = true
        close.image = xImg
        close.contentTintColor = .white
        close.toolTip = "Fermer"
        close.target = self
        close.action = #selector(closeAction)
        let cs: CGFloat = 20
        close.frame = NSRect(x: bounds.width - cs - 8, y: bounds.height - cs - 8, width: cs, height: cs)
        close.autoresizingMask = [.minXMargin, .minYMargin]
        close.wantsLayer = true
        close.layer?.backgroundColor = NSColor.black.withAlphaComponent(0.72).cgColor   // fond plus NOIR
        close.layer?.cornerRadius = cs / 2
        close.layer?.masksToBounds = true
        close.layer?.borderWidth = 1                                                     // MÊME liseré blanc que la capsule
        close.layer?.borderColor = NSColor.white.withAlphaComponent(0.28).cgColor
        close.restBG = NSColor.black.withAlphaComponent(0.72)
        close.hoverBG = NSColor.black.withAlphaComponent(0.55)                           // se relève d'un cran (reste sombre) ; c'est le LISERÉ qui brille
        close.alphaValue = 0
        close.interactive = false                            // révélée au survol (cf. hitTest)
        addSubview(close)

        self.barView = bar
        self.closeButton = close
    }

    // MARK: Survol — zoom de l'image seule (la croix/barre ne bougent pas → zones cliquables alignées)

    /// Zoom « mini » fluide de l'IMAGE au survol (transform GPU, calque centré). On ne zoome QUE l'image —
    /// pas la croix ni la barre — pour que leur rendu reste EXACTEMENT sur leur zone cliquable (hitTest
    /// ignore les transforms de calque, donc scaler tout décalerait le visuel des hit-zones).
    func setImageZoom(_ on: Bool) {
        guard let l = contentImageView?.layer else { return }
        let scale: CGFloat = on ? 1.04 : 1.0
        let anim = CABasicAnimation(keyPath: "transform.scale")
        anim.fromValue = l.presentation()?.value(forKeyPath: "transform.scale") ?? (on ? 1.0 : 1.04)
        anim.toValue = scale
        anim.duration = 0.28
        anim.timingFunction = CAMediaTimingFunction(name: .easeOut)
        l.setValue(scale, forKeyPath: "transform.scale")
        l.add(anim, forKey: "imgZoom")
    }

    // MARK: Révélation du chrome au survol

    func setChromeVisible(_ visible: Bool) {
        // Hors survol, barre et croix sont « transparentes » au clic (hitTest → nil),
        // pour que le clic sur la carte déclenche « annoter » et non un bouton fantôme.
        barView?.interactive = visible
        closeButton?.interactive = visible

        // Révélation TRÈS DOUCE (demande user) : la barre GLISSE LÉGÈREMENT depuis le bas (5 px) + fondu,
        // ease-out SANS rebond ; la croix FOND simplement (pas de glissé). « très léger, très smooth ».
        if let bar = barView, visible {
            bar.setFrameOrigin(NSPoint(x: bar.frame.minX, y: barMargin - 5))
        }
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.26
            ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)   // doux, AUCUN rebond
            if let bar = barView {
                bar.animator().alphaValue = visible ? 1 : 0
                bar.animator().setFrameOrigin(NSPoint(x: bar.frame.minX, y: barMargin))
            }
            closeButton?.animator().alphaValue = visible ? 1 : 0
        }
    }

    // MARK: Suivi du survol

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero,
                                       options: [.activeAlways, .mouseEnteredAndExited, .inVisibleRect],
                                       owner: self, userInfo: nil))
    }
    override func mouseEntered(with event: NSEvent) { onHover?(true) }
    override func mouseExited(with event: NSEvent) { onHover?(false) }

    // Clic sur la vignette (hors boutons/barre) : un simple clic OUVRE dans l'app, mais un
    // appui-glissé DÉTACHE le fichier (drag natif) pour le déposer ailleurs (Discord, Drive, Finder…).
    // `acceptsFirstMouse` → le tout premier clic/glissé est livré même quand l'app n'est pas active.
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with event: NSEvent) {
        pressOrigin = event.locationInWindow            // on attend : glissé au-delà du seuil, ou relâché sur place ?
    }

    override func mouseDragged(with event: NSEvent) {
        guard let start = pressOrigin else { return }   // (nil = un glissé est déjà en cours)
        let moved = hypot(event.locationInWindow.x - start.x, event.locationInWindow.y - start.y)
        guard moved > dragThreshold else { return }
        pressOrigin = nil                               // bascule en mode glissé → le mouseUp ne rouvrira pas
        beginFileDrag(with: event)
    }

    override func mouseUp(with event: NSEvent) {
        guard pressOrigin != nil else { return }        // un glissé a consommé l'appui → pas d'ouverture
        pressOrigin = nil
        onAction?(.open)                                // simple clic sur la carte = ouvrir dans l'app
    }

    /// Démarre un glissé natif du FICHIER capturé : l'utilisateur peut le déposer dans n'importe quelle
    /// app acceptant un fichier (conversation Discord, Google Drive, Finder, Mail…). L'aperçu du glissé
    /// reprend la vignette (image « couverture », coins arrondis). Copie (jamais déplacé).
    private func beginFileDrag(with event: NSEvent) {
        // On glisse l'URL de fichier (type `public.file-url`) → les cibles la traitent comme un vrai fichier.
        // Repli si le fichier n'existe plus sur le disque : on glisse l'image elle-même (données bitmap).
        let writer: NSPasteboardWriting = FileManager.default.fileExists(atPath: fileURL.path)
            ? (fileURL as NSURL) : sourceImage
        let item = NSDraggingItem(pasteboardWriter: writer)
        item.setDraggingFrame(bounds, contents: dragPreviewImage())
        let session = beginDraggingSession(with: [item], event: event, source: self)
        session.animatesToStartingPositionsOnCancelOrFail = true
        isDraggingFile = true
        dragKeepAlive = window
    }

    /// Aperçu du glissé : l'image en « couverture » dans la taille de la carte, coins arrondis (identique à la vignette).
    private func dragPreviewImage() -> NSImage {
        let size = bounds.size
        let out = NSImage(size: size)
        out.lockFocus()
        NSBezierPath(roundedRect: NSRect(origin: .zero, size: size), xRadius: 12, yRadius: 12).addClip()
        let s = sourceImage.size
        if s.width > 0, s.height > 0 {
            let scale = max(size.width / s.width, size.height / s.height)   // couverture (aspect fill) comme la vignette
            let w = s.width * scale, h = s.height * scale
            sourceImage.draw(in: NSRect(x: (size.width - w) / 2, y: (size.height - h) / 2, width: w, height: h))
        }
        out.unlockFocus()
        return out
    }

    @objc private func annotateAction() { onAction?(.annotate) }
    @objc private func copyAction()     { onAction?(.copy) }
    @objc private func enhanceAction()  { onAction?(.enhance) }
    @objc private func pinAction()      { onAction?(.pin) }
    @objc private func finderAction()   { onAction?(.finder) }
    @objc private func trashAction()    { onAction?(.trash) }
    @objc private func closeAction()    { onAction?(.close) }
}

// MARK: - Source de glissé (déposer le fichier ailleurs)

extension ThumbnailRootView: NSDraggingSource {
    /// COPIE partout (la capture reste dans la galerie) — jamais un déplacement qui viderait FrameLine.
    func draggingSession(_ session: NSDraggingSession,
                         sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation {
        .copy
    }
    /// Un glissé annulé (relâché dans le vide) ne doit PAS être interprété ensuite comme un clic « ouvrir ».
    func draggingSession(_ session: NSDraggingSession, endedAt screenPoint: NSPoint,
                         operation: NSDragOperation) {
        pressOrigin = nil
        isDraggingFile = false
        // Libérée au tour suivant : AppKit peut encore toucher la source juste après ce rappel.
        let keepAlive = dragKeepAlive
        dragKeepAlive = nil
        DispatchQueue.main.async { _ = keepAlive }
        // Réglage « Fermer l'aperçu après un glisser-déposer » : on referme la vignette UNIQUEMENT si le
        // dépôt a RÉUSSI (une cible l'a accepté → operation non vide). Un glissé annulé/relâché dans le
        // vide (operation == []) laisse la vignette en place.
        if operation != [], Preferences.shared.closePreviewAfterDrag {
            onAction?(.close)
        }
    }
}

// MARK: - Sous-vues utilitaires

/// Icône de la barre d'actions « façon BridgeShot » : icône blanche centrée + ROND de surbrillance
/// au survol qui apparaît EN DOUCEUR derrière (blanc translucide ; ROUGE pour la corbeille, qui teinte
/// aussi son icône en rouge — cf. captures BridgeShot). Le rond est SOUS l'icône (vue dédiée), donc
/// l'icône reste visible.
private final class IconChip: NSView {
    var onClick: (() -> Void)?
    private let circleView = NSView()
    private let iconView = NSImageView()
    private let iconRestTint: NSColor = .white
    private let iconHoverTint: NSColor
    private var hovering = false
    private var tracking: NSTrackingArea?

    init(symbol: String, tip: String, circleColor: NSColor, iconHoverTint: NSColor, pointSize: CGFloat) {
        self.iconHoverTint = iconHoverTint
        super.init(frame: .zero)
        wantsLayer = true
        toolTip = tip

        circleView.wantsLayer = true
        circleView.layer?.backgroundColor = circleColor.cgColor
        circleView.alphaValue = 0                              // caché au repos, fondu au survol
        addSubview(circleView)                                 // SOUS l'icône

        let cfg = NSImage.SymbolConfiguration(pointSize: pointSize, weight: .semibold)
        iconView.image = NSImage(systemSymbolName: symbol, accessibilityDescription: tip)?.withSymbolConfiguration(cfg)
        iconView.image?.isTemplate = true
        iconView.contentTintColor = iconRestTint
        iconView.imageScaling = .scaleProportionallyDown       // icône à sa petite taille, centrée
        addSubview(iconView)
    }
    @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }

    override func layout() {
        super.layout()
        let d = min(bounds.width, bounds.height) + 2           // rond plus GRAND (zone de survol agrandie)
        circleView.frame = NSRect(x: (bounds.width - d) / 2, y: (bounds.height - d) / 2, width: d, height: d)
        circleView.layer?.cornerRadius = d / 2
        iconView.frame = bounds
    }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }   // 1er clic même app inactive
    override func hitTest(_ point: NSPoint) -> NSView? {
        bounds.contains(convert(point, from: superview)) ? self : nil
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let t = tracking { removeTrackingArea(t) }
        let t = NSTrackingArea(rect: bounds, options: [.activeAlways, .mouseEnteredAndExited, .inVisibleRect],
                               owner: self, userInfo: nil)
        addTrackingArea(t); tracking = t
    }
    override func mouseEntered(with event: NSEvent) { setHover(true) }
    override func mouseExited(with event: NSEvent) { setHover(false) }
    override func mouseDown(with event: NSEvent) { /* avale → pas d'« annoter » par mégarde */ }
    override func mouseUp(with event: NSEvent) {
        if bounds.contains(convert(event.locationInWindow, from: nil)) { onClick?() }
    }

    private func setHover(_ on: Bool) {
        guard hovering != on else { return }
        hovering = on
        iconView.contentTintColor = on ? iconHoverTint : iconRestTint
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.16
            ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)   // smooth, pas de rebond
            circleView.animator().alphaValue = on ? 1 : 0
        }
    }
}

/// Bouton-icône qui accepte le tout premier clic même fenêtre/app inactive
/// (la vignette ne devient jamais key) → la barre reste cliquable du premier coup.
/// `interactive == false` → invisible au clic (hitTest → nil), pour la croix masquée.
private final class IconButton: NSButton {
    var interactive = true
    /// Tolérance de clic autour de la case. 0 → la zone cliquable est EXACTEMENT la case visible (même taille,
    /// même emplacement). Comme l'image seule zoome au survol, la croix ne se décale plus de sa zone cliquable.
    var hitSlop: CGFloat = 0
    /// Survol : le fond passe en douceur de `restBG` à `hoverBG` (pastille de surbrillance façon
    /// BridgeShot). Une seule icône est éclairée à la fois → pas de gêne entre icônes adjacentes.
    var restBG: NSColor = .clear
    var hoverBG: NSColor = NSColor.white.withAlphaComponent(0.16)
    var restBorder: NSColor = NSColor.white.withAlphaComponent(0.28)   // liseré au repos
    var hoverBorder: NSColor = NSColor.white.withAlphaComponent(0.6)   // liseré au survol (brille)
    private var hovering = false
    private var hoverTracking: NSTrackingArea?

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    // Zone cliquable = EXACTEMENT la case visible (et non le seul glyphe ✕, comme le ferait le hitTest
    // d'un NSButton image-only). `hitSlop` (0 par défaut) permettrait de l'élargir un peu si besoin.
    override func hitTest(_ point: NSPoint) -> NSView? {
        guard interactive else { return nil }
        let local = convert(point, from: superview)
        return bounds.insetBy(dx: -hitSlop, dy: -hitSlop).contains(local) ? self : nil
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let t = hoverTracking { removeTrackingArea(t) }
        let t = NSTrackingArea(rect: bounds,
                               options: [.activeAlways, .mouseEnteredAndExited, .inVisibleRect],
                               owner: self, userInfo: nil)
        addTrackingArea(t)
        hoverTracking = t
    }

    override func mouseEntered(with event: NSEvent) {
        guard interactive else { return }
        setHover(true)
    }
    override func mouseExited(with event: NSEvent) { setHover(false) }

    private func setHover(_ on: Bool) {
        guard hovering != on else { return }
        hovering = on
        wantsLayer = true
        // Survol DISCRET, PROPRE, NET : le liseré blanc BRILLE + le fond se relève d'un cran (reste
        // sombre). Pas de gris baveux ni de zoom → juste le contour qui s'illumine. On le voit, c'est net.
        let bgTarget = (on ? hoverBG : restBG).cgColor
        let bdTarget = (on ? hoverBorder : restBorder).cgColor
        let bg = CABasicAnimation(keyPath: "backgroundColor")
        bg.fromValue = layer?.presentation()?.backgroundColor ?? layer?.backgroundColor
        bg.toValue = bgTarget
        let bd = CABasicAnimation(keyPath: "borderColor")
        bd.fromValue = layer?.presentation()?.borderColor ?? layer?.borderColor
        bd.toValue = bdTarget
        for a in [bg, bd] { a.duration = 0.16; a.timingFunction = CAMediaTimingFunction(name: .easeOut) }
        layer?.backgroundColor = bgTarget
        layer?.borderColor = bdTarget
        layer?.add(bg, forKey: "hoverBG")
        layer?.add(bd, forKey: "hoverBorder")
    }
}

/// Fond frosté de la barre d'actions. Consomme les clics tombés dans ses vides
/// (entre les boutons) pour ne pas déclencher « annoter » par mégarde. Hors survol
/// (`interactive == false`), elle laisse passer le clic vers l'image (hitTest → nil).
private final class BarView: NSVisualEffectView {
    var interactive = false
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with event: NSEvent) { /* avale le clic */ }
    override func hitTest(_ point: NSPoint) -> NSView? { interactive ? super.hitTest(point) : nil }
}

/// Affiche l'image en « couverture » (aspect fill) : remplit la carte sans
/// déformation, le débordement étant clippé par le coin arrondi du parent.
private final class ThumbnailImageView: NSView {
    private let image: NSImage
    init(image: NSImage) {
        self.image = image
        super.init(frame: .zero)
    }
    required init?(coder: NSCoder) { fatalError("no coder") }

    override var isFlipped: Bool { false }                    // origine en bas-gauche, comme NSImage

    override func draw(_ dirtyRect: NSRect) {
        let b = bounds
        NSColor.black.setFill()                              // fond derrière l'image (jamais visible si couverture)
        b.fill()
        let s = image.size
        guard s.width > 0, s.height > 0 else { return }
        let scale = max(b.width / s.width, b.height / s.height)   // couverture = max (et non min)
        let w = s.width * scale, h = s.height * scale
        image.draw(in: NSRect(x: (b.width - w) / 2, y: (b.height - h) / 2, width: w, height: h))
    }
}
