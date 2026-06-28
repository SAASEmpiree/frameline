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
private enum ThumbAction { case annotate, copy, enhance, pin, finder, trash, close }

// MARK: - Point d'entrée public

/// Contrôleur partagé : seul objet que l'orchestrateur manipule.
@MainActor
final class CaptureThumbnailController {
    static let shared = CaptureThumbnailController()
    private init() {}

    /// La vignette courante (forte ref → la maintient en vie ; les timers internes
    /// la retiennent aussi tant qu'ils tournent, cf. `ThumbnailPanel`).
    private var panel: ThumbnailPanel?

    /// Affiche une vignette pour `image`/`fileURL`. Remplace immédiatement toute
    /// vignette déjà visible (une à la fois).
    func present(image: NSImage, fileURL: URL) {
        panel?.dismissImmediately()                       // ferme l'ancienne sans animation
        let p = ThumbnailPanel(image: image, fileURL: fileURL)
        // À la fermeture (auto, croix ou action), on lâche notre référence — mais
        // seulement si c'est bien CETTE vignette (une nouvelle a pu prendre sa place).
        p.onClosed = { [weak self, weak p] in
            if self?.panel === p { self?.panel = nil }
        }
        panel = p
        p.present()
    }

    /// Surcharge pratique quand l'appelant n'a que l'URL du fichier capturé.
    func present(fileURL: URL) {
        guard let img = NSImage(contentsOf: fileURL) else { return }
        present(image: img, fileURL: fileURL)
    }

    /// Ferme la vignette UNIQUEMENT si elle correspond à `url` (ex. l'écriture disque de cette
    /// capture a échoué → on retire la vignette orpheline qui pointait vers un fichier inexistant,
    /// sans tuer une vignette plus récente qui aurait pris sa place).
    func dismiss(forFailedURL url: URL) {
        guard panel?.capturedURL == url else { return }
        panel?.dismissImmediately()
        panel = nil
    }
}

// MARK: - Fenêtre de la vignette

/// Panneau borderless flottant non-activant : il s'affiche par-dessus toutes les
/// fenêtres (y compris en plein écran) sans voler le focus de l'app de l'utilisateur.
private final class ThumbnailPanel: NSPanel {
    private let fileURL: URL
    /// URL de la capture affichée (pour qu'un appelant puisse cibler CETTE vignette).
    var capturedURL: URL { fileURL }
    private let rootView: ThumbnailRootView

    /// Appelé une fois la vignette réellement retirée de l'écran.
    var onClosed: (() -> Void)?

    private var dismissTimer: Timer?          // auto-disparition (~6 s)
    private var followTimer: Timer?           // suivi de la souris (~0,4 s)
    private var isMouseInside = false         // survol → fige la position + suspend l'auto-disparition
    private var isDismissing = false          // garde-fou anti double-fermeture

    private let size = NSSize(width: 240, height: 170)
    private let screenMargin: CGFloat = 19    // marge depuis le bord bas-gauche de l'écran
    private let hoverGrow: CGFloat = 12        // « grossissement » de la carte au survol (feedback)

    init(image: NSImage, fileURL: URL) {
        self.fileURL = fileURL
        self.rootView = ThumbnailRootView(image: image)
        super.init(contentRect: NSRect(origin: .zero, size: size),
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

        contentView = rootView
        rootView.onHover = { [weak self] inside in self?.setHover(inside) }
        rootView.onAction = { [weak self] action in self?.handle(action) }
    }
    required init?(coder: NSCoder) { fatalError("no coder") }

    // Borderless : ne jamais devenir key/main pour ne pas perturber l'app active.
    // Les clics sur les boutons fonctionnent quand même via `acceptsFirstMouse`.
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    // MARK: Présentation / fermeture

    func present() {
        guard let origin = bottomLeftOrigin() else { return }
        // Apparition « façon BridgeShot » : la vignette GLISSE depuis la gauche (hors-champ) + fondu,
        // jusqu'à sa place en bas-gauche.
        setFrameOrigin(NSPoint(x: origin.x - frame.width - screenMargin, y: origin.y))
        alphaValue = 0
        orderFrontRegardless()                            // s'affiche même app inactive
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.34
            ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)
            animator().alphaValue = 1
            animator().setFrame(NSRect(origin: origin, size: frame.size), display: true)
        }
        scheduleAutoDismiss()
        // Timer de suivi : Timer cible/sélecteur (et non closure) pour rester aligné
        // sur le style du repo et éviter toute friction de concurrence ; le cycle de
        // rétention Timer→self est rompu par `invalidateTimers()` à la fermeture.
        followTimer = Timer.scheduledTimer(timeInterval: 0.4, target: self,
                                           selector: #selector(followMouse), userInfo: nil, repeats: true)
    }

