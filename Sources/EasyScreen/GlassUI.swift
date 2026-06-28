import AppKit

/// FONDATION « LIQUID GLASS » (parité BridgeShot).
///
/// Regroupe la palette de marque (couleurs exactes relevées dans le binaire BridgeShot :
/// #131313 / #9a9a9a / #f2f2f2) et les briques visuelles transverses : pour l'instant le
/// toast de confirmation `GlassConfirmBadge` (« Copié » / « Enregistré ») ; les keycaps et
/// barres d'aide à l'écran de l'overlay viendront s'appuyer sur la même palette.

/// Palette de marque BridgeShot.
enum GlassPalette {
    static func color(_ hex: UInt32, alpha: CGFloat = 1) -> NSColor {
        NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255,
                green: CGFloat((hex >> 8) & 0xFF) / 255,
                blue: CGFloat(hex & 0xFF) / 255,
                alpha: alpha)
    }
    static let ink = color(0x131313)     // fonds sombres
    static let mid = color(0x9a9a9a)     // textes / icônes secondaires
    static let light = color(0xf2f2f2)   // textes / surfaces claires
}

// MARK: - Toast de confirmation « glass »

/// Petit toast flottant (« Copié », « Enregistré »…) qui surgit en bas-centre de l'écran sous
/// la souris puis disparaît seul (~1,1 s). Panneau NON-ACTIVANT : n'active jamais l'app et
/// s'affiche au-dessus du plein écran (même patron que la vignette de capture). Un seul à la fois.
@MainActor
final class GlassConfirmBadge {
    static let shared = GlassConfirmBadge()
    private init() {}

    private var panel: NSPanel?
    private var hideWork: DispatchWorkItem?

    /// Affiche le toast. `symbol` = SF Symbol (✓ par défaut).
    static func show(_ text: String, symbol: String = "checkmark.circle.fill") {
        shared.present(text: text, symbol: symbol)
    }

    private func present(text: String, symbol: String) {
        hideWork?.cancel()
        panel?.orderOut(nil)

        let label = NSTextField(labelWithString: text)
        label.font = .systemFont(ofSize: 13, weight: .semibold)
        label.textColor = .white
        label.translatesAutoresizingMaskIntoConstraints = false

        let icon = NSImageView()
        let cfg = NSImage.SymbolConfiguration(pointSize: 13, weight: .semibold)
        icon.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)?
            .withSymbolConfiguration(cfg)
        icon.contentTintColor = .white
        icon.translatesAutoresizingMaskIntoConstraints = false

        let effect = NSVisualEffectView()
        effect.material = .hudWindow
        effect.blendingMode = .behindWindow
        effect.state = .active
        effect.appearance = NSAppearance(named: .darkAqua)
        effect.wantsLayer = true
        effect.layer?.cornerRadius = 11
        effect.layer?.masksToBounds = true
        effect.layer?.borderWidth = 1
        effect.layer?.borderColor = NSColor.white.withAlphaComponent(0.12).cgColor

        effect.addSubview(icon)
        effect.addSubview(label)
        NSLayoutConstraint.activate([
            icon.widthAnchor.constraint(equalToConstant: 16),
            icon.heightAnchor.constraint(equalToConstant: 16),
            icon.leadingAnchor.constraint(equalTo: effect.leadingAnchor, constant: 13),
            icon.centerYAnchor.constraint(equalTo: effect.centerYAnchor),
            label.leadingAnchor.constraint(equalTo: icon.trailingAnchor, constant: 7),
            label.trailingAnchor.constraint(equalTo: effect.trailingAnchor, constant: -14),
            label.centerYAnchor.constraint(equalTo: effect.centerYAnchor),
        ])

        let textW = label.intrinsicContentSize.width
        let w = 13 + 16 + 7 + textW + 14            // lead + icône + gap + texte + trail
        let h: CGFloat = 36

        let p = NSPanel(contentRect: NSRect(x: 0, y: 0, width: w, height: h),
                        styleMask: [.borderless, .nonactivatingPanel],
                        backing: .buffered, defer: false)
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = true
        p.level = .floating
        p.hidesOnDeactivate = false
        p.isReleasedWhenClosed = false
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        effect.frame = NSRect(x: 0, y: 0, width: w, height: h)
        effect.autoresizingMask = [.width, .height]
        p.contentView = effect

