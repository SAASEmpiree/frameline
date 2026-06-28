import AppKit
import CoreImage

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
        case .pencil:    return "scribble"
        case .text:      return "textformat"
        case .pixelate:  return "mosaic"
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
        NSColor.windowBackgroundColor.setFill()
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

    private func drawText(_ m: Markup, map: (CGPoint) -> CGPoint, scaleX: CGFloat) {
        guard !m.text.isEmpty else { return }
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.boldSystemFont(ofSize: max(1, m.fontSize * scaleX)),
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
final class PreviewWindowController: NSWindowController, NSWindowDelegate {

    private let canvas: CanvasView
    private var fileURL: URL
    private let colorWell = NSColorWell()
    private let scrollView = NSScrollView()
    private let zoomLabel = NSTextField(labelWithString: "100 %")
    private var undoBtn: NSButton!
    private var redoBtn: NSButton!
    private var doneBtn: NSButton!

    /// `true` = on garde l'image ajustée à la fenêtre lors des redimensionnements.
    private var zoomFitMode = true
    private var didInitialFit = false

    /// Ordre des segments de la barre d'outils → outils.
    private let toolOrder: [MarkupTool] = [.cursor, .rectangle, .arrow, .pencil, .text, .pixelate, .crop]

    private static var retained: [PreviewWindowController] = []

    static func show(imageURL: URL) {
        guard let img = NSImage(contentsOf: imageURL) else { return }
        let controller = PreviewWindowController(image: img, url: imageURL)
        retained.append(controller)
        // Éditeur ouvert direct après une capture : installe la barre de menus (dont
        // Édition) sinon Cmd-C/V/A/Z/Q sont morts dans le champ de texte d'annotation.
        // Idempotent et n'ouvre PAS la galerie.
        MainWindowController.shared.installMainMenuIfNeeded()
        NSApp.activate(ignoringOtherApps: true)
        controller.showWindow(nil)
        controller.window?.makeKeyAndOrderFront(nil)
        controller.fitInitial()
    }

    init(image: NSImage, url: URL) {
        self.fileURL = url
        self.canvas = CanvasView(image: image)
        let topH: CGFloat = 44, bottomH: CGFloat = 30
        let maxW: CGFloat = 1100, maxH: CGFloat = 760
        let s = image.size
        let scale = min(1, min(maxW / max(s.width, 1), (maxH - topH - bottomH) / max(s.height, 1)))
        let cw = max(760, s.width * scale)
        let ch = max(320, s.height * scale)
        // AlwaysActiveWindow : feux tricolores toujours colorés (cf. MainWindowController).
        let win = AlwaysActiveWindow(contentRect: NSRect(x: 0, y: 0, width: cw, height: ch + topH + bottomH),
                                     styleMask: [.titled, .closable, .resizable],
                                     backing: .buffered, defer: false)
        win.title = "Aperçu — " + url.lastPathComponent
        win.isReleasedWhenClosed = false
        win.level = .floating
        // Même correctif que la fenêtre principale : amène l'éditeur sur le Space
        // ACTIF après une capture (openEditor) au lieu de faire basculer macOS
        // vers son ancien Space.
        win.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]
        win.minSize = NSSize(width: 760, height: 360)
        win.center()
        super.init(window: win)
        win.delegate = self
        buildUI(topHeight: topH, bottomHeight: bottomH)
        canvas.onImageChanged = { [weak self] in self?.imageDidChange() }
        canvas.onStackChanged = { [weak self] in self?.updateUndoRedo() }
        // Pendant la saisie d'un texte, on retire le « \r » du bouton « Terminé » pour
        // qu'Entrée valide le champ au lieu d'enregistrer + fermer prématurément.
        canvas.onTextEditingChanged = { [weak self] editing in self?.doneBtn.keyEquivalent = editing ? "" : "\r" }
        updateUndoRedo()
    }
    required init?(coder: NSCoder) { fatalError("no coder") }

    // MARK: - UI

    private func buildUI(topHeight: CGFloat, bottomHeight: CGFloat) {
        guard let content = window?.contentView else { return }

        // --- Barre d'outils (HAUT) ---
        let seg = NSSegmentedControl()
        seg.segmentCount = toolOrder.count
        seg.trackingMode = .selectOne
        seg.target = self
        seg.action = #selector(toolChanged)
        for (i, t) in toolOrder.enumerated() {
            if let img = NSImage(systemSymbolName: t.symbol, accessibilityDescription: t.label) {
                seg.setImage(img, forSegment: i)
                seg.setImageScaling(.scaleProportionallyDown, forSegment: i)
            } else {
                seg.setLabel(t.label, forSegment: i)
            }
            seg.setToolTip(t.label, forSegment: i)
            seg.setWidth(36, forSegment: i)
        }
        seg.selectedSegment = 0

        colorWell.color = .systemRed
        colorWell.target = self
        colorWell.action = #selector(colorChanged)
        colorWell.widthAnchor.constraint(equalToConstant: 42).isActive = true

        undoBtn = iconButton(symbol: "arrow.uturn.backward", fallback: "Annuler", action: #selector(undo as () -> Void))
        redoBtn = iconButton(symbol: "arrow.uturn.forward", fallback: "Rétablir", action: #selector(redo as () -> Void))
        let copyBtn = NSButton(title: "Copier", target: self, action: #selector(copyImage))
        doneBtn = NSButton(title: "Terminé", target: self, action: #selector(done))
        doneBtn.keyEquivalent = "\r"       // bouton par défaut (sauf pendant la saisie de texte)

        let spacer = NSView()
        spacer.setContentHuggingPriority(.init(1), for: .horizontal)

        let bar = NSStackView(views: [seg, colorWell, undoBtn, redoBtn, spacer, copyBtn, doneBtn])
        bar.orientation = .horizontal
        bar.spacing = 8
        bar.edgeInsets = NSEdgeInsets(top: 6, left: 10, bottom: 6, right: 10)
        bar.translatesAutoresizingMaskIntoConstraints = false

        // --- Zone d'image (CENTRE) : scroll view grossissant ---
        scrollView.translatesAutoresizingMaskIntoConstraints = false
        scrollView.borderType = .noBorder
        scrollView.hasVerticalScroller = true
        scrollView.hasHorizontalScroller = true
        scrollView.autohidesScrollers = true
        scrollView.drawsBackground = true
        scrollView.backgroundColor = .windowBackgroundColor
        scrollView.allowsMagnification = true
        scrollView.minMagnification = 0.1
        scrollView.maxMagnification = 8
        canvas.translatesAutoresizingMaskIntoConstraints = true
        scrollView.documentView = canvas

        // Le pincement / magnification utilisateur sort du mode « ajusté ».
        NotificationCenter.default.addObserver(self, selector: #selector(liveMagnifyEnded),
                                               name: NSScrollView.didEndLiveMagnifyNotification, object: scrollView)

        // --- Barre de zoom (BAS, optionnelle) ---
        zoomLabel.alignment = .center
        zoomLabel.font = .monospacedDigitSystemFont(ofSize: 11, weight: .regular)
        zoomLabel.widthAnchor.constraint(equalToConstant: 56).isActive = true
        let fitBtn = NSButton(title: "Ajuster", target: self, action: #selector(zoomFit))
        let actualBtn = NSButton(title: "100 %", target: self, action: #selector(zoomActual))
        for b in [fitBtn, actualBtn] { b.bezelStyle = .recessed; b.controlSize = .small }
        let zSpacerL = NSView(), zSpacerR = NSView()
        zSpacerL.setContentHuggingPriority(.init(1), for: .horizontal)
        zSpacerR.setContentHuggingPriority(.init(1), for: .horizontal)
        let zbar = NSStackView(views: [zSpacerL, fitBtn, zoomLabel, actualBtn, zSpacerR])
        zbar.orientation = .horizontal
        zbar.spacing = 8
        zbar.edgeInsets = NSEdgeInsets(top: 2, left: 10, bottom: 4, right: 10)
        zbar.translatesAutoresizingMaskIntoConstraints = false

        content.addSubview(scrollView)
        content.addSubview(bar)
        content.addSubview(zbar)
        NSLayoutConstraint.activate([
            bar.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            bar.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            bar.topAnchor.constraint(equalTo: content.topAnchor),
            bar.heightAnchor.constraint(equalToConstant: topHeight),

            zbar.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            zbar.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            zbar.bottomAnchor.constraint(equalTo: content.bottomAnchor),
            zbar.heightAnchor.constraint(equalToConstant: bottomHeight),

            scrollView.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            scrollView.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            scrollView.topAnchor.constraint(equalTo: bar.bottomAnchor),
            scrollView.bottomAnchor.constraint(equalTo: zbar.topAnchor),
        ])
    }

    private func iconButton(symbol: String, fallback: String, action: Selector) -> NSButton {
        let b: NSButton
        if let img = NSImage(systemSymbolName: symbol, accessibilityDescription: fallback) {
            b = NSButton(image: img, target: self, action: action)
        } else {
            b = NSButton(title: fallback, target: self, action: action)
        }
        b.toolTip = fallback
        b.bezelStyle = .rounded
        return b
    }

    // MARK: - Actions d'outils

    @objc private func toolChanged(_ sender: NSSegmentedControl) {
        canvas.endTextEditing()        // changer d'outil fige le texte en cours
        let idx = sender.selectedSegment
        canvas.tool = (idx >= 0 && idx < toolOrder.count) ? toolOrder[idx] : .cursor
    }
    @objc private func colorChanged(_ sender: NSColorWell) { canvas.strokeColor = sender.color }
    @objc private func undo() { canvas.undo() }
    @objc private func redo() { canvas.redo() }

    // Le menu Édition envoie undo:/redo: (sélecteurs à argument) ; on les relaie vers
    // le canevas. Distincts des undo()/redo() sans argument des boutons de la barre.
    @objc func undo(_ sender: Any?) { canvas.undo() }
    @objc func redo(_ sender: Any?) { canvas.redo() }

    // NSWindowController ne fournit PAS validateMenuItem (≠ NSResponder) : on implémente
    // NSMenuItemValidation (informelle via respondsToSelector) sans `override`.
    @objc func validateMenuItem(_ item: NSMenuItem) -> Bool {
        switch item.action {
        case #selector(undo(_:)): return canvas.canUndo
        case #selector(redo(_:)): return canvas.canRedo
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
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setData(data, forType: .png)
    }

    /// « Terminé » : fige le texte, enregistre (écrase la capture en PNG annoté), ferme.
    @objc private func done() {
        canvas.endTextEditing()
        saveImage()
        window?.close()
    }

    private func saveImage() {
        guard let data = canvas.flattenedPNGData() else { return }
        // Écrase la capture d'origine en PNG (qui porte désormais les annotations).
        let original = fileURL
        let target = (original.pathExtension.lowercased() == "png")
            ? original
            : original.deletingPathExtension().appendingPathExtension("png")
        do {
            try data.write(to: target)
            // Si l'original était en .jpg, on supprime le doublon non annoté.
            if target != original { try? FileManager.default.removeItem(at: original) }
            fileURL = target
            window?.title = "Aperçu — " + target.lastPathComponent
            // Réécriture sur place : invalide le cache vignette et reconstruit la
            // Capture (date de modif), sinon galerie + détail restent figés jusqu'au
            // relancement. CaptureLibrary est @MainActor, saveImage tourne sur le main.
            CaptureLibrary.shared.reloadFile(target)
        } catch {
            NSLog("Easy Screen — enregistrement annoté: \(error.localizedDescription)")
        }
    }

    // MARK: - Zoom

    /// Premier affichage : ajuste l'image une fois la disposition connue.
    func fitInitial() {
        guard !didInitialFit else { return }
        didInitialFit = true
        window?.layoutIfNeeded()
        zoomFit()
    }

    @objc private func zoomFit() {
        zoomFitMode = true
        scrollView.magnify(toFit: canvas.bounds)
        updateZoomLabel()
    }
    @objc private func zoomActual() {
        zoomFitMode = false
        scrollView.magnification = 1.0
        updateZoomLabel()
    }
    @objc private func liveMagnifyEnded() {
        zoomFitMode = false
        updateZoomLabel()
    }
    private func updateZoomLabel() {
        zoomLabel.stringValue = "\(Int((scrollView.magnification * 100).rounded())) %"
    }

    private func imageDidChange() {
        if zoomFitMode { scrollView.magnify(toFit: canvas.bounds) }
        updateZoomLabel()
        updateUndoRedo()
    }

    // MARK: - NSWindowDelegate

    func windowDidResize(_ notification: Notification) {
        if zoomFitMode { scrollView.magnify(toFit: canvas.bounds) }
        updateZoomLabel()
    }

    func windowWillClose(_ notification: Notification) {
        NotificationCenter.default.removeObserver(self)
        PreviewWindowController.retained.removeAll { $0 === self }
    }
}