    /// Fermeture immédiate (utilisée quand une nouvelle vignette remplace celle-ci).
    func dismissImmediately() { dismiss(animated: false) }

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
        followTimer?.invalidate(); followTimer = nil
    }

    // MARK: Auto-disparition

    private func scheduleAutoDismiss() {
        // Auto-disparition DÉSACTIVÉE : la vignette reste affichée tant que l'utilisateur ne la
        // ferme pas lui-même (croix) ou ne la met pas à la corbeille — comme demandé. Conservé en
        // no-op pour ne pas toucher aux appelants (present/setHover).
    }
    @objc private func autoDismissFired() { dismiss(animated: true) }

    // MARK: Survol

    private func setHover(_ inside: Bool) {
        isMouseInside = inside
        rootView.setChromeVisible(inside)
        // Mini-animation : la carte « grandit » légèrement sous la souris (feedback de survol, façon BridgeShot).
        let f = frame
        let target: NSRect = inside
            ? NSRect(x: f.minX - hoverGrow / 2, y: f.minY - hoverGrow / 2,
                     width: size.width + hoverGrow, height: size.height + hoverGrow)
            : NSRect(x: f.minX + hoverGrow / 2, y: f.minY + hoverGrow / 2,
                     width: size.width, height: size.height)
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.14
            ctx.timingFunction = CAMediaTimingFunction(name: inside ? .easeOut : .easeIn)
            animator().setFrame(target, display: true)
        }
        if inside {
            dismissTimer?.invalidate(); dismissTimer = nil   // ne pas disparaître sous la souris
        } else {
            scheduleAutoDismiss()                            // relance le compte à rebours en sortie
        }
    }

    // MARK: Suivi de la souris

    /// Cible d'un déplacement animé en cours → évite que le timer relance l'animation
    /// à chaque tic tant que la vignette glisse encore (sinon : saccades).
    private var animatingToOrigin: NSPoint?

    @objc private func followMouse() {
        // On ne déplace pas la vignette pendant un survol : éviter de la dérober au
        // curseur de l'utilisateur en pleine interaction.
        guard !isMouseInside, let origin = bottomLeftOrigin() else { return }
        // Déjà à destination, ou déjà en route vers elle → rien à faire.
        if approxEqual(frame.origin, origin) { return }
        if let t = animatingToOrigin, approxEqual(t, origin) { return }
        moveAnimated(to: origin)
    }

    /// Déplacement FLUIDE vers le coin de l'écran courant quand la souris change de
    /// fenêtre/écran : glissé court en ease-in-out au lieu d'un saut sec → la vignette
    /// « suit » proprement plutôt que de téléporter.
    private func moveAnimated(to origin: NSPoint) {
        animatingToOrigin = origin
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.32
            ctx.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            self.animator().setFrame(NSRect(origin: origin, size: self.frame.size), display: true)
        }, completionHandler: { [weak self] in
            guard let self, let t = self.animatingToOrigin, self.approxEqual(t, origin) else { return }
            self.animatingToOrigin = nil          // arrivé : on réautorise le prochain déplacement
        })
    }

    private func approxEqual(_ a: NSPoint, _ b: NSPoint) -> Bool {
        abs(a.x - b.x) < 0.5 && abs(a.y - b.y) < 0.5
    }

    /// Coin bas-gauche (au-dessus du Dock) de l'écran qui contient la souris.
    private func bottomLeftOrigin() -> NSPoint? {
        let loc = NSEvent.mouseLocation
        guard let screen = NSScreen.screens.first(where: { NSMouseInRect(loc, $0.frame, false) }) ?? NSScreen.main
        else { return nil }
        let vf = screen.visibleFrame                         // exclut Dock + barre de menus
        return NSPoint(x: vf.minX + screenMargin, y: vf.minY + screenMargin)
    }

    // MARK: Routage des actions → AppCoordinator

    private func handle(_ action: ThumbAction) {
        let url = fileURL
        switch action {
        case .close:
            dismiss(animated: true)                          // SEULE la croix referme explicitement
        case .annotate, .enhance:
            // « Améliorer » mène à l'annotation tant qu'aucun pipeline dédié n'existe.
            AppCoordinator.shared.annotate(url)              // la vignette RESTE ouverte
        case .copy:
            AppCoordinator.shared.copyImage(url)             // la vignette RESTE ouverte
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

    init(image: NSImage) {
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
        addSubview(imageView)

        // Barre d'actions = CAPSULE sombre flottante, DÉTACHÉE des bords (façon BridgeShot, cf. Image #3),
        // masquée tant qu'on ne survole pas.
        let bar = BarView()
        bar.material = .hudWindow
        bar.blendingMode = .withinWindow
        bar.state = .active
        bar.appearance = NSAppearance(named: .darkAqua)      // fond sombre constant → icônes blanches lisibles
        bar.wantsLayer = true
        let barH: CGFloat = 40
        let barMargin: CGFloat = 10
        bar.layer?.cornerRadius = barH / 2                   // pilule (capsule)
        bar.layer?.masksToBounds = true
        bar.layer?.borderWidth = 1
        bar.layer?.borderColor = NSColor.white.withAlphaComponent(0.14).cgColor
        bar.frame = NSRect(x: barMargin, y: barMargin, width: bounds.width - barMargin * 2, height: barH)
        bar.autoresizingMask = [.width, .maxYMargin]
        bar.alphaValue = 0
        bar.interactive = false                              // masquée → laisse passer le clic « annoter » vers l'image (cf. hitTest)
        addSubview(bar)

        let stack = NSStackView(views: [
            iconButton("pencil.tip.crop.circle", "Annoter", #selector(annotateAction)),
            iconButton("doc.on.doc", "Copier", #selector(copyAction)),
            iconButton("sparkles", "Améliorer", #selector(enhanceAction)),
            iconButton("pin", "Épingler", #selector(pinAction)),
            iconButton("folder", "Afficher dans le Finder", #selector(finderAction)),
            iconButton("trash", "Mettre à la corbeille", #selector(trashAction)),
        ])
        stack.orientation = .horizontal
        stack.distribution = .fillEqually
        stack.alignment = .centerY
        stack.spacing = 0
        stack.translatesAutoresizingMaskIntoConstraints = false
        bar.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: bar.leadingAnchor, constant: 10),
            stack.trailingAnchor.constraint(equalTo: bar.trailingAnchor, constant: -10),
            stack.topAnchor.constraint(equalTo: bar.topAnchor),
            stack.bottomAnchor.constraint(equalTo: bar.bottomAnchor),
        ])

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
        close.layer?.backgroundColor = NSColor.black.withAlphaComponent(0.5).cgColor
        close.layer?.cornerRadius = cs / 2
        close.layer?.masksToBounds = true
        close.alphaValue = 0
        close.interactive = false                            // révélée au survol (cf. hitTest)
        addSubview(close)

        self.barView = bar
        self.closeButton = close
    }

    private func iconButton(_ symbol: String, _ tip: String, _ action: Selector) -> NSButton {
        let b = IconButton()
        b.bezelStyle = .regularSquare
        b.isBordered = false
        b.imagePosition = .imageOnly
        let cfg = NSImage.SymbolConfiguration(pointSize: 14, weight: .semibold)
        let img = NSImage(systemSymbolName: symbol, accessibilityDescription: tip)?.withSymbolConfiguration(cfg)
        img?.isTemplate = true
        b.image = img
        b.contentTintColor = .white
        b.toolTip = tip
        b.target = self
        b.action = action
        return b
    }

    // MARK: Révélation du chrome au survol

    func setChromeVisible(_ visible: Bool) {
        // Hors survol, barre et croix sont « transparentes » au clic (hitTest → nil),
        // pour que le clic sur la carte déclenche « annoter » et non un bouton fantôme.
        barView?.interactive = visible
        closeButton?.interactive = visible
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.15
            barView?.animator().alphaValue = visible ? 1 : 0
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

    // Clic sur la vignette (hors boutons/barre) = annoter. `acceptsFirstMouse` →
    // le tout premier clic est livré même quand l'app n'est pas active.
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with event: NSEvent) { onAction?(.annotate) }

    @objc private func annotateAction() { onAction?(.annotate) }
    @objc private func copyAction()     { onAction?(.copy) }
    @objc private func enhanceAction()  { onAction?(.enhance) }
    @objc private func pinAction()      { onAction?(.pin) }
    @objc private func finderAction()   { onAction?(.finder) }
    @objc private func trashAction()    { onAction?(.trash) }
    @objc private func closeAction()    { onAction?(.close) }
}

// MARK: - Sous-vues utilitaires

/// Bouton-icône qui accepte le tout premier clic même fenêtre/app inactive
/// (la vignette ne devient jamais key) → la barre reste cliquable du premier coup.
/// `interactive == false` → invisible au clic (hitTest → nil), pour la croix masquée.
private final class IconButton: NSButton {
    var interactive = true
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { interactive ? super.hitTest(point) : nil }
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
