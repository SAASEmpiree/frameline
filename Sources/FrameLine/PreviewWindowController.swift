import AppKit
import CoreImage
import UniformTypeIdentifiers

private extension CGFloat {
    func clamped(_ lo: CGFloat, _ hi: CGFloat) -> CGFloat { Swift.max(lo, Swift.min(hi, self)) }
}

/// Outils d'annotation. L'ordre des cas est libre : la barre d'outils mappe
/// index de segment → outil via `toolOrder`, jamais via le rawValue.
private enum MarkupTool {
    case cursor, rectangle, arrow, pencil, text, pixelate, crop

    /// Symbole SF (icône de la barre) avec repli libellé si indisponible.
    var symbol: String {
        switch self {
        case .cursor:    return "cursorarrow"
        case .rectangle: return "rectangle"
        case .arrow:     return "arrow.up.right"
        case .pencil:    return "scribble.variable"
        case .text:      return "textformat"      // (non utilisé : l'outil Texte s'affiche en « Aa », cf. barre)
        case .pixelate:  return "checkerboard.rectangle"
        case .crop:      return "crop"
        }
    }
    var label: String {
        switch self {
        case .cursor:    return "Sélection"
        case .rectangle: return "Rectangle"
        case .arrow:     return "Flèche"
        case .pencil:    return "Stylo"
        case .text:      return "Texte"
        case .pixelate:  return "Pixeliser"
        case .crop:      return "Rogner"
        }
    }
}

/// Une annotation. Les géométries sont TOUJOURS en coordonnées IMAGE (origine
/// bas-gauche, unités = points-image) → stables au zoom/redimensionnement et
/// fidèles à l'aplatissement natif. `text`/`fontSize` ne servent qu'à l'outil
/// Texte ; `points` qu'au stylo libre.
private struct Markup {
    var tool: MarkupTool
    var color: NSColor
    var lineWidth: CGFloat        // points-image
    var start: CGPoint            // coordonnées image
    var end: CGPoint
    var points: [CGPoint] = []
    var text: String = ""
    var fontSize: CGFloat = 0     // points-image
}

/// Vue document (dans un NSScrollView grossissant) : affiche l'image à sa taille
/// NATIVE en pixels et capte les annotations. Comme la vue mesure exactement la
/// taille pixel de l'image, l'échelle aperçu == l'échelle d'aplatissement → ce
/// qu'on voit est ce qu'on enregistre. Le zoom est géré par la magnification du
/// scroll view, pas par cette vue (donc aucune divergence de coordonnées).
private final class CanvasView: NSView, NSTextFieldDelegate {

    private(set) var image: NSImage
    var tool: MarkupTool = .cursor
    var strokeColor: NSColor = .systemRed {
        didSet { activeTextField?.textColor = strokeColor }   // recolore le texte en cours de saisie
    }
    var lineWidth: CGFloat = 6

    /// Prévenu quand l'image de base change (rognage / annuler-rétablir d'un
    /// rognage) → le contrôleur réajuste le zoom et le scroll.
    var onImageChanged: (() -> Void)?
    /// Prévenu quand la pile annuler/rétablir évolue → MAJ de l'état des boutons.
    var onStackChanged: (() -> Void)?
    /// Prévenu quand on entre (true) / sort (false) de la saisie d'un texte → le
    /// contrôleur retire le « \r » du bouton par défaut pendant la frappe (sinon
    /// Entrée déclenche « Terminé » au lieu de valider le champ).
    var onTextEditingChanged: ((Bool) -> Void)?

    private var markups: [Markup] = []
    private var inProgress: Markup?

    // Saisie de texte en direct (champ flottant au-dessus du canevas).
    private var activeTextField: NSTextField?
    private var editingTextSize: CGFloat = 24      // points-image, fixé à l'ouverture du champ

    // Pile annuler/rétablir : snapshots (image + annotations). L'image étant une
    // référence partagée, un snapshot sans rognage est quasi gratuit.
    private struct EditorState { let image: NSImage; let markups: [Markup] }
    private var undoStack: [EditorState] = []
    private var redoStack: [EditorState] = []

    // Version pixelisée de l'image (cache), recalculée à chaque changement d'image.
    private var pixelatedCache: NSImage?

    init(image: NSImage) {
        self.image = image
        super.init(frame: CGRect(origin: .zero, size: CanvasView.pixelSize(of: image)))
    }
    required init?(coder: NSCoder) { fatalError("no coder") }

    override var isFlipped: Bool { false }   // origine bas-gauche, comme NSImage → pas de miroir

    // MARK: - Géométrie

    static func pixelSize(of image: NSImage) -> NSSize {
        if let rep = image.representations.first, rep.pixelsWide > 0, rep.pixelsHigh > 0 {
            return NSSize(width: rep.pixelsWide, height: rep.pixelsHigh)
        }
        return image.size
    }
    private var pixelSize: NSSize { CanvasView.pixelSize(of: image) }

    /// Rectangle (coords vue) où l'image est dessinée. La vue mesurant la taille
    /// pixel de l'image, c'est en pratique tout `bounds` (aspect identique) ; on
    /// conserve le calcul aspect-fit pour rester correct si jamais les pixels
    /// n'étaient pas carrés.
    private func imageRect() -> CGRect {
        let b = bounds
        let s = image.size
        guard s.width > 0, s.height > 0 else { return b }
        let scale = min(b.width / s.width, b.height / s.height)
        let w = s.width * scale, h = s.height * scale
        return CGRect(x: (b.width - w) / 2, y: (b.height - h) / 2, width: w, height: h)
    }

    private func viewToImage(_ p: CGPoint) -> CGPoint {
        let r = imageRect()
        guard r.width > 0, r.height > 0 else { return .zero }
        // Bornage dans l'image : un clic dans une éventuelle marge ne crée pas
        // d'annotation hors-cadre divergente entre aperçu et aplatissement.
        return CGPoint(x: ((p.x - r.minX) / r.width * image.size.width).clamped(0, image.size.width),
                       y: ((p.y - r.minY) / r.height * image.size.height).clamped(0, image.size.height))
    }

    private func normalizedRect(_ m: Markup) -> CGRect {
        CGRect(x: min(m.start.x, m.end.x), y: min(m.start.y, m.end.y),
               width: abs(m.end.x - m.start.x), height: abs(m.end.y - m.start.y))
    }

    // MARK: - Rendu

    override func draw(_ dirtyRect: NSRect) {
        NSColor(white: 19.0/255.0, alpha: 1).setFill()   // fond charbon #131313 (uni avec le détail, pas de bord gris)
        dirtyRect.fill()
        let r = imageRect()
        image.draw(in: r)
        let sX = image.size.width > 0 ? r.width / image.size.width : 1
        let sY = image.size.height > 0 ? r.height / image.size.height : 1
        let map: (CGPoint) -> CGPoint = { CGPoint(x: r.minX + $0.x * sX, y: r.minY + $0.y * sY) }
        for m in markups { drawMarkup(m, map: map, scaleX: sX, scaleY: sY) }
        if let m = inProgress {
            if m.tool == .crop { drawCropMask(m, map: map, scaleX: sX, scaleY: sY, area: r) }
            else { drawMarkup(m, map: map, scaleX: sX, scaleY: sY) }
        }
    }

    /// Dessine une annotation. `scaleX/scaleY` = points-destination par point-image
    /// (identiques en aperçu et à l'aplatissement → fidélité 1:1).
    private func drawMarkup(_ m: Markup, map: (CGPoint) -> CGPoint, scaleX: CGFloat, scaleY: CGFloat) {
        switch m.tool {
        case .cursor, .crop:
            return
        case .rectangle, .arrow, .pencil:
            strokeShape(m, map: map, widthScale: scaleX)
        case .pixelate:
            drawPixelate(m, map: map, scaleX: scaleX, scaleY: scaleY)
        case .text:
            drawText(m, map: map, scaleX: scaleX)
        }
    }

