import AppKit
import ScreenCaptureKit

/// Overlay de sélection de ZONE « façon BridgeShot » :
///  • VOILE SOMBRE sur tout l'écran, troué par un rectangle CLAIR sur la zone choisie + badge dimensions.
///  • LOUPE (zoom 8×) qui suit le curseur pendant la sélection, avec « #RRGGBB · x, y ».
///
/// FIABILITÉ : le glisser est suivi par POLLING de l'état MATÉRIEL de la souris
/// (`NSEvent.mouseLocation` + `NSEvent.pressedMouseButtons`), PAS par la livraison d'events à une
/// fenêtre non-activante (qui « ratait » le glisser → « ça ne prend pas le screen »). La fenêtre ne
/// sert qu'à DESSINER et à AVALER les clics.
///
/// La CAPTURE est faite par le moteur NATIF `screencapture -R` (orchestrateur) : on renvoie juste le
/// rectangle GLOBAL (coords Cocoa) ou nil.
@MainActor
final class RegionSelectOverlay {
    static let shared = RegionSelectOverlay()
    private init() {}

    private var windows: [NSWindow] = []
    private var views: [VeilDimView] = []
    private var pollTimer: Timer?
    private var keyMonitor: Any?
    private var completion: ((NSRect?) -> Void)?
    private var previousApp: NSRunningApplication?

    private var dragStart: NSPoint?
    private var dragging = false
    private var finished = false
    private var starting = false

    var isPresenting: Bool { !windows.isEmpty || starting }

    private var unionFrame: NSRect {
        NSScreen.screens.dropFirst().reduce(NSScreen.screens.first?.frame ?? .zero) { $0.union($1.frame) }
    }

    /// `completion` reçoit le rectangle sélectionné en coords GLOBALES Cocoa (bas-gauche), ou nil.
    func begin(completion: @escaping (NSRect?) -> Void) {
        guard windows.isEmpty, self.completion == nil, !starting else { completion(nil); return }
        self.completion = completion
        self.finished = false
        self.dragging = false
        self.dragStart = nil
        self.starting = true
        self.previousApp = NSWorkspace.shared.frontmostApplication
        ESLog.log("RegionSelectOverlay.begin")
        Task { await self.present() }
    }

