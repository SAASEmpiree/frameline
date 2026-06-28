import AppKit

/// Fenêtre « épinglée » qui fait flotter une capture AU-DESSUS de tout, sur tous les
/// Spaces, déplaçable au glissé n'importe où et fermable. Sert à la fois pour
/// « Pin to Screen » (figer une capture à l'écran) et « Restore to Screen » (faire
/// réapparaître une capture du dossier sur l'écran) : dans les deux cas le besoin est
/// identique — réafficher l'image flottante, petite et toujours visible.
///
/// POURQUOI un tableau `retained` statique — personne d'autre ne garde une référence
/// forte sur l'instance (l'appelant ne fait qu'un `show(...)` et oublie). Sans ce
/// tableau, ARC désallouerait aussitôt l'objet → la fenêtre se fermerait. On retire
/// l'instance à la fermeture (`windowWillClose`) pour libérer proprement la mémoire.
@MainActor
final class PinnedShotWindow: NSObject, NSWindowDelegate {

    /// Garde les fenêtres épinglées en vie tant qu'elles sont affichées.
    private static var retained: [PinnedShotWindow] = []

    private let window: PinWindow
    private let url: URL
    private let image: NSImage

    /// Affiche `imageURL` dans une fenêtre épinglée flottante. Point d'entrée unique
    /// appelé par AppCoordinator pour `pinToScreen(url:)` ET `restoreToScreen(url:)`.
    @MainActor
    static func show(imageURL: URL) {
        guard let image = NSImage(contentsOf: imageURL) else { return }
        let instance = PinnedShotWindow(image: image, url: imageURL)
        retained.append(instance)
        instance.window.makeKeyAndOrderFront(nil)
        instance.window.orderFrontRegardless()   // visible même si l'app n'est pas au premier plan
        instance.window.invalidateShadow()        // l'ombre épouse les coins arrondis du contenu
    }

    private init(image: NSImage, url: URL) {
        self.url = url
        self.image = image
        // Taille d'affichage : réduite pour tenir à l'écran si la capture est trop grande,
        // ratio conservé. Jamais agrandie (échelle ≤ 1) → une petite capture reste nette.
        let size = Self.displaySize(for: image)
        self.window = PinWindow(contentRect: NSRect(origin: .zero, size: size),
                                styleMask: [.borderless], backing: .buffered, defer: false)
        super.init()

        let content = PinnedContentView(image: image, cornerRadius: 10)
        content.onClose = { [weak self] in self?.close() }
        content.onCopy  = { [weak self] in self?.copyImage() }

        window.contentView = content
        window.isOpaque = false
        window.backgroundColor = .clear            // fond transparent → coins arrondis + ombre nets
        window.hasShadow = true
        window.level = .floating                   // toujours au-dessus des fenêtres ordinaires
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]  // suit l'utilisateur sur tous les Spaces
        window.isMovableByWindowBackground = true  // glisser n'importe où dans la fenêtre = déplacer
        window.isReleasedWhenClosed = false        // la durée de vie est gérée par `retained`
        window.delegate = self

        positionWindow(size: size)
    }

    /// Place la nouvelle fenêtre centrée, avec un léger décalage en cascade pour ne pas
    /// recouvrir exactement les épingles déjà présentes.
    private func positionWindow(size: NSSize) {
        guard let visible = (NSScreen.main ?? NSScreen.screens.first)?.visibleFrame else {
            window.center(); return
        }
        let step = CGFloat(Self.retained.count) * 24   // une épingle de plus = un cran de décalage
        let x = visible.midX - size.width / 2 + step
        let y = visible.midY - size.height / 2 - step
        window.setFrameOrigin(NSPoint(x: x, y: y))
    }

    private func close() { window.close() }

    /// Copie l'image dans le presse-papiers (même convention que CaptureLibrary).
    private func copyImage() {
        let pb = NSPasteboard.general
        pb.clearContents()
        // PNG : on copie les octets d'origine (sans perte) ; sinon NSImage fournit un TIFF standard.
        if url.pathExtension.lowercased() == "png", let data = try? Data(contentsOf: url) {
            pb.setData(data, forType: .png)
        } else {
            pb.writeObjects([image])
        }
    }

    // MARK: NSWindowDelegate

    func windowWillClose(_ notification: Notification) {
        Self.retained.removeAll { $0 === self }    // libère l'instance → ARC désalloue la fenêtre
    }

    /// Taille d'affichage bornée à 70 % de l'écran, ratio conservé, sans agrandissement.
    private static func displaySize(for image: NSImage) -> NSSize {
        var s = image.size
        if s.width < 1 || s.height < 1, let rep = image.representations.first {
            // NSImage.size dégénérée (DPI exotique) → on retombe sur les pixels du rep.
            s = NSSize(width: rep.pixelsWide, height: rep.pixelsHigh)
        }
        guard s.width > 0, s.height > 0 else { return NSSize(width: 320, height: 200) }
        let screen = (NSScreen.main ?? NSScreen.screens.first)?.visibleFrame.size
            ?? NSSize(width: 1440, height: 900)
        let maxW = screen.width * 0.7, maxH = screen.height * 0.7
        let scale = min(1, maxW / s.width, maxH / s.height)
        return NSSize(width: (s.width * scale).rounded(), height: (s.height * scale).rounded())
    }
}