    private func strokeShape(_ m: Markup, map: (CGPoint) -> CGPoint, widthScale: CGFloat) {
        m.color.setStroke()
        let path = NSBezierPath()
        // Largeur en points-image × échelle → trait identique aperçu/aplatissement.
        path.lineWidth = m.lineWidth * widthScale
        path.lineCapStyle = .round
        path.lineJoinStyle = .round
        let start = map(m.start), end = map(m.end)
        switch m.tool {
        case .rectangle:
            path.appendRect(CGRect(x: min(start.x, end.x), y: min(start.y, end.y),
                                   width: abs(end.x - start.x), height: abs(end.y - start.y)))
        case .arrow:
            path.move(to: start); path.line(to: end)
            let ang = atan2(end.y - start.y, end.x - start.x)
            let head = m.lineWidth * widthScale * 3
            for a in [ang + .pi * 0.82, ang - .pi * 0.82] {
                path.move(to: end)
                path.line(to: CGPoint(x: end.x + cos(a) * head, y: end.y + sin(a) * head))
            }
        case .pencil:
            let pts = m.points.map(map)
            guard let first = pts.first else { return }
            path.move(to: first)
            for p in pts.dropFirst() { path.line(to: p) }
        default:
            return
        }
        path.stroke()
    }

    /// Polices des annotations Texte, créées UNE fois par taille puis gardées à vie (cf.
    /// `OverlayText`) : `draw(_:)` repasse ici à chaque glissé de souris ; recréer la police à
    /// chaque image expose à la course CoreText `InitShapingGlyphs` (nil inséré → abort).
    private static var textFonts: [CGFloat: NSFont] = [:]
    private static func textFont(ofSize size: CGFloat) -> NSFont {
        if let f = textFonts[size] { return f }
        let f = NSFont.boldSystemFont(ofSize: size)
        textFonts[size] = f
        return f
    }

    private func drawText(_ m: Markup, map: (CGPoint) -> CGPoint, scaleX: CGFloat) {
        guard !m.text.isEmpty else { return }
        let attrs: [NSAttributedString.Key: Any] = [
            .font: CanvasView.textFont(ofSize: max(1, (m.fontSize * scaleX).rounded())),
            .foregroundColor: m.color,
        ]
        // Contexte non-retourné (aperçu ET bitmap d'aplatissement) → texte droit,
        // `start` = coin bas-gauche du texte.
        (m.text as NSString).draw(at: map(m.start), withAttributes: attrs)
    }

    private func drawPixelate(_ m: Markup, map: (CGPoint) -> CGPoint, scaleX: CGFloat, scaleY: CGFloat) {
        guard let pix = pixelatedImage() else { return }
        let img = normalizedRect(m)                              // sous-rect en coords image
        guard img.width > 0, img.height > 0 else { return }
        let dest = CGRect(origin: map(CGPoint(x: img.minX, y: img.minY)),
                          size: CGSize(width: img.width * scaleX, height: img.height * scaleY))
        // `pix` a pour taille image.size → son repère `from` est en coords image.
        pix.draw(in: dest, from: img, operation: .sourceOver, fraction: 1,
                 respectFlipped: true, hints: nil)
    }

    private func drawCropMask(_ m: Markup, map: (CGPoint) -> CGPoint, scaleX: CGFloat, scaleY: CGFloat, area: CGRect) {
        let img = normalizedRect(m)
        let dest = CGRect(origin: map(CGPoint(x: img.minX, y: img.minY)),
                          size: CGSize(width: img.width * scaleX, height: img.height * scaleY))
        // Assombrit tout sauf la sélection → lecture « zone conservée ».
        NSColor.black.withAlphaComponent(0.45).setFill()
        let mask = NSBezierPath(rect: area)
        mask.append(NSBezierPath(rect: dest))
        mask.windingRule = .evenOdd
        mask.fill()
        NSColor.white.setStroke()
        let border = NSBezierPath(rect: dest)
        border.lineWidth = 1
        border.stroke()
    }

    /// Génère (et cache) une version pixelisée plein cadre, à résolution native.
    private func pixelatedImage() -> NSImage? {
        if let c = pixelatedCache { return c }
        guard let cg = baseCGImage() else { return nil }
        let ci = CIImage(cgImage: cg)
        // Taille de bloc proportionnelle à l'image → flou « mosaïque » homogène.
        let block = max(8, min(ci.extent.width, ci.extent.height) / 50)
        let filter = CIFilter(name: "CIPixellate", parameters: [
            kCIInputImageKey: ci,
            kCIInputScaleKey: block,
            kCIInputCenterKey: CIVector(x: ci.extent.midX, y: ci.extent.midY),
        ])
        guard let out = filter?.outputImage?.cropped(to: ci.extent),
              let outCG = CIContext(options: nil).createCGImage(out, from: ci.extent) else { return nil }
        let img = NSImage(cgImage: outCG, size: image.size)   // size en points-image → `from` cohérent
        pixelatedCache = img
        return img
    }

    private func baseCGImage() -> CGImage? {
        if let rep = image.representations.compactMap({ $0 as? NSBitmapImageRep }).first,
           let cg = rep.cgImage { return cg }
        var rect = CGRect(origin: .zero, size: image.size)
        return image.cgImage(forProposedRect: &rect, context: nil, hints: nil)
    }

    // MARK: - Souris

    override func mouseDown(with event: NSEvent) {
        commitActiveText()                                       // tout clic ailleurs valide le texte en cours
        let p = convert(event.locationInWindow, from: nil)
        switch tool {
        case .cursor:
            return
        case .text:
            beginTextEditing(at: p)
        default:
            let ip = viewToImage(p)
            inProgress = Markup(tool: tool, color: strokeColor, lineWidth: lineWidth,
                                start: ip, end: ip, points: [ip])
            needsDisplay = true
        }
    }

    override func mouseDragged(with event: NSEvent) {
        guard var m = inProgress else { return }                 // copie locale → pas d'accès exclusif chevauchant
        let p = viewToImage(convert(event.locationInWindow, from: nil))
        m.end = p
        m.points.append(p)
        inProgress = m
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        defer { needsDisplay = true }
        guard let m = inProgress else { return }
        inProgress = nil
        if m.tool == .crop {
            let r = normalizedRect(m)
            if r.width > 8, r.height > 8 { applyCrop(r) }         // ignore un micro-glissé
            return
        }
        guard isSignificant(m) else { return }                   // ignore les clics sans glissé
        pushUndo()
        markups.append(m)
    }

    private func isSignificant(_ m: Markup) -> Bool {
        switch m.tool {
        case .cursor, .crop, .text: return false
        case .pencil: return m.points.count > 1
        case .rectangle, .arrow, .pixelate: return hypot(m.end.x - m.start.x, m.end.y - m.start.y) > 3
        }
    }

    // MARK: - Saisie de texte

    private func beginTextEditing(at viewPoint: CGPoint) {
        editingTextSize = max(14, (image.size.height * 0.035).rounded())   // points-image, lisible quelle que soit l'image
        let canvasPerImage = image.size.width > 0 ? pixelSize.width / image.size.width : 1
        let field = NSTextField(frame: CGRect(x: viewPoint.x, y: viewPoint.y, width: 30, height: 24))
        field.font = NSFont.boldSystemFont(ofSize: editingTextSize * canvasPerImage)   // taille en coords vue (= pixel)
        field.textColor = strokeColor
        field.isBordered = false
        field.drawsBackground = true
        field.backgroundColor = NSColor.white.withAlphaComponent(0.18)
        field.focusRingType = .none
        field.lineBreakMode = .byClipping
        field.cell?.wraps = false
        field.cell?.isScrollable = true
        field.delegate = self
        field.sizeToFit()
        if field.frame.width < 30 { field.setFrameSize(NSSize(width: 30, height: field.frame.height)) }
        addSubview(field)
        window?.makeFirstResponder(field)
        activeTextField = field
        onTextEditingChanged?(true)
    }

    /// Public : appelé par le contrôleur avant un changement d'outil, un
    /// annuler/rétablir, une copie ou « Terminé » → fige le texte saisi.
    func endTextEditing() { commitActiveText() }

    private func commitActiveText() {
        guard let field = activeTextField else { return }
        activeTextField = nil                                    // avant removeFromSuperview → pas de ré-entrance via le delegate
        let str = field.stringValue
        let origin = field.frame.origin
        let color = field.textColor ?? strokeColor
        field.removeFromSuperview()
        if window?.firstResponder === field { window?.makeFirstResponder(self) }
        guard !str.isEmpty else { needsDisplay = true; onTextEditingChanged?(false); return }
        pushUndo()
        var m = Markup(tool: .text, color: color, lineWidth: lineWidth,
                       start: viewToImage(origin), end: viewToImage(origin))
        m.text = str
        m.fontSize = editingTextSize
        markups.append(m)
        needsDisplay = true
        onTextEditingChanged?(false)
    }