    private func present() async {
        // Snapshot SCK par écran (pour la loupe). Si SCK échoue, on continue SANS loupe (capture OK quand même).
        var snaps: [CGDirectDisplayID: CGImage] = [:]
        if let content = try? await ScreenCapturer.shareableContent() {
            let myPID = getpid()
            let own = content.windows.filter { $0.owningApplication?.processID == myPID }
            for d in content.displays {
                if let img = try? await ScreenCapturer.captureDisplay(d, excluding: own) { snaps[d.displayID] = img }
            }
        }
        guard starting, !finished else { return }
        starting = false

        for screen in NSScreen.screens {
            let snap = snaps[screen.es_displayID]
            let view = VeilDimView(frame: NSRect(origin: .zero, size: screen.frame.size), snapshot: snap)
            let win = OverlayPanel(contentRect: screen.frame,
                                   styleMask: [.borderless, .nonactivatingPanel],
                                   backing: .buffered, defer: false)
            win.level = NSWindow.Level(rawValue: Int(CGShieldingWindowLevel()))
            win.backgroundColor = .clear
            win.isOpaque = false
            win.hasShadow = false
            win.isReleasedWhenClosed = false
            win.hidesOnDeactivate = false
            win.ignoresMouseEvents = false                 // avale les clics (sinon ils cliquent l'app derrière)
            win.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
            win.contentView = view
            win.setFrame(screen.frame, display: true)
            windows.append(win)
            views.append(view)
        }

        let mouse = NSEvent.mouseLocation
        let keyWin = windows.first { $0.frame.contains(mouse) } ?? windows.first
        for w in windows { w.orderFrontRegardless() }
        keyWin?.makeKey()
        NSApp.activate(ignoringOtherApps: true)
        NSCursor.crosshair.set()
        ESLog.log("RegionSelectOverlay SHOWN (\(windows.count) panels, \(snaps.count) snapshots)")

        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] e in
            if e.keyCode == 53 { self?.cancel(); return nil }      // Échap
            return e
        }
        let t = Timer(timeInterval: 1.0 / 120.0, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.poll() }
        }
        RunLoop.main.add(t, forMode: .common)
        pollTimer = t
    }

    // MARK: - Polling (état matériel souris)

    private func poll() {
        guard !finished else { return }
        NSCursor.crosshair.set()
        let down = (NSEvent.pressedMouseButtons & 0x1) != 0
        let loc = NSEvent.mouseLocation
        if dragging {
            if down { updateSelection(to: loc) }
            else { finishSelection(at: loc) }
        } else if down {
            dragStart = loc
            dragging = true
            ESLog.log("RegionSelectOverlay drag start \(Int(loc.x)),\(Int(loc.y))")
            updateSelection(to: loc)
        }
    }

    private func currentRect(to loc: NSPoint) -> NSRect {
        guard let s = dragStart else { return .zero }
        let r = NSRect(x: min(s.x, loc.x), y: min(s.y, loc.y),
                       width: abs(loc.x - s.x), height: abs(loc.y - s.y))
        return r.intersection(unionFrame)
    }

    private func updateSelection(to loc: NSPoint) {
        let sel = currentRect(to: loc)
        let dim = "\(Int(sel.width.rounded())) × \(Int(sel.height.rounded()))"
        let cursorFrame = NSScreen.screens.first { NSMouseInRect(loc, $0.frame, false) }?.frame
        for (i, screen) in NSScreen.screens.enumerated() where i < views.count {
            let local = sel.offsetBy(dx: -screen.frame.minX, dy: -screen.frame.minY)
            let hasSel = sel.width >= 1 && sel.height >= 1
            let onCursor = (screen.frame == cursorFrame)
            views[i].update(selection: hasSel ? local : nil, dimText: onCursor ? dim : nil)
            if onCursor {
                let lp = NSPoint(x: loc.x - screen.frame.minX, y: loc.y - screen.frame.minY)
                views[i].showLoupe(atLocal: lp, globalTopLeft: globalTopLeft(loc))
            } else {
                views[i].hideLoupe()
            }
        }
    }

    private func globalTopLeft(_ g: NSPoint) -> NSPoint {
        let primaryH = NSScreen.screens.first { $0.frame.origin == .zero }?.frame.height
            ?? NSScreen.main?.frame.height ?? g.y
        return NSPoint(x: g.x, y: primaryH - g.y)
    }

    private func finishSelection(at loc: NSPoint) {
        let sel = currentRect(to: loc)
        ESLog.log("RegionSelectOverlay release → sel \(Int(sel.width))×\(Int(sel.height))")
        if sel.width < 4 || sel.height < 4 { finish(nil); return }
        finish(sel)
    }

    private func cancel() { finish(nil) }

    private func finish(_ rect: NSRect?) {
        guard !finished else { return }
        finished = true
        starting = false
        pollTimer?.invalidate(); pollTimer = nil
        if let m = keyMonitor { NSEvent.removeMonitor(m); keyMonitor = nil }
        for w in windows { w.orderOut(nil) }
        windows.removeAll(); views.removeAll()
        dragStart = nil; dragging = false
        NSCursor.arrow.set()
        if NSApp.isActive, let prev = previousApp,
           prev.processIdentifier != getpid(), !prev.isTerminated {
            prev.activate()
        }
        previousApp = nil
        ESLog.log("RegionSelectOverlay.finish (\(rect != nil ? "rect" : "nil"))")
        let c = completion; completion = nil
        c?(rect)
    }
}

private final class OverlayPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

// MARK: - Vue : voile sombre + trou clair + dimensions + loupe

private final class VeilDimView: NSView {
    private var selection: NSRect?
    private var dimText: String?
    private let snapshot: CGImage?
    private let bitmapRep: NSBitmapImageRep?
    private let loupe = LoupeView(frame: .zero)