/// Fenêtre borderless qui PEUT devenir key (sinon `makeKeyAndOrderFront` n'aurait aucun
/// effet sur une fenêtre sans barre de titre) → permet le focus et le glissé de fond.
private final class PinWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

/// Vue de contenu : dessine la capture, l'arrondit, et affiche au survol deux petits
/// boutons (fermer + copier). On DESSINE l'image directement (au lieu d'un NSImageView)
/// car NSImageView est un NSControl dont `mouseDownCanMoveWindow` vaut false : un glissé
/// sur l'image ne déplacerait alors PAS la fenêtre. Une NSView simple renvoie true →
/// glisser n'importe où sur l'image déplace bien la fenêtre.
private final class PinnedContentView: NSView {
    private let image: NSImage
    private let closeButton: NSButton
    private let copyButton: NSButton
    var onClose: (() -> Void)?
    var onCopy: (() -> Void)?
    private var trackingArea: NSTrackingArea?

    init(image: NSImage, cornerRadius: CGFloat) {
        self.image = image
        self.closeButton = PinnedContentView.makeIconButton(symbol: "xmark", tooltip: "Fermer")
        self.copyButton  = PinnedContentView.makeIconButton(symbol: "doc.on.doc", tooltip: "Copier")
        super.init(frame: .zero)

        wantsLayer = true
        layer?.cornerRadius = cornerRadius
        layer?.masksToBounds = true                // arrondit l'image dessinée
        layer?.borderWidth = 0.5                    // liseré discret pour décoller des fonds clairs
        layer?.borderColor = NSColor.white.withAlphaComponent(0.15).cgColor

        closeButton.target = self; closeButton.action = #selector(closeTapped)
        copyButton.target  = self; copyButton.action  = #selector(copyTapped)
        for b in controls {
            b.alphaValue = 0                        // masqués hors survol
            b.translatesAutoresizingMaskIntoConstraints = false
            addSubview(b)
        }
        NSLayoutConstraint.activate([
            closeButton.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 8),
            closeButton.topAnchor.constraint(equalTo: topAnchor, constant: 8),
            copyButton.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -8),
            copyButton.topAnchor.constraint(equalTo: topAnchor, constant: 8),
        ])
    }
    required init?(coder: NSCoder) { fatalError("no coder") }

    private var controls: [NSButton] { [closeButton, copyButton] }

    override var mouseDownCanMoveWindow: Bool { true }   // fond = poignée de déplacement

    override func draw(_ dirtyRect: NSRect) {
        image.draw(in: bounds, from: .zero, operation: .copy, fraction: 1)
    }

    // MARK: Survol → boutons

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let trackingArea { removeTrackingArea(trackingArea) }
        // .activeAlways : le survol fonctionne même quand l'app n'est pas active (épingle
        // posée par-dessus une autre app).
        let area = NSTrackingArea(rect: bounds,
                                  options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect],
                                  owner: self, userInfo: nil)
        addTrackingArea(area)
        trackingArea = area
    }

    override func mouseEntered(with event: NSEvent) { setControls(visible: true) }
    override func mouseExited(with event: NSEvent)  { setControls(visible: false) }

    private func setControls(visible: Bool) {
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.12
            for b in controls { b.animator().alphaValue = visible ? 1 : 0 }
        }
    }

    @objc private func closeTapped() { onClose?() }
    @objc private func copyTapped()  { onCopy?() }

    /// Petit bouton rond translucide à icône SF Symbol blanche.
    private static func makeIconButton(symbol: String, tooltip: String) -> NSButton {
        let b = NSButton()
        b.isBordered = false
        b.bezelStyle = .regularSquare
        b.imagePosition = .imageOnly
        b.toolTip = tooltip
        let cfg = NSImage.SymbolConfiguration(pointSize: 11, weight: .semibold)
        b.image = NSImage(systemSymbolName: symbol, accessibilityDescription: tooltip)?
            .withSymbolConfiguration(cfg)
        b.contentTintColor = .white
        b.wantsLayer = true
        b.layer?.backgroundColor = NSColor.black.withAlphaComponent(0.55).cgColor
        b.layer?.cornerRadius = 11
        NSLayoutConstraint.activate([
            b.widthAnchor.constraint(equalToConstant: 22),
            b.heightAnchor.constraint(equalToConstant: 22),
        ])
        return b
    }
}