    func controlTextDidChange(_ obj: Notification) {
        guard let field = activeTextField else { return }
        let origin = field.frame.origin
        field.sizeToFit()                                        // grandit avec la saisie, ancre bas-gauche fixe
        field.setFrameOrigin(origin)
    }
    func controlTextDidEndEditing(_ obj: Notification) { commitActiveText() }

    // MARK: - Rognage

    /// Aplatit l'état courant puis rogne à la résolution native ; remplace l'image
    /// de base et repart sans annotations (elles sont déjà « cuites » dans l'image).
    private func applyCrop(_ rectImg: CGRect) {
        guard let data = flattenedPNGData(), let baked = NSImage(data: data) else { return }
        let full = pixelSize                                     // dimensions natives avant rognage
        let sx = image.size.width > 0 ? full.width / image.size.width : 1
        let sy = image.size.height > 0 ? full.height / image.size.height : 1
        let px = CGRect(x: rectImg.minX * sx, y: rectImg.minY * sy,
                        width: rectImg.width * sx, height: rectImg.height * sy)
        let outW = Int(px.width.rounded()), outH = Int(px.height.rounded())
        guard outW > 0, outH > 0,
              let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: outW, pixelsHigh: outH,
                                         bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                         isPlanar: false, colorSpaceName: .deviceRGB,
                                         bytesPerRow: 0, bitsPerPixel: 0),
              let ctx = NSGraphicsContext(bitmapImageRep: rep)
        else { return }
        pushUndo()
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = ctx
        // Décale l'image cuite pour que `px` tombe à l'origine du nouveau bitmap.
        baked.draw(in: CGRect(x: -px.minX, y: -px.minY, width: full.width, height: full.height),
                   from: .zero, operation: .copy, fraction: 1)
        NSGraphicsContext.restoreGraphicsState()
        let cropped = NSImage(size: NSSize(width: outW, height: outH))
        cropped.addRepresentation(rep)
        markups.removeAll()
        setImage(cropped)
    }

    private func setImage(_ img: NSImage) {
        image = img
        pixelatedCache = nil
        setFrameSize(CanvasView.pixelSize(of: img))
        needsDisplay = true
        onImageChanged?()
    }

    // MARK: - Annuler / Rétablir

    var canUndo: Bool { !undoStack.isEmpty }
    var canRedo: Bool { !redoStack.isEmpty }

    private func snapshot() -> EditorState { EditorState(image: image, markups: markups) }
    private func pushUndo() {
        undoStack.append(snapshot())
        redoStack.removeAll()                                    // une nouvelle action invalide la branche « rétablir »
        onStackChanged?()
    }
    func undo() {
        commitActiveText()
        guard let s = undoStack.popLast() else { return }
        redoStack.append(snapshot())
        restore(s)
        onStackChanged?()
    }
    func redo() {
        commitActiveText()
        guard let s = redoStack.popLast() else { return }
        undoStack.append(snapshot())
        restore(s)
        onStackChanged?()
    }
    /// Efface toutes les annotations posées (l'image de base est conservée) ; annulable.
    var hasMarkups: Bool { !markups.isEmpty }
    func clearAnnotations() {
        commitActiveText()
        guard !markups.isEmpty else { return }
        pushUndo()
        markups.removeAll()
        needsDisplay = true
    }
    private func restore(_ s: EditorState) {
        let imageChanged = (s.image !== image)
        markups = s.markups
        if imageChanged { setImage(s.image) } else { needsDisplay = true }
    }

    // MARK: - Aplatissement

    /// Aplatit image + annotations en PNG, à la résolution NATIVE en pixels.
    /// Les nouveaux outils (texte, pixeliser) sont rendus avec la MÊME logique que
    /// l'aperçu → ce qu'on voit est ce qu'on enregistre.
    func flattenedPNGData() -> Data? {
        let px = pixelSize
        let pw = Int(px.width), ph = Int(px.height)
        guard pw > 0, ph > 0,
              let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pw, pixelsHigh: ph,
                                         bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                         isPlanar: false, colorSpaceName: .deviceRGB,
                                         bytesPerRow: 0, bitsPerPixel: 0),
              let ctx = NSGraphicsContext(bitmapImageRep: rep)
        else { return nil }

        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = ctx
        image.draw(in: CGRect(x: 0, y: 0, width: px.width, height: px.height))
        let sx = image.size.width > 0 ? px.width / image.size.width : 1
        let sy = image.size.height > 0 ? px.height / image.size.height : 1
        let map: (CGPoint) -> CGPoint = { CGPoint(x: $0.x * sx, y: $0.y * sy) }
        for m in markups { drawMarkup(m, map: map, scaleX: sx, scaleY: sy) }
        NSGraphicsContext.restoreGraphicsState()
        return rep.representation(using: .png, properties: [:])
    }
}

/// Fenêtre flottante d'aperçu + éditeur d'annotation après une capture.
/// API publique inchangée : `PreviewWindowController.show(imageURL:)`.
/// Héberge désormais le MÊME éditeur que le détail (`MarkupEditorViewController`) → barres strictement
/// identiques (capsules flottantes façon BridgeShot), au lieu de l'ancienne barre segmentée docquée.
final class PreviewWindowController: NSWindowController, NSWindowDelegate {

    private let editor: MarkupEditorViewController
    private static var retained: [PreviewWindowController] = []

    static func show(imageURL: URL) {
        guard NSImage(contentsOf: imageURL) != nil else { return }
        let controller = PreviewWindowController(url: imageURL)
        retained.append(controller)
        // Éditeur ouvert direct après une capture : installe la barre de menus (dont Édition) sinon
        // Cmd-C/V/A/Z/Q sont morts dans le champ de texte d'annotation. Idempotent, n'ouvre PAS la galerie.
        MainWindowController.shared.installMainMenuIfNeeded()
        NSApp.activate(ignoringOtherApps: true)
        controller.showWindow(nil)
        controller.window?.makeKeyAndOrderFront(nil)
    }

    init(url: URL) {
        let editor = MarkupEditorViewController(url: url)
        editor.defaultDone = true            // « Done » = bouton par défaut (Entrée), neutralisé pendant la saisie de texte
        self.editor = editor

        let img = NSImage(contentsOf: url) ?? NSImage(size: NSSize(width: 800, height: 500))
        let s = CanvasView.pixelSize(of: img)
        let maxW: CGFloat = 1100, maxH: CGFloat = 760
        let chromeV: CGFloat = 120           // place pour les deux capsules flottantes au-dessus/dessous du canevas
        let scale = min(1, min(maxW / max(s.width, 1), (maxH - chromeV) / max(s.height, 1)))
        let cw = max(760, s.width * scale)
        let ch = max(360, s.height * scale) + chromeV
        // AlwaysActiveWindow : feux tricolores toujours colorés (cf. MainWindowController).
        let win = AlwaysActiveWindow(contentRect: NSRect(x: 0, y: 0, width: cw, height: ch),
                                     styleMask: [.titled, .closable, .resizable],
                                     backing: .buffered, defer: false)
        win.title = "Aperçu — " + url.lastPathComponent
        win.isReleasedWhenClosed = false
        win.level = .floating
        // Amène l'éditeur sur le Space ACTIF après une capture, plutôt que de basculer macOS vers l'ancien.
        win.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]
        win.minSize = NSSize(width: 760, height: 380)
        win.contentViewController = editor   // déclenche loadView (barres) ; viewDidLayout fait le premier ajustement
        win.setContentSize(NSSize(width: cw, height: ch))
        win.centerOnActiveScreen()
        super.init(window: win)
        win.delegate = self
        // En fenêtre flottante, « Done » ferme après enregistrement (dans le détail il n'y a rien à fermer).
        editor.onDone = { [weak self] in self?.window?.close() }
    }
    required init?(coder: NSCoder) { fatalError("no coder") }

    func windowWillClose(_ notification: Notification) {
        // Libération DIFFÉRÉE d'un tour : on est encore dans `close()` — souvent appelé depuis
        // l'action du bouton « Done » (clic ou Entrée) dont la fenêtre, la vue et l'éditeur sont
        // sur la pile. Relâcher le contrôleur ici désallouerait fenêtre + éditeur sous leurs pieds.
        DispatchQueue.main.async {
            PreviewWindowController.retained.removeAll { $0 === self }
        }
    }
}