    init(frame: NSRect, snapshot: CGImage?) {
        self.snapshot = snapshot
        self.bitmapRep = snapshot.map { NSBitmapImageRep(cgImage: $0) }
        super.init(frame: frame)
        wantsLayer = true
        loupe.wantsLayer = true
        loupe.isHidden = true
        loupe.frame = NSRect(origin: .zero, size: LoupeView.size)
        addSubview(loupe)
    }
    @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { false }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { self }   // avale tous les clics
    override func resetCursorRects() { addCursorRect(bounds, cursor: .crosshair) }
    override func mouseDown(with event: NSEvent) {}               // suivi par polling, pas par events
    override func mouseDragged(with event: NSEvent) {}
    override func mouseUp(with event: NSEvent) {}

    private var scaleX: CGFloat { (snapshot != nil && bounds.width > 0) ? CGFloat(snapshot!.width) / bounds.width : 1 }
    private var scaleY: CGFloat { (snapshot != nil && bounds.height > 0) ? CGFloat(snapshot!.height) / bounds.height : 1 }

    func update(selection: NSRect?, dimText: String?) {
        self.selection = selection
        self.dimText = dimText
        needsDisplay = true
    }

    func showLoupe(atLocal p: NSPoint, globalTopLeft: NSPoint) {
        guard let snapshot, let bitmapRep else { return }     // pas de snapshot → pas de loupe
        loupe.isHidden = false
        loupe.refresh(snapshot: snapshot, bitmapRep: bitmapRep, scaleX: scaleX, scaleY: scaleY,
                      viewHeight: bounds.height, cursor: p, globalTopLeft: globalTopLeft)
        placeLoupe(near: p)
    }
    func hideLoupe() { loupe.isHidden = true }

    private func placeLoupe(near p: NSPoint) {
        let s = LoupeView.size
        let gap: CGFloat = 16
        let inset = (s.width - LoupeView.diameter) / 2
        var x = p.x + gap - inset
        var y = p.y - gap - s.height
        if p.x + gap + LoupeView.diameter > bounds.maxX - 4 { x = p.x - gap - LoupeView.diameter - inset }
        if y < bounds.minY + 4 { y = p.y + gap }
        x = min(max(x, bounds.minX + 4), bounds.maxX - s.width - 4)
        y = min(max(y, bounds.minY + 4), bounds.maxY - s.height - 4)
        loupe.frame = NSRect(x: x.rounded(), y: y.rounded(), width: s.width, height: s.height)
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor(white: 0, alpha: 0.5).setFill()                  // voile SOMBRE
        guard let sel = selection, sel.width > 0, sel.height > 0 else {
            NSBezierPath(rect: bounds).fill()                    // avant sélection : tout assombri
            return
        }
        let path = NSBezierPath(rect: bounds)
        path.appendRect(sel)
        path.windingRule = .evenOdd
        path.fill()
        NSColor.white.setStroke()
        let border = NSBezierPath(rect: sel.insetBy(dx: 0.5, dy: 0.5)); border.lineWidth = 1; border.stroke()
        if let dimText { drawBadge(dimText, for: sel) }
    }

    private func drawBadge(_ text: String, for sel: NSRect) {
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .semibold),
            .foregroundColor: NSColor.white,
        ]
        let str = NSAttributedString(string: text, attributes: attrs)
        let ts = str.size(); let padX: CGFloat = 6, padY: CGFloat = 3
        let bw = ts.width + padX * 2, bh = ts.height + padY * 2
        var x = sel.midX - bw / 2; x = min(max(x, bounds.minX + 2), bounds.maxX - bw - 2)
        var y = sel.minY - 6 - bh; if y < bounds.minY + 2 { y = sel.minY + 6 }
        let badge = NSRect(x: x, y: y, width: bw, height: bh)
        NSColor(white: 0, alpha: 0.8).setFill()
        NSBezierPath(roundedRect: badge, xRadius: 4, yRadius: 4).fill()
        str.draw(at: NSPoint(x: badge.minX + padX, y: badge.minY + padY))
    }
}

// MARK: - Loupe (zoom 8× + « #hex · x, y »)