        // Bas-centre de l'écran de la souris, au-dessus du Dock.
        let loc = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { NSMouseInRect(loc, $0.frame, false) } ?? NSScreen.main
        if let vf = screen?.visibleFrame {
            p.setFrameOrigin(NSPoint(x: (vf.midX - w / 2).rounded(), y: (vf.minY + 64).rounded()))
        }
        p.alphaValue = 0
        p.orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.16
            ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)
            p.animator().alphaValue = 1
        }
        panel = p

        let work = DispatchWorkItem { [weak self] in self?.dismiss() }
        hideWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.1, execute: work)
    }

    private func dismiss() {
        guard let p = panel else { return }
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.28
            ctx.timingFunction = CAMediaTimingFunction(name: .easeIn)
            p.animator().alphaValue = 0
        }, completionHandler: { [weak self] in
            p.orderOut(nil)
            if self?.panel === p { self?.panel = nil }
        })
    }
}

// MARK: - Barre d'aide « keycaps » de l'overlay de capture

/// Barre d'aide façon BridgeShot (« Espace  Mode fenêtre   Échap  Annuler ») : fond sombre arrondi
/// + touches stylisées + libellés. Transparente à la souris (l'overlay garde tous les événements).
/// Se dimensionne toute seule (`barSize`) ; l'overlay la positionne en bas-centre.
final class GlassHintBar: NSView {
    struct Segment {
        let key: String?
        let label: String
        init(key: String? = nil, label: String) { self.key = key; self.label = label }
    }

    private let segments: [Segment]
    private let keyFont = NSFont.systemFont(ofSize: 11, weight: .semibold)
    private let labelFont = NSFont.systemFont(ofSize: 12, weight: .medium)

    private let hPad: CGFloat = 14      // marge intérieure gauche/droite
    private let segGap: CGFloat = 16    // espace entre segments
    private let keyGap: CGFloat = 7     // espace touche ↔ libellé
    private let keyPadX: CGFloat = 7    // marge horizontale dans une touche
    private let keyH: CGFloat = 20

    let barSize: NSSize

    init(segments: [Segment]) {
        self.segments = segments
        let keyAttrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 11, weight: .semibold)]
        let labAttrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 12, weight: .medium)]
        var w: CGFloat = hPad
        for (i, s) in segments.enumerated() {
            if let k = s.key {
                w += (k as NSString).size(withAttributes: keyAttrs).width + keyPadX * 2 + keyGap
            }
            w += (s.label as NSString).size(withAttributes: labAttrs).width
            if i < segments.count - 1 { w += segGap }
        }
        w += hPad
        self.barSize = NSSize(width: w.rounded(), height: 34)
        super.init(frame: NSRect(origin: .zero, size: barSize))
        wantsLayer = true
    }
    @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }   // jamais d'interception souris
    override var isFlipped: Bool { false }

    override func draw(_ dirtyRect: NSRect) {
        GlassPalette.ink.withAlphaComponent(0.82).setFill()
        let bg = NSBezierPath(roundedRect: bounds, xRadius: 10, yRadius: 10)
        bg.fill()
        NSColor.white.withAlphaComponent(0.10).setStroke()
        bg.lineWidth = 1; bg.stroke()

        let keyAttrs: [NSAttributedString.Key: Any] = [.font: keyFont, .foregroundColor: NSColor.white]
        let labAttrs: [NSAttributedString.Key: Any] = [.font: labelFont, .foregroundColor: NSColor.white.withAlphaComponent(0.92)]

        var x = hPad
        let midY = bounds.midY
        for (i, s) in segments.enumerated() {
            if let k = s.key {
                let kTextW = (k as NSString).size(withAttributes: keyAttrs).width
                let kw = kTextW + keyPadX * 2
                let keyRect = NSRect(x: x, y: midY - keyH / 2, width: kw, height: keyH)
                NSColor.white.withAlphaComponent(0.16).setFill()
                let kp = NSBezierPath(roundedRect: keyRect, xRadius: 5, yRadius: 5); kp.fill()
                NSColor.white.withAlphaComponent(0.28).setStroke()
                kp.lineWidth = 1; kp.stroke()
                let ks = (k as NSString).size(withAttributes: keyAttrs)
                (k as NSString).draw(at: NSPoint(x: keyRect.midX - ks.width / 2, y: keyRect.midY - ks.height / 2),
                                     withAttributes: keyAttrs)
                x += kw + keyGap
            }
            let ls = (s.label as NSString).size(withAttributes: labAttrs)
            (s.label as NSString).draw(at: NSPoint(x: x, y: midY - ls.height / 2), withAttributes: labAttrs)
            x += ls.width
            if i < segments.count - 1 { x += segGap }
        }
    }
}