// MARK: - Éditeur RÉUTILISABLE (hébergeable dans le DÉTAIL de la fenêtre principale, façon BridgeShot)

/// Même éditeur (barre d'outils HAUT + canevas zoomable + barre de zoom BAS, cf. images #10/#11/#12)
/// mais sous forme de `NSViewController` → on peut l'embarquer dans le détail de l'app (et plus seulement
/// dans une fenêtre flottante). Réutilise `CanvasView` (même fichier).
final class MarkupEditorViewController: NSViewController {
    private var canvas: CanvasView
    private(set) var fileURL: URL
    private let colorButton = EditorColorButton(color: .systemRed)
    private let scrollView = NSScrollView()
    private let zoomLabel = NSTextField(labelWithString: "100%")
    private var undoBtn: EditorToolButton!
    private var redoBtn: EditorToolButton!
    private var doneBtn: NSButton!
    private var toolButtons: [EditorToolButton] = []
    private let dimsLabel = NSTextField(labelWithString: "")
    private var colorPopover: NSPopover?
    private var zoomFitMode = true
    private var didInitialFit = false
    private let toolOrder: [MarkupTool] = [.cursor, .rectangle, .arrow, .pencil, .text, .pixelate, .crop]
    /// Modifications non encore écrites sur le fichier (annotation, rognage, annuler/rétablir).
    private var isDirty = false

    /// Fenêtre flottante (post-capture) : appelé après « Done » → ferme la fenêtre.
    var onDone: (() -> Void)?
    /// Si vrai, « Done » devient le bouton par défaut (Entrée), neutralisé pendant la saisie de texte.
    var defaultDone = false

    init(url: URL) {
        self.fileURL = url
        let img = NSImage(contentsOf: url) ?? NSImage(size: NSSize(width: 1, height: 1))
        self.canvas = CanvasView(image: img)
        super.init(nibName: nil, bundle: nil)
    }
    required init?(coder: NSCoder) { fatalError("no coder") }

    override func loadView() {
        let root = NSView(frame: NSRect(x: 0, y: 0, width: 960, height: 640))
        self.view = root
        buildUI(on: root)
        canvas.onImageChanged = { [weak self] in self?.isDirty = true; self?.imageDidChange() }
        canvas.onStackChanged = { [weak self] in self?.isDirty = true; self?.updateUndoRedo() }
        // En fenêtre flottante, « Done » est le bouton par défaut : on retire le « \r » pendant la
        // saisie d'un texte (sinon Entrée enregistre+ferme au lieu de valider le champ). Inerte si embarqué.
        canvas.onTextEditingChanged = { [weak self] editing in
            guard let self, self.defaultDone else { return }
            self.doneBtn?.keyEquivalent = editing ? "" : "\r"
        }
        if defaultDone { doneBtn?.keyEquivalent = "\r" }
        updateUndoRedo()
    }

    override func viewDidLayout() {
        super.viewDidLayout()
        if !didInitialFit {
            didInitialFit = true
            zoomFit()
        } else if zoomFitMode {
            applyFitMagnification()
            updateZoomLabel()
        }
    }