private final class LoupeView: NSView {
    static let radius: CGFloat = 64
    static let zoom: CGFloat = 8
    static let diameter: CGFloat = radius * 2
    static let labelGap: CGFloat = 8
    static let labelH: CGFloat = 22
    static let viewW: CGFloat = 168
    static var size: NSSize { NSSize(width: viewW, height: diameter + labelGap + labelH) }

    private var zoomImage: NSImage?
    private var hexString = "#000000"
    private var coordString = "0, 0"

    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    override var isFlipped: Bool { false }

    func refresh(snapshot: CGImage, bitmapRep: NSBitmapImageRep, scaleX: CGFloat, scaleY: CGFloat,
                 viewHeight: CGFloat, cursor: NSPoint, globalTopLeft: NSPoint) {
        let px = cursor.x * scaleX
        let py = (viewHeight - cursor.y) * scaleY
        let halfX = (Self.radius / Self.zoom) * scaleX
        let halfY = (Self.radius / Self.zoom) * scaleY
        var src = CGRect(x: px - halfX, y: py - halfY, width: halfX * 2, height: halfY * 2)
        src = src.intersection(CGRect(x: 0, y: 0, width: snapshot.width, height: snapshot.height))
        if let sub = snapshot.cropping(to: src) {
            zoomImage = NSImage(cgImage: sub, size: NSSize(width: sub.width, height: sub.height))
        }
        let cx = min(max(Int(px), 0), snapshot.width - 1)
        let cy = min(max(Int(py), 0), snapshot.height - 1)
        if let c = bitmapRep.colorAt(x: cx, y: cy)?.usingColorSpace(.sRGB) {
            hexString = String(format: "#%02X%02X%02X",
                               Int((c.redComponent * 255).rounded()),
                               Int((c.greenComponent * 255).rounded()),
                               Int((c.blueComponent * 255).rounded()))
        }
        coordString = "\(Int(globalTopLeft.x.rounded())), \(Int(globalTopLeft.y.rounded()))"
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        let circle = NSRect(x: (bounds.width - Self.diameter) / 2, y: bounds.height - Self.diameter,
                            width: Self.diameter, height: Self.diameter)
        NSGraphicsContext.saveGraphicsState()
        NSBezierPath(ovalIn: circle).addClip()
        NSColor.black.setFill(); circle.fill()
        if let img = zoomImage {
            NSGraphicsContext.current?.imageInterpolation = .none
            img.draw(in: circle, from: .zero, operation: .copy, fraction: 1)
        }
        let c = NSPoint(x: circle.midX, y: circle.midY)
        let cross = NSBezierPath(); cross.lineWidth = 1
        cross.move(to: NSPoint(x: c.x - 8, y: c.y)); cross.line(to: NSPoint(x: c.x + 8, y: c.y))
        cross.move(to: NSPoint(x: c.x, y: c.y - 8)); cross.line(to: NSPoint(x: c.x, y: c.y + 8))
        NSColor.white.withAlphaComponent(0.9).setStroke(); cross.stroke()
        NSGraphicsContext.restoreGraphicsState()

        NSColor.white.setStroke()
        let ring = NSBezierPath(ovalIn: circle); ring.lineWidth = 1.5; ring.stroke()

        let font = NSFont.monospacedSystemFont(ofSize: 11, weight: .medium)
        let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: NSColor.white]
        let str = NSAttributedString(string: "\(hexString) · \(coordString)", attributes: attrs)
        let ts = str.size(); let padX: CGFloat = 7, padY: CGFloat = 3
        let pillW = ts.width + padX * 2, pillH = ts.height + padY * 2
        let pill = NSRect(x: (bounds.width - pillW) / 2, y: circle.minY - Self.labelGap - pillH,
                          width: pillW, height: pillH)
        NSColor(white: 0, alpha: 0.78).setFill()
        NSBezierPath(roundedRect: pill, xRadius: 5, yRadius: 5).fill()
        str.draw(at: NSPoint(x: pill.minX + padX, y: pill.minY + padY))
    }
}