    private func buildUI(on content: NSView) {
        content.wantsLayer = true
        content.layer?.backgroundColor = NSColor(white: 19.0/255.0, alpha: 1).cgColor   // surround charbon #131313 (uni avec le détail)

        // ===================================================================
        // BARRE D'OUTILS (HAUT) — capsule sombre flottante, centrée (façon BridgeShot, image #2)
        //   [curseur rect flèche stylo Aa pixel] │ [● couleur] │ [rogner ✦] │ [↶ ↷] │ [🪄 ⋯] │ [Copy][Done]
        // ===================================================================
        let toolStack = NSStackView()
        toolStack.orientation = .horizontal
        toolStack.spacing = 3
        toolStack.alignment = .centerY

        // — Outils de dessin sélectionnables (indices 0…5 de toolOrder) —
        for i in 0...5 {
            let b = makeToolButton(toolOrder[i], index: i)
            toolButtons.append(b)
            toolStack.addArrangedSubview(b)
        }
        toolStack.addArrangedSubview(makeSeparator())

        // — Couleur : pastille ronde + chevron, ouvre une palette (remplace le NSColorWell) —
        colorButton.target = self
        colorButton.action = #selector(colorButtonClicked(_:))
        colorButton.toolTip = "Couleur"
        toolStack.addArrangedSubview(colorButton)
        toolStack.addArrangedSubview(makeSeparator())

        // — Rogner (outil sélectionnable, index 6) + Améliorer (IA) —
        let cropBtn = makeToolButton(toolOrder[6], index: 6)
        toolButtons.append(cropBtn)
        toolStack.addArrangedSubview(cropBtn)
        let sparklesBtn = EditorToolButton(symbol: "sparkles")
        sparklesBtn.toolTip = "Améliorer (IA)"; sparklesBtn.target = self; sparklesBtn.action = #selector(sparklesAction)
        toolStack.addArrangedSubview(sparklesBtn)
        toolStack.addArrangedSubview(makeSeparator())

        // — Annuler / Rétablir —
        undoBtn = EditorToolButton(symbol: "arrow.uturn.backward")
        undoBtn.toolTip = "Annuler"; undoBtn.target = self; undoBtn.action = #selector(undoAction)
        redoBtn = EditorToolButton(symbol: "arrow.uturn.forward")
        redoBtn.toolTip = "Rétablir"; redoBtn.target = self; redoBtn.action = #selector(redoAction)
        toolStack.addArrangedSubview(undoBtn)
        toolStack.addArrangedSubview(redoBtn)
        toolStack.addArrangedSubview(makeSeparator())

        // — Gomme magique + menu « … » (overflow) —
        let eraserBtn = EditorToolButton(symbol: "wand.and.stars")
        eraserBtn.toolTip = "Gomme magique"; eraserBtn.target = self; eraserBtn.action = #selector(magicEraserAction)
        let moreBtn = EditorToolButton(symbol: "ellipsis")
        moreBtn.toolTip = "Plus"; moreBtn.target = self; moreBtn.action = #selector(showMoreMenu(_:))
        toolStack.addArrangedSubview(eraserBtn)
        toolStack.addArrangedSubview(moreBtn)
        toolStack.addArrangedSubview(makeSeparator())

        // — Actions Copy / Done (pilules ; Done = primaire) —
        toolStack.addArrangedSubview(makePill(title: "Copy", symbol: "doc.on.doc", primary: false, action: #selector(copyImage)))
        doneBtn = makePill(title: "Done", symbol: "checkmark", primary: true, action: #selector(done))
        toolStack.addArrangedSubview(doneBtn)

        applySeparatorSpacing(toolStack, 7)      // un peu plus d'air autour des séparateurs (lecture en groupes)
        // Barre du haut : pilule pleinement arrondie (radius = hauteur/2) → « moins carré, plus ovale ».
        let toolBarH: CGFloat = 42
        let toolCapsule = makeCapsule(toolStack, height: toolBarH, radius: toolBarH / 2)

        // ===================================================================
        // BARRE DE ZOOM (BAS) — pilule sombre flottante, centrée (image #1)
        //   1 648 × 233 px │ ⊖ 100% ⊕ │ Fit  100%
        // ===================================================================
        dimsLabel.stringValue = pixelSizeText
        dimsLabel.font = .monospacedDigitSystemFont(ofSize: 11.5, weight: .medium)
        dimsLabel.textColor = NSColor.white.withAlphaComponent(0.78)

        zoomLabel.alignment = .center
        zoomLabel.font = .monospacedDigitSystemFont(ofSize: 11.5, weight: .semibold)
        zoomLabel.textColor = .white
        zoomLabel.widthAnchor.constraint(equalToConstant: 40).isActive = true

        let zoomOut = EditorToolButton(symbol: "minus.magnifyingglass", pointSize: 11.5, weight: .regular, side: 24)
        zoomOut.toolTip = "Dézoomer"; zoomOut.target = self; zoomOut.action = #selector(zoomOutAction)
        let zoomIn = EditorToolButton(symbol: "plus.magnifyingglass", pointSize: 11.5, weight: .regular, side: 24)
        zoomIn.toolTip = "Zoomer"; zoomIn.target = self; zoomIn.action = #selector(zoomInAction)

        // « Fit » / « 100% » : boutons texte sans fond (survol seulement), comme BridgeShot.
        let fitBtn = EditorToolButton(text: "Fit", pointSize: 11.5, weight: .medium, fixedWidth: nil, side: 24)
        fitBtn.toolTip = "Ajuster à la fenêtre"; fitBtn.target = self; fitBtn.action = #selector(zoomFit)
        let actualBtn = EditorToolButton(text: "100%", pointSize: 11.5, weight: .medium, fixedWidth: nil, side: 24)
        actualBtn.toolTip = "Taille réelle"; actualBtn.target = self; actualBtn.action = #selector(zoomActual)

        let zoomStack = NSStackView(views: [dimsLabel, makeSeparator(), zoomOut, zoomLabel, zoomIn,
                                            makeSeparator(), fitBtn, actualBtn])
        zoomStack.orientation = .horizontal
        zoomStack.spacing = 5
        zoomStack.alignment = .centerY
        applySeparatorSpacing(zoomStack, 9)
        let zoomBarH: CGFloat = 32
        let zoomCapsule = makeCapsule(zoomStack, height: zoomBarH, radius: zoomBarH / 2)

        // === CANEVAS (scroll zoomable) au centre ===
        scrollView.translatesAutoresizingMaskIntoConstraints = false
        scrollView.borderType = .noBorder
        // Clip view qui CENTRE l'image quand elle est plus petite que la zone (au lieu de la
        // coller en bas-gauche) → marges NOIRES égales tout autour, comme BridgeShot.
        let clip = CenteringClipView()
        clip.drawsBackground = true
        clip.backgroundColor = NSColor(white: 19.0/255.0, alpha: 1)
        scrollView.contentView = clip
        scrollView.hasVerticalScroller = true
        scrollView.hasHorizontalScroller = true
        scrollView.autohidesScrollers = true
        scrollView.drawsBackground = true
        scrollView.backgroundColor = NSColor(white: 19.0/255.0, alpha: 1)   // marges = charbon #131313 (jamais de gris clair)
        scrollView.allowsMagnification = true
        scrollView.minMagnification = 0.1
        scrollView.maxMagnification = 8
        canvas.translatesAutoresizingMaskIntoConstraints = true
        scrollView.documentView = canvas
        NotificationCenter.default.addObserver(self, selector: #selector(liveMagnifyEnded),
                                               name: NSScrollView.didEndLiveMagnifyNotification, object: scrollView)

        content.addSubview(scrollView)
        content.addSubview(toolCapsule)
        content.addSubview(zoomCapsule)
        NSLayoutConstraint.activate([
            toolCapsule.centerXAnchor.constraint(equalTo: content.centerXAnchor),
            toolCapsule.topAnchor.constraint(equalTo: content.topAnchor, constant: 14),
            zoomCapsule.centerXAnchor.constraint(equalTo: content.centerXAnchor),
            zoomCapsule.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -12),
            scrollView.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            scrollView.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            scrollView.topAnchor.constraint(equalTo: toolCapsule.bottomAnchor, constant: 12),
            scrollView.bottomAnchor.constraint(equalTo: zoomCapsule.topAnchor, constant: -10),
        ])
    }

    /// Dimensions PIXEL de l'image, séparateur de milliers (« 1 648 × 233 px »), comme BridgeShot.
    private var pixelSizeText: String {
        let w: Int, h: Int
        if let rep = canvas.image.representations.first, rep.pixelsWide > 0, rep.pixelsHigh > 0 {
            w = rep.pixelsWide; h = rep.pixelsHigh
        } else {
            w = Int(canvas.image.size.width); h = Int(canvas.image.size.height)
        }
        return "\(Self.grouped(w)) × \(Self.grouped(h)) px"
    }
    private static func grouped(_ n: Int) -> String {
        let f = NumberFormatter()
        f.numberStyle = .decimal
        f.groupingSeparator = "\u{202F}"      // espace fine insécable → « 1 648 »
        f.groupingSize = 3
        return f.string(from: NSNumber(value: n)) ?? "\(n)"
    }

    // MARK: Briques d'UI (capsule, séparateur, pilules, bouton d'outil)

    /// Bouton d'outil de la barre (icône SF, ou « Aa » pour l'outil Texte), câblé sur la sélection.
    private func makeToolButton(_ t: MarkupTool, index: Int) -> EditorToolButton {
        let b = (t == .text) ? EditorToolButton(text: "Aa", pointSize: 12.5, weight: .semibold)
                             : EditorToolButton(symbol: t.symbol)
        b.toolTip = t.label
        b.tag = index
        b.target = self
        b.action = #selector(toolButtonClicked(_:))
        b.isSelectedTool = (index == 0)
        return b
    }

    private func makeSeparator() -> NSView {
        let v = BarSeparator()
        v.wantsLayer = true
        v.layer?.backgroundColor = NSColor.white.withAlphaComponent(0.16).cgColor
        v.translatesAutoresizingMaskIntoConstraints = false
        v.widthAnchor.constraint(equalToConstant: 1).isActive = true
        v.heightAnchor.constraint(equalToConstant: 19).isActive = true
        return v
    }

    /// Donne un peu plus d'air des deux côtés de chaque séparateur (lecture en groupes).
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

    /// Pilule « Copy » / « Done » (Done = primaire, fond clair) — icône + libellé, survol.
    private func makePill(title: String, symbol: String, primary: Bool, action: Selector) -> NSButton {
        let b = EditorPillButton(title: title, symbol: symbol, primary: primary)
        b.target = self; b.action = action
        return b
    }

    @objc private func toolButtonClicked(_ sender: EditorToolButton) {
        canvas.endTextEditing()
        let idx = sender.tag
        canvas.tool = (idx >= 0 && idx < toolOrder.count) ? toolOrder[idx] : .cursor
        for b in toolButtons { b.isSelectedTool = (b === sender) }
    }
    @objc private func zoomOutAction() {
        zoomFitMode = false
        scrollView.magnification = max(scrollView.minMagnification, scrollView.magnification * 0.8)
        updateZoomLabel()
    }
    @objc private func zoomInAction() {
        zoomFitMode = false
        scrollView.magnification = min(scrollView.maxMagnification, scrollView.magnification * 1.25)
        updateZoomLabel()
    }
    // MARK: Couleur (palette en popover + couleur personnalisée)

    @objc private func colorButtonClicked(_ sender: EditorColorButton) {
        if let pop = colorPopover, pop.isShown { pop.close(); colorPopover = nil; return }
        let pop = NSPopover()
        pop.behavior = .transient
        pop.contentViewController = EditorColorPopoverController(
            current: canvas.strokeColor,
            onPick: { [weak self] c in self?.setColor(c); self?.colorPopover?.close() },
            onCustom: { [weak self] in self?.colorPopover?.close(); self?.openColorPanel() })
        colorPopover = pop
        pop.show(relativeTo: sender.bounds, of: sender, preferredEdge: .minY)
    }
    private func setColor(_ c: NSColor) {
        canvas.strokeColor = c
        colorButton.color = c
    }
    @objc private func openColorPanel() {
        let cp = NSColorPanel.shared
        cp.showsAlpha = false
        cp.setTarget(self)
        Self.colorPanelOwner = ObjectIdentifier(self)
        cp.setAction(#selector(colorPanelChanged(_:)))
        cp.color = canvas.strokeColor
        cp.makeKeyAndOrderFront(nil)
    }
    @objc private func colorPanelChanged(_ sender: NSColorPanel) { setColor(sender.color) }
    /// Éditeur auquel le panneau Couleurs PARTAGÉ est branché (sa cible n'est pas retenue et le
    /// panneau survit à l'éditeur : fenêtre d'aperçu fermée, autre capture choisie dans le détail).
    private static var colorPanelOwner: ObjectIdentifier?

    // MARK: Améliorer (IA) / Gomme magique — présents, branchés plus tard (cf. doc de parité)

    @objc private func sparklesAction() { GlassConfirmBadge.show("Améliorer — bientôt", symbol: "sparkles") }
    @objc private func magicEraserAction() { GlassConfirmBadge.show("Gomme magique — bientôt", symbol: "wand.and.stars") }

    // MARK: Menu « … » (overflow)

    @objc private func showMoreMenu(_ sender: NSButton) {
        let menu = NSMenu()
        let ocr = menu.addItem(withTitle: "Copier le texte (OCR)", action: #selector(ocrCopy), keyEquivalent: "")
        ocr.image = NSImage(systemSymbolName: "text.viewfinder", accessibilityDescription: nil)
        let save = menu.addItem(withTitle: "Enregistrer une copie…", action: #selector(saveCopy), keyEquivalent: "")
        save.image = NSImage(systemSymbolName: "square.and.arrow.down", accessibilityDescription: nil)
        let pin = menu.addItem(withTitle: "Épingler à l'écran", action: #selector(pinToScreen), keyEquivalent: "")
        pin.image = NSImage(systemSymbolName: "pin", accessibilityDescription: nil)
        menu.addItem(.separator())
        let reset = menu.addItem(withTitle: "Effacer les annotations", action: #selector(resetAnnotations), keyEquivalent: "")
        reset.image = NSImage(systemSymbolName: "trash", accessibilityDescription: nil)
        reset.isEnabled = canvas.hasMarkups
        for it in menu.items { it.target = self }
        let origin = NSPoint(x: 0, y: sender.bounds.maxY + 6)
        menu.popUp(positioning: nil, at: origin, in: sender)
    }
    @objc private func ocrCopy() {
        let url = fileURL
        Task { @MainActor in
            let ok = await TextRecognizer.copyText(from: url)
            GlassConfirmBadge.show(ok ? "Texte copié" : "Aucun texte détecté",
                                   symbol: ok ? "checkmark.circle.fill" : "text.viewfinder")
        }
    }
    @objc private func saveCopy() {
        canvas.endTextEditing()
        guard let data = canvas.flattenedPNGData() else { return }
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.png]
        panel.nameFieldStringValue = fileURL.deletingPathExtension().lastPathComponent + " copie.png"
        let handler: (NSApplication.ModalResponse) -> Void = { resp in
            guard resp == .OK, let url = panel.url else { return }
            do { try data.write(to: url); GlassConfirmBadge.show("Enregistré") }
            catch {
                NSLog("FrameLine — copie: \(error.localizedDescription)")
                GlassConfirmBadge.show("Échec de l'enregistrement", symbol: "exclamationmark.triangle.fill")
            }
        }
        // En feuille sur la fenêtre de l'éditeur : seul, le panneau (niveau normal) s'ouvrait
        // centré SOUS la fenêtre d'aperçu flottante, elle aussi centrée → invisible.
        if let win = view.window { panel.beginSheetModal(for: win, completionHandler: handler) }
        else { panel.begin(completionHandler: handler) }
    }
    @objc private func pinToScreen() {
        canvas.endTextEditing()
        saveImage()                       // l'épingle reflète les annotations en cours
        PinnedShotWindow.show(imageURL: fileURL)
    }
    @objc private func resetAnnotations() { canvas.clearAnnotations() }

    @objc private func undoAction() { canvas.undo() }
    @objc private func redoAction() { canvas.redo() }
    @objc func undo(_ sender: Any?) { canvas.undo() }
    @objc func redo(_ sender: Any?) { canvas.redo() }
    @objc func validateMenuItem(_ item: NSMenuItem) -> Bool {
        switch item.action {
        case #selector(undo(_:)): return canvas.canUndo
        case #selector(redo(_:)): return canvas.canRedo
        // Le menu « … » s'auto-active : sans ce cas, `isEnabled = hasMarkups` posé à sa
        // construction était écrasé et « Effacer les annotations » restait toujours actif.
        case #selector(resetAnnotations): return canvas.hasMarkups
        default: return true
        }
    }
    private func updateUndoRedo() {
        undoBtn?.isEnabled = canvas.canUndo
        redoBtn?.isEnabled = canvas.canRedo
    }
    @objc private func copyImage() {
        canvas.endTextEditing()
        guard let data = canvas.flattenedPNGData() else { return }
        let pb = NSPasteboard.general; pb.clearContents(); pb.setData(data, forType: .png)
        GlassConfirmBadge.show("Copié")
    }
    @objc private func done() {
        canvas.endTextEditing()
        // Échec d'écriture → on NE ferme PAS : fermer jetterait les annotations en annonçant « Enregistré ».
        guard saveImage() else {
            GlassConfirmBadge.show("Échec de l'enregistrement", symbol: "exclamationmark.triangle.fill")
            return
        }
        GlassConfirmBadge.show("Enregistré")
        onDone?()                          // fenêtre flottante : ferme après enregistrement
    }
    /// Écrit l'état annoté sur le fichier. `false` = rien n'a pu être écrit.
    @discardableResult
    private func saveImage() -> Bool {
        guard let png = canvas.flattenedPNGData() else { return false }
        let original = fileURL
        let ext = original.pathExtension.lowercased()
        // Réglage « format JPG » : on réécrit en JPEG SUR PLACE (même qualité que la capture,
        // cf. ScreenCapturer.write) au lieu de convertir en .png et de supprimer l'original
        // (la galerie gardait alors une sélection sur un fichier disparu).
        let isJPG = (ext == "jpg" || ext == "jpeg")
        let data: Data
        if isJPG {
            guard let rep = NSBitmapImageRep(data: png),
                  let jpg = rep.representation(using: .jpeg, properties: [.compressionFactor: 0.92])
            else { return false }
            data = jpg
        } else {
            data = png
        }
        let target = (ext == "png" || isJPG) ? original
            : original.deletingPathExtension().appendingPathExtension("png")
        do {
            try data.write(to: target)
            if target != original { try? FileManager.default.removeItem(at: original) }
            fileURL = target
            isDirty = false
            CaptureLibrary.shared.reloadFile(target)
            return true
        } catch {
            NSLog("FrameLine — enregistrement annoté: \(error.localizedDescription)")
            return false
        }
    }
    /// Éditeur EMBARQUÉ démonté (autre capture sélectionnée, nouvelle capture arrivée) : on écrit
    /// les annotations en cours au lieu de les jeter sans prévenir.
    func saveIfEdited() {
        canvas.endTextEditing()
        guard isDirty else { return }
        // Capture mise à la corbeille / supprimée / déplacée entre-temps : surtout ne pas la recréer.
        guard FileManager.default.fileExists(atPath: fileURL.path) else { isDirty = false; return }
        let oldDate = (try? fileURL.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
        guard saveImage() else {
            GlassConfirmBadge.show("Échec de l'enregistrement", symbol: "exclamationmark.triangle.fill")
            return
        }
        // Enregistrement implicite : la capture garde sa place dans la galerie (sinon elle passait
        // devant la capture qu'on vient de prendre). +1 ms pour que les vues relisent quand même L×H.
        if let oldDate {
            try? FileManager.default.setAttributes([.modificationDate: oldDate.addingTimeInterval(0.001)],
                                                   ofItemAtPath: fileURL.path)
            CaptureLibrary.shared.reloadFile(fileURL)
        }
    }
    @objc private func zoomFit() { zoomFitMode = true; applyFitMagnification(); updateZoomLabel() }
    @objc private func zoomActual() { zoomFitMode = false; scrollView.magnification = 1.0; updateZoomLabel() }
    @objc private func liveMagnifyEnded() { zoomFitMode = false; updateZoomLabel() }
    private func updateZoomLabel() { zoomLabel.stringValue = "\(Int((scrollView.magnification * 100).rounded()))%" }

    /// Ajuste l'image dans la zone en gardant une MARGE noire tout autour (jamais collée aux
    /// bords gauche/droite) ; le `CenteringClipView` la centre ensuite → bandes noires égales.
    private func applyFitMagnification() {
        let pad: CGFloat = 22
        let avail = scrollView.bounds.insetBy(dx: pad, dy: pad).size
        let doc = canvas.bounds.size
        guard avail.width > 0, avail.height > 0, doc.width > 0, doc.height > 0 else { return }
        let mag = min(avail.width / doc.width, avail.height / doc.height)
        scrollView.magnification = max(scrollView.minMagnification, min(scrollView.maxMagnification, mag))
    }

    private func imageDidChange() {
        if zoomFitMode { applyFitMagnification() }
        updateZoomLabel(); updateUndoRedo()
        dimsLabel.stringValue = pixelSizeText            // les dimensions changent après un rognage
    }
    deinit {
        NotificationCenter.default.removeObserver(self)
        // Débranche le panneau Couleurs s'il vise encore cet éditeur → plus de message envoyé à
        // un objet libéré quand on bouge la roue après la fermeture.
        if Self.colorPanelOwner == ObjectIdentifier(self) {
            Self.colorPanelOwner = nil
            if NSColorPanel.sharedColorPanelExists {
                NSColorPanel.shared.setTarget(nil)
                NSColorPanel.shared.setAction(nil)
            }
        }
    }
}

// MARK: - Clip view centrant (image centrée, bandes noires égales — façon BridgeShot)

/// `NSClipView` qui CENTRE le document quand il est plus petit que la zone visible,
/// au lieu de l'ancrer en bas-gauche (comportement par défaut). Fonctionne avec la
/// magnification du scroll view : `constrainBoundsRect` raisonne dans le repère
/// (déjà mis à l'échelle) du clip view.
final class CenteringClipView: NSClipView {
    override func constrainBoundsRect(_ proposedBounds: NSRect) -> NSRect {
        var rect = super.constrainBoundsRect(proposedBounds)
        guard let doc = documentView else { return rect }
        let docFrame = doc.frame
        if rect.width > docFrame.width {
            rect.origin.x = (docFrame.width - rect.width) / 2.0          // marges noires égales gauche/droite
        }
        if rect.height > docFrame.height {
            rect.origin.y = (docFrame.height - rect.height) / 2.0        // marges noires égales haut/bas
        }
        return rect
    }
}

// MARK: - Bouton d'icône de la barre d'outils (état sélectionné + survol, façon BridgeShot)

/// Bouton icône carré-arrondi : fond clair translucide quand l'outil est ACTIF, plus léger au survol,
/// icône grisée quand désactivé (annuler/rétablir). C'est la brique de la barre d'outils en capsule.
/// Variante texte (`init(text:)`) pour « Aa » (outil Texte) et « Fit » / « 100% » (barre de zoom).
final class EditorToolButton: NSButton {
    var isSelectedTool = false { didSet { refreshAppearance() } }
    private var hovering = false
    private var trk: NSTrackingArea?
    private var titleText: String?
    private var titleFont: NSFont = .systemFont(ofSize: 13, weight: .medium)
    private var flexibleWidth = false

    /// Bouton icône (outils, annuler/rétablir, zoom +/-). `side` = côté du carré.
    init(symbol: String, pointSize: CGFloat = 13, weight: NSFont.Weight = .medium, side: CGFloat = 26) {
        super.init(frame: NSRect(x: 0, y: 0, width: side, height: side))
        isBordered = false
        imagePosition = .imageOnly
        let cfg = NSImage.SymbolConfiguration(pointSize: pointSize, weight: weight)
        image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)?.withSymbolConfiguration(cfg)
        image?.isTemplate = true
        configureChrome(fixedWidth: side, side: side)
    }

    /// Bouton texte. `fixedWidth == nil` → la largeur épouse le texte (avec marge horizontale).
    init(text: String, pointSize: CGFloat = 12, weight: NSFont.Weight = .semibold, fixedWidth: CGFloat? = 26, side: CGFloat = 26) {
        super.init(frame: NSRect(x: 0, y: 0, width: side, height: side))
        isBordered = false
        imagePosition = .noImage
        titleText = text
        titleFont = .systemFont(ofSize: pointSize, weight: weight)
        flexibleWidth = (fixedWidth == nil)
        configureChrome(fixedWidth: fixedWidth, side: side)
    }
    required init?(coder: NSCoder) { fatalError("no coder") }

    private func configureChrome(fixedWidth: CGFloat?, side: CGFloat) {
        wantsLayer = true
        layer?.cornerRadius = 6
        layer?.masksToBounds = true
        translatesAutoresizingMaskIntoConstraints = false
        if let w = fixedWidth { widthAnchor.constraint(equalToConstant: w).isActive = true }
        heightAnchor.constraint(equalToConstant: side).isActive = true
        refreshAppearance()
    }

    override var intrinsicContentSize: NSSize {
        guard flexibleWidth else { return super.intrinsicContentSize }
        var s = super.intrinsicContentSize
        s.width += 18           // marge horizontale pour les boutons texte qui épousent le contenu
        return s
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let t = trk { removeTrackingArea(t) }
        let t = NSTrackingArea(rect: bounds, options: [.activeAlways, .mouseEnteredAndExited, .inVisibleRect],
                               owner: self, userInfo: nil)
        addTrackingArea(t); trk = t
    }
    override func mouseEntered(with event: NSEvent) { hovering = true; refreshAppearance() }
    override func mouseExited(with event: NSEvent) { hovering = false; refreshAppearance() }
    override var isEnabled: Bool { didSet { refreshAppearance() } }

    /// Indicateur de sélection dessiné en CARRÉ centré (côté = min des dimensions) → toujours carré,
    /// jamais rectangulaire, quelle que soit la largeur du bouton. Blanc opaque quand sélectionné.
    override func draw(_ dirtyRect: NSRect) {
        let side = min(bounds.width, bounds.height)
        let sq = NSRect(x: (bounds.width - side) / 2, y: (bounds.height - side) / 2, width: side, height: side)
        if isEnabled && isSelectedTool {
            NSColor.white.withAlphaComponent(0.97).setFill()           // « bien bien blanc » + opaque
            NSBezierPath(roundedRect: sq, xRadius: 6, yRadius: 6).fill()
        } else if isEnabled && hovering {
            NSColor.white.withAlphaComponent(0.13).setFill()
            NSBezierPath(roundedRect: sq, xRadius: 6, yRadius: 6).fill()
        }
        super.draw(dirtyRect)                                          // l'icône / le texte par-dessus
    }

    private func refreshAppearance() {
        wantsLayer = true
        layer?.backgroundColor = NSColor.clear.cgColor                 // le fond (carré) est tracé dans draw(_:)
        // icône/texte : SOMBRE quand sélectionné (lisible sur le carré blanc), sinon blanc VIF.
        let fg: NSColor = !isEnabled ? NSColor.white.withAlphaComponent(0.30)
            : isSelectedTool ? NSColor(white: 0.10, alpha: 1)
            : hovering ? .white
            : NSColor.white.withAlphaComponent(0.95)
        contentTintColor = fg
        if let t = titleText {
            attributedTitle = NSAttributedString(string: t, attributes: [.foregroundColor: fg, .font: titleFont])
        }
        needsDisplay = true
    }
}

// MARK: - Séparateur vertical de barre (repérable pour l'espacement en groupes)

final class BarSeparator: NSView {}

// MARK: - Bouton couleur (pastille ronde + chevron, façon BridgeShot)

/// Pastille ronde remplie de la couleur courante + petit chevron « ▾ » → ouvre une palette.
/// Remplace le `NSColorWell` carré par l'élément exact de BridgeShot (image #2).
final class EditorColorButton: NSButton {
    var color: NSColor { didSet { needsDisplay = true } }
    private var hovering = false
    private var trk: NSTrackingArea?

    init(color: NSColor) {
        self.color = color
        super.init(frame: NSRect(x: 0, y: 0, width: 26, height: 26))
        isBordered = false
        title = ""
        wantsLayer = true
        layer?.cornerRadius = 6
        translatesAutoresizingMaskIntoConstraints = false
        widthAnchor.constraint(equalToConstant: 26).isActive = true
        heightAnchor.constraint(equalToConstant: 26).isActive = true
    }
    required init?(coder: NSCoder) { fatalError("no coder") }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let t = trk { removeTrackingArea(t) }
        let t = NSTrackingArea(rect: bounds, options: [.activeAlways, .mouseEnteredAndExited, .inVisibleRect],
                               owner: self, userInfo: nil)
        addTrackingArea(t); trk = t
    }
    override func mouseEntered(with event: NSEvent) { hovering = true; needsDisplay = true }
    override func mouseExited(with event: NSEvent) { hovering = false; needsDisplay = true }

    override func draw(_ dirtyRect: NSRect) {
        if hovering {
            NSColor.white.withAlphaComponent(0.09).setFill()
            NSBezierPath(roundedRect: bounds, xRadius: 6, yRadius: 6).fill()
        }
        // Disque de couleur, légèrement décalé vers le haut-gauche pour loger le chevron.
        let d: CGFloat = 15
        let circle = NSRect(x: (bounds.width - d) / 2 - 1, y: (bounds.height - d) / 2 + 1, width: d, height: d)
        color.setFill()
        NSBezierPath(ovalIn: circle).fill()
        // Liseré clair (lisibilité du blanc/noir sur fond sombre).
        NSColor.white.withAlphaComponent(0.25).setStroke()
        let ring = NSBezierPath(ovalIn: circle.insetBy(dx: 0.5, dy: 0.5)); ring.lineWidth = 1; ring.stroke()
        // Chevron « ▾ » en bas-droite.
        let cx = circle.maxX - 1, cy = circle.minY - 1
        let chev = NSBezierPath()
        chev.move(to: NSPoint(x: cx - 3.2, y: cy + 2.2))
        chev.line(to: NSPoint(x: cx, y: cy - 1.0))
        chev.line(to: NSPoint(x: cx + 3.2, y: cy + 2.2))
        chev.lineWidth = 1.6; chev.lineCapStyle = .round; chev.lineJoinStyle = .round
        NSColor.white.withAlphaComponent(0.9).setStroke()
        chev.stroke()
    }
}

// MARK: - Pilule d'action (Copy / Done) — icône + libellé, survol, marge interne

/// Bouton pilule à coins arrondis. `primary == true` → fond clair, texte noir (« Done »).
final class EditorPillButton: NSButton {
    private let primary: Bool
    private var hovering = false
    private var trk: NSTrackingArea?

    init(title: String, symbol: String, primary: Bool) {
        self.primary = primary
        super.init(frame: .zero)
        isBordered = false
        imagePosition = .imageLeading
        imageHugsTitle = true
        let cfg = NSImage.SymbolConfiguration(pointSize: 10.5, weight: .semibold)
        image = NSImage(systemSymbolName: symbol, accessibilityDescription: title)?.withSymbolConfiguration(cfg)
        image?.isTemplate = true
        let fg: NSColor = primary ? .black : NSColor.white.withAlphaComponent(0.92)
        contentTintColor = fg
        attributedTitle = NSAttributedString(string: " " + title,
            attributes: [.foregroundColor: fg, .font: NSFont.systemFont(ofSize: 12, weight: .semibold)])
        wantsLayer = true
        layer?.masksToBounds = true
        translatesAutoresizingMaskIntoConstraints = false
        heightAnchor.constraint(equalToConstant: 27).isActive = true
        layer?.cornerRadius = 13.5      // pilule pleinement arrondie (façon BridgeShot)
        refresh()
    }
    required init?(coder: NSCoder) { fatalError("no coder") }

    override var intrinsicContentSize: NSSize {
        var s = super.intrinsicContentSize
        s.width += 20           // marge horizontale (icône + texte respirent)
        return s
    }
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let t = trk { removeTrackingArea(t) }
        let t = NSTrackingArea(rect: bounds, options: [.activeAlways, .mouseEnteredAndExited, .inVisibleRect],
                               owner: self, userInfo: nil)
        addTrackingArea(t); trk = t
    }
    override func mouseEntered(with event: NSEvent) { hovering = true; refresh() }
    override func mouseExited(with event: NSEvent) { hovering = false; refresh() }

    private func refresh() {
        let bg: NSColor = primary
            ? NSColor.white.withAlphaComponent(hovering ? 1.0 : 0.95)
            : NSColor.white.withAlphaComponent(hovering ? 0.16 : 0.10)
        layer?.backgroundColor = bg.cgColor
    }
}

// MARK: - Palette de couleurs (popover : 9 pastilles + « Personnalisé… »)

/// Petite grille de couleurs ouverte par `EditorColorButton`. Reproduit la logique « couleur + dropdown »
/// de BridgeShot avec des préréglages + une entrée vers le sélecteur système.
final class EditorColorPopoverController: NSViewController {
    private let current: NSColor
    private let onPick: (NSColor) -> Void
    private let onCustom: () -> Void
    private let presets: [NSColor] = [.systemRed, .systemOrange, .systemYellow,
                                      .systemGreen, .systemBlue, .systemPurple,
                                      .systemPink, .white, .black]

    init(current: NSColor, onPick: @escaping (NSColor) -> Void, onCustom: @escaping () -> Void) {
        self.current = current; self.onPick = onPick; self.onCustom = onCustom
        super.init(nibName: nil, bundle: nil)
    }
    required init?(coder: NSCoder) { fatalError("no coder") }

    override func loadView() {
        let grid = NSStackView()
        grid.orientation = .vertical
        grid.spacing = 8
        grid.alignment = .leading
        grid.translatesAutoresizingMaskIntoConstraints = false

        var row: NSStackView!
        for (i, c) in presets.enumerated() {
            if i % 3 == 0 {
                row = NSStackView(); row.orientation = .horizontal; row.spacing = 8
                grid.addArrangedSubview(row)
            }
            let sw = RoundSwatchButton(color: c, selected: EditorColorPopoverController.sameColor(c, current))
            sw.target = self; sw.action = #selector(swatchTapped(_:))
            row.addArrangedSubview(sw)
        }
        let custom = NSButton(title: "Personnalisé…", target: self, action: #selector(customTapped))
        custom.bezelStyle = .rounded
        custom.controlSize = .small
        grid.addArrangedSubview(custom)

        let root = NSView()
        root.addSubview(grid)
        NSLayoutConstraint.activate([
            grid.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 12),
            grid.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -12),
            grid.topAnchor.constraint(equalTo: root.topAnchor, constant: 12),
            grid.bottomAnchor.constraint(equalTo: root.bottomAnchor, constant: -12),
        ])
        self.view = root
    }

    @objc private func swatchTapped(_ sender: RoundSwatchButton) { onPick(sender.swatchColor) }
    @objc private func customTapped() { onCustom() }

    /// Égalité de couleur tolérante (comparée en sRGB) → repère le préréglage actif.
    static func sameColor(_ a: NSColor, _ b: NSColor) -> Bool {
        guard let x = a.usingColorSpace(.sRGB), let y = b.usingColorSpace(.sRGB) else { return false }
        return abs(x.redComponent - y.redComponent) < 0.02
            && abs(x.greenComponent - y.greenComponent) < 0.02
            && abs(x.blueComponent - y.blueComponent) < 0.02
    }
}

/// Pastille ronde cliquable (préréglage de couleur) avec anneau de sélection.
final class RoundSwatchButton: NSButton {
    let swatchColor: NSColor
    private let selected: Bool
    init(color: NSColor, selected: Bool) {
        self.swatchColor = color; self.selected = selected
        super.init(frame: NSRect(x: 0, y: 0, width: 26, height: 26))
        isBordered = false; title = ""
        translatesAutoresizingMaskIntoConstraints = false
        widthAnchor.constraint(equalToConstant: 26).isActive = true
        heightAnchor.constraint(equalToConstant: 26).isActive = true
    }
    required init?(coder: NSCoder) { fatalError("no coder") }
    override func draw(_ dirtyRect: NSRect) {
        let inset: CGFloat = selected ? 3 : 1
        let disc = bounds.insetBy(dx: inset, dy: inset)
        swatchColor.setFill()
        NSBezierPath(ovalIn: disc).fill()
        NSColor.black.withAlphaComponent(0.25).setStroke()
        let edge = NSBezierPath(ovalIn: disc.insetBy(dx: 0.5, dy: 0.5)); edge.lineWidth = 1; edge.stroke()
        if selected {
            NSColor.white.setStroke()
            let ring = NSBezierPath(ovalIn: bounds.insetBy(dx: 0.75, dy: 0.75)); ring.lineWidth = 1.5; ring.stroke()
        }
    }
}
