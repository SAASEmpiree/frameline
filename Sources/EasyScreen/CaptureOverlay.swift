import AppKit
import ScreenCaptureKit
import CoreGraphics

/// Overlay de capture « région » fidèle à BridgeShot : on fige d'abord un snapshot
/// ScreenCaptureKit de CHAQUE écran, puis on présente un panneau borderless non-activant au niveau
/// bouclier PAR écran dont le fond est ce snapshot.
///
/// MULTI-ÉCRAN QUI TRAVERSE LES ÉCRANS : avec « les écrans ont des Spaces distincts » (défaut macOS),
/// une seule fenêtre ne peut pas couvrir deux écrans → on garde un panneau par écran, mais le glisser
/// est suivi en COORDONNÉES GLOBALES par le contrôleur (grâce au grab implicite, le panneau où démarre
/// le clic reçoit tout le glisser même hors de son écran). Chaque écran dessine la portion de la
/// sélection qui le concerne ; au relâchement on COMPOSITE la zone depuis les snapshots de tous les
/// écrans traversés (multi-DPI géré).
///
/// Déroulé en DEUX temps : 1) après le raccourci, juste le réticule ; 2) au glisser, voile + sélection
/// (bord blanc + dimensions) + loupe (zoom pixels, hex, coords).
@MainActor
final class CaptureOverlayController {
    static let shared = CaptureOverlayController()
    private init() {}

    /// Un panneau par écran : (écran, snapshot natif, vue racine).
    private var panels: [(screen: NSScreen, snapshot: CGImage, view: OverlayRootView)] = []
    private var windows: [NSWindow] = []
    private var completion: ((CGImage?) -> Void)?
    /// Vrai tant que l'overlay de capture région est à l'écran (garde anti-reopen pendant la capture).
    var isPresenting: Bool { !windows.isEmpty }
    /// Bascule de mode (Espace) : l'orchestrateur y branche le démarrage de la capture FENÊTRE.
    var onSwitchMode: (() -> Void)?
    /// App active AVANT l'overlay : on lui rend le focus après la capture (ne jamais le voler).
    private var previousApp: NSRunningApplication?
    /// Réaffirme le réticule EN CONTINU : au-dessus d'une app PLEIN ÉCRAN, le système reprend la
    /// flèche (cursor rects / mouseMoved peu fiables pour un panneau non-activant).
    private var cursorTimer: Timer?

    // État du glisser GLOBAL (coordonnées Cocoa globales, origine bas-gauche).
    private var dragStartGlobal: NSPoint?
    private var globalSelection: NSRect?

    /// Union des cadres écran : borne la sélection (évite les zones « entre écrans »).
    private var unionFrame: NSRect {
        panels.dropFirst().reduce(panels.first?.screen.frame ?? .zero) { $0.union($1.screen.frame) }
    }

    /// Point d'entrée unique (AppCoordinator.captureRegion()). `completion` reçoit le CGImage rogné
    /// (origine haut-gauche, pixels natifs) ou nil si annulé.
    func beginRegion(completion: @escaping (CGImage?) -> Void) {
        guard windows.isEmpty, self.completion == nil else { completion(nil); return }
        self.completion = completion
        previousApp = NSWorkspace.shared.frontmostApplication   // app où était l'utilisateur
        Task { await self.present() }
    }

    private func present() async {
        let content: SCShareableContent
        do {
            content = try await ScreenCapturer.shareableContent()
        } catch {
            CaptureManager.shared.ensureScreenRecordingPermission()
            finish(nil)
            return
        }

        let myPID = getpid()
        let ownWindows = content.windows.filter { $0.owningApplication?.processID == myPID }

        // Un snapshot par écran relié à un NSScreen (points ↔ pixels).
        var built: [(screen: NSScreen, image: CGImage)] = []
        for display in content.displays {
            guard let screen = NSScreen.screens.first(where: { $0.es_displayID == display.displayID }) else { continue }
            guard let image = try? await ScreenCapturer.captureDisplay(display, excluding: ownWindows) else { continue }
            built.append((screen, image))
        }
        guard !built.isEmpty else {
            CaptureManager.shared.ensureScreenRecordingPermission()
            finish(nil)
            return
        }

        for (screen, image) in built {
            let view = OverlayRootView(snapshot: image, screen: screen, controller: self)
            let win = OverlayWindow(contentRect: screen.frame,
                                    styleMask: [.borderless, .nonactivatingPanel],
                                    backing: .buffered, defer: false)
            win.level = NSWindow.Level(rawValue: Int(CGShieldingWindowLevel()))
            win.backgroundColor = .clear
            win.isOpaque = false
            win.hasShadow = false
            win.isReleasedWhenClosed = false
            win.hidesOnDeactivate = false
            win.acceptsMouseMovedEvents = true
            win.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
            win.contentView = view
            win.setFrame(screen.frame, display: true)
            windows.append(win)
            panels.append((screen, image, view))
        }

        // Panneaux non-activants affichés sur le Space courant (plein écran inclus) via orderFrontRegardless.
        let mouse = NSEvent.mouseLocation
        let keyWindow = windows.first { $0.frame.contains(mouse) } ?? windows.first
        for win in windows { win.orderFrontRegardless() }
        keyWindow?.makeKey()
        if let content = keyWindow?.contentView { keyWindow?.makeFirstResponder(content) }
        // On ACTIVE l'app : c'est la SEULE façon que macOS applique notre réticule DÈS le survol
        // (NSCursor n'est honoré que pour l'app active). Le focus est rendu à l'app précédente dans
        // finish() ; les panneaux ne peuvent PAS devenir main (canBecomeMain=false) → aucune fenêtre
        // ne « revient ». Et comme aucune fenêtre galerie n'existe sur un autre Space, l'activation ne
        // fait pas basculer hors du plein écran (les overlays sont déjà sur le Space courant).
        NSApp.activate(ignoringOtherApps: true)
        NSCursor.crosshair.set()
        ESLog.snapshot("CaptureOverlay SHOWN (\(windows.count) overlay panels)")
        // Maintien du réticule tant que l'overlay est ouvert (indispensable au-dessus du plein écran).
        cursorTimer?.invalidate()
        // Mode .common (≠ .default) : le timer continue de tirer PENDANT la boucle de tracking du
        // glisser (qui bloque le runloop en .eventTracking). Sinon le réticule cesse d'être réaffirmé
        // pendant la sélection et le système peut reprendre la flèche au-dessus d'une app plein écran.
        let t = Timer(timeInterval: 0.05, repeats: true) { _ in NSCursor.crosshair.set() }
        RunLoop.main.add(t, forMode: .common)
        cursorTimer = t
        // PHASE 1 (avant toute sélection) : UNIQUEMENT le réticule. Pas de loupe, pas de code hex, pas
        // de coordonnées tant qu'on n'a pas commencé à sélectionner une zone (demande explicite). La
        // loupe n'apparaît qu'au glisser (refreshDrag).
    }

    // MARK: - Glisser global (appelé par la vue racine sous le grab implicite)

    func beginDrag(global: NSPoint) {
        dragStartGlobal = global
        refreshDrag(current: global)
    }
    func updateDrag(global: NSPoint) {
        refreshDrag(current: global)
    }

    private func refreshDrag(current: NSPoint) {
        guard let start = dragStartGlobal else { return }
        var sel = NSRect(x: min(start.x, current.x), y: min(start.y, current.y),
                         width: abs(current.x - start.x), height: abs(current.y - start.y))
        sel = sel.intersection(unionFrame)
        globalSelection = sel
        let hasSel = sel.width >= 1 && sel.height >= 1
        let dimText = "\(Int(sel.width.rounded())) × \(Int(sel.height.rounded()))"

        // Écran sous le curseur : il porte la loupe + le badge de dimensions.
        let cursorPanel = panels.first { NSMouseInRect(current, $0.screen.frame, false) }

        for p in panels {
            let local = sel.offsetBy(dx: -p.screen.frame.minX, dy: -p.screen.frame.minY)
            let isCursor = (cursorPanel?.view === p.view)
            p.view.applySelection(local: hasSel ? local : nil, dimensionText: isCursor ? dimText : nil)
            if isCursor {
                let localCursor = NSPoint(x: current.x - p.screen.frame.minX, y: current.y - p.screen.frame.minY)
                p.view.showMagnifier(atLocal: localCursor)
            } else {
                p.view.hideMagnifier()
            }
        }
    }

    func endDrag() {
        let sel = globalSelection
        dragStartGlobal = nil; globalSelection = nil
        // On efface AUSSITÔT le voile + la sélection : l'écran redevient « live » plein éclat avant
        // la capture (et nos overlays sont de toute façon exclus de l'image, par PID).
        for p in panels { p.view.applySelection(local: nil, dimensionText: nil); p.view.hideMagnifier() }
        ESLog.log("endDrag sel=\(NSStringFromRect(sel ?? .zero))")
        guard let sel, sel.width >= 4, sel.height >= 4 else {
            ESLog.log("endDrag → sélection trop petite, annulation")
            finish(nil)                                  // simple clic / trop petit = annulation
            return
        }
        // Capture EN DIRECT au relâchement : le contenu a continué de tourner, on recapture la zone
        // depuis l'écran RÉEL (nos fenêtres overlay exclues → ni voile ni sélection dans l'image).
        Task {
            let img = await self.captureLiveGlobal(sel)
            ESLog.log("captureLiveGlobal → \(img != nil ? "OK \(img!.width)x\(img!.height)" : "NIL")")
            self.finish(img)
        }
    }

    /// Rect pixels (origine haut-gauche) d'une portion globale `g` dans le snapshot de l'écran.
    private func srcPixels(_ g: NSRect, screen: NSScreen, snapshot: CGImage) -> CGRect {
        let sx = CGFloat(snapshot.width) / screen.frame.width
        let sy = CGFloat(snapshot.height) / screen.frame.height
        let local = g.offsetBy(dx: -screen.frame.minX, dy: -screen.frame.minY)
        return CGRect(x: (local.minX * sx).rounded(), y: ((screen.frame.height - local.maxY) * sy).rounded(),
                      width: (local.width * sx).rounded(), height: (local.height * sy).rounded())
            .intersection(CGRect(x: 0, y: 0, width: snapshot.width, height: snapshot.height))
    }

    /// Recapture EN DIRECT la sélection depuis l'écran réel (contenu live), nos overlays exclus (par
    /// PID) → ni voile ni rectangle de sélection dans l'image. Un snapshot SCK par écran traversé.
    private func captureLiveGlobal(_ sel: NSRect) async -> CGImage? {
        guard let content = try? await ScreenCapturer.shareableContent() else {
            ESLog.log("captureLiveGlobal: shareableContent ÉCHEC"); return nil
        }
        let myPID = getpid()
        let own = content.windows.filter { $0.owningApplication?.processID == myPID }
        ESLog.log("captureLiveGlobal: \(content.displays.count) displays, \(own.count) fenêtres à exclure")
        var pieces: [(screen: NSScreen, snapshot: CGImage)] = []
        for d in content.displays {
            guard let screen = NSScreen.screens.first(where: { $0.es_displayID == d.displayID }) else {
                ESLog.log("  display \(d.displayID): pas de NSScreen"); continue
            }
            guard screen.frame.intersects(sel) else { continue }
            do {
                let img = try await ScreenCapturer.captureDisplay(d, excluding: own)
                pieces.append((screen, img))
            } catch {
                ESLog.log("  captureDisplay \(d.displayID) ÉCHEC: \(error.localizedDescription)")
            }
        }
        let result = compositeGlobal(sel, from: pieces)
        ESLog.log("captureLiveGlobal: \(pieces.count) pièces → composite \(result != nil ? "OK" : "NIL")")
        return result
    }

    /// Composite la sélection GLOBALE depuis des snapshots (un par écran traversé). Sortie en pixels
    /// (origine haut-gauche) à l'échelle Retina la plus haute des écrans concernés.
    private func compositeGlobal(_ sel: NSRect, from pieces: [(screen: NSScreen, snapshot: CGImage)]) -> CGImage? {
        let hit = pieces.filter { $0.screen.frame.intersects(sel) }
        guard !hit.isEmpty else { return nil }

        // Cas courant : sélection sur UN seul écran → crop direct O(1) (le compositing ne sert qu'à traverser).
        if hit.count == 1 {
            let src = srcPixels(sel, screen: hit[0].screen, snapshot: hit[0].snapshot)
            guard src.width >= 1, src.height >= 1 else { return nil }
            return hit[0].snapshot.cropping(to: src)
        }

        let outScale = hit.map { $0.screen.backingScaleFactor }.max() ?? 2
        let W = Int((sel.width * outScale).rounded()), H = Int((sel.height * outScale).rounded())
        guard W >= 1, H >= 1,
              let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: W, pixelsHigh: H,
                    bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                    colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0),
              let ctx = NSGraphicsContext(bitmapImageRep: rep) else { return nil }
        rep.size = NSSize(width: W, height: H)           // 1 unité = 1 PIXEL
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = ctx
        ctx.imageInterpolation = .high
        for p in hit {
            let piece = p.screen.frame.intersection(sel)
            guard piece.width > 0, piece.height > 0 else { continue }
            let src = srcPixels(piece, screen: p.screen, snapshot: p.snapshot)
            guard src.width >= 1, src.height >= 1, let pieceCG = p.snapshot.cropping(to: src) else { continue }
            let dest = NSRect(x: (piece.minX - sel.minX) * outScale, y: (piece.minY - sel.minY) * outScale,
                              width: piece.width * outScale, height: piece.height * outScale)
            NSImage(cgImage: pieceCG, size: dest.size).draw(in: dest, from: .zero, operation: .copy, fraction: 1)
        }
        NSGraphicsContext.restoreGraphicsState()
        return rep.cgImage
    }

    /// Termine l'overlay : démonte tout et notifie le résultat UNE seule fois.
    func finish(_ image: CGImage?) {
        guard let done = completion else { return }
        ESLog.snapshot("CaptureOverlay.finish (image=\(image != nil ? "capturée" : "nil/annulé"))")
        completion = nil
        for win in windows { win.orderOut(nil) }
        windows.removeAll()
        panels.removeAll()
        dragStartGlobal = nil; globalSelection = nil
        cursorTimer?.invalidate(); cursorTimer = nil
        NSCursor.arrow.set()
        // Rendre le focus à l'app où l'utilisateur était AVANT la capture (ne jamais le voler).
        // Si l'éditeur s'ouvre ensuite (openEditor), il réactivera Easy Screen lui-même.
        // On ne RESTAURE le focus que si la capture l'a réellement volé (NSApp.isActive). Sinon
        // l'utilisateur n'a jamais quitté son app : un prev.activate() superflu rejouerait une
        // transition de Space (le « retour d'écran »), surtout au-dessus d'une app plein écran.
        if NSApp.isActive, let prev = previousApp,
           prev.processIdentifier != getpid(), !prev.isTerminated {
            ESLog.log("finish → réactive l'app précédente: \(prev.localizedName ?? "?")")
            prev.activate()
        }
        previousApp = nil
        done(image)
    }

    /// Espace : referme cet overlay (sans capture) puis démarre la capture FENÊTRE via l'orchestrateur.
    func switchModeRequested() {
        let cb = onSwitchMode
        finish(nil)
        cb?()
    }
}

// MARK: - Fenêtre overlay

/// Panneau borderless NON-ACTIVANT : s'affiche sur le Space courant (y compris au-dessus d'une app
/// en plein écran) sans activer Easy Screen ni basculer de Space. Doit pouvoir devenir key — sinon
/// il ne recevrait jamais Échap.
private final class OverlayWindow: NSPanel {
    override var canBecomeKey: Bool { true }
    // canBecomeMain=false : rendre MAIN un panneau non-activant ACTIVE l'app (vol de focus +
    // transition de Space au-dessus d'une app plein écran). canBecomeKey=true suffit pour recevoir
    // Échap via OverlayRootView.keyDown — le panneau n'a JAMAIS besoin de devenir main.
    override var canBecomeMain: Bool { false }
}

// MARK: - Vue racine (événements + composition)

/// Une vue par écran. Reçoit la souris/clavier et compose : fond (snapshot), voile troué par la
/// sélection, loupe. Le glisser est délégué au contrôleur en coordonnées GLOBALES (multi-écran).
private final class OverlayRootView: NSView {

    let screen: NSScreen
    private let snapshot: CGImage
    private let bitmapRep: NSBitmapImageRep
    private weak var controller: CaptureOverlayController?

    private let veil = VeilView(frame: .zero)
    private let magnifier = MagnifierView(frame: .zero)
    private let hintBar = GlassHintBar(segments: [.init(key: "Espace", label: "Mode fenêtre"),
                                                  .init(key: "Échap", label: "Annuler")])
    private var trackingArea: NSTrackingArea?

    init(snapshot: CGImage, screen: NSScreen, controller: CaptureOverlayController) {
        self.snapshot = snapshot
        self.screen = screen
        self.bitmapRep = NSBitmapImageRep(cgImage: snapshot)
        self.controller = controller
        super.init(frame: .zero)

        wantsLayer = true

        // PAS de fond figé : l'overlay est TRANSPARENT → le contenu réel CONTINUE de tourner dessous
        // (vidéo, animations…), comme BridgeShot / le screenshot natif macOS. Le snapshot d'ouverture
        // ne sert qu'à la loupe ; l'image FINALE est recapturée EN DIRECT au relâchement.
        veil.autoresizingMask = [.width, .height]
        veil.frame = bounds
        addSubview(veil)

        hintBar.frame = NSRect(origin: .zero, size: hintBar.barSize)
        addSubview(hintBar)

        magnifier.wantsLayer = true
        magnifier.frame = NSRect(origin: .zero, size: MagnifierView.size)
        magnifier.isHidden = true
        addSubview(magnifier)
    }
    @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        let s = hintBar.barSize
        hintBar.frame = NSRect(x: ((bounds.width - s.width) / 2).rounded(), y: 48,
                               width: s.width, height: s.height)
    }

    // Échelles pixels ↔ points pour la loupe.
    private var scaleX: CGFloat { bounds.width > 0 ? CGFloat(snapshot.width) / bounds.width : 1 }
    private var scaleY: CGFloat { bounds.height > 0 ? CGFloat(snapshot.height) / bounds.height : 1 }

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func resetCursorRects() { addCursorRect(bounds, cursor: .crosshair) }

    // Overlay TRANSPARENT (live) : on capture EXPLICITEMENT toute la souris. Sans contenu opaque,
    // les clics « passeraient à travers » la fenêtre → impossible de sélectionner / capturer.
    override func hitTest(_ point: NSPoint) -> NSView? {
        bounds.contains(convert(point, from: superview)) ? self : nil
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let trackingArea { removeTrackingArea(trackingArea) }
        let ta = NSTrackingArea(rect: bounds,
                                options: [.activeAlways, .mouseMoved, .mouseEnteredAndExited, .inVisibleRect],
                                owner: self, userInfo: nil)
        addTrackingArea(ta)
        trackingArea = ta
    }

    /// Point global (Cocoa, origine bas-gauche) du curseur pour cet événement.
    private func globalPoint(of event: NSEvent) -> NSPoint {
        window?.convertPoint(toScreen: event.locationInWindow) ?? event.locationInWindow
    }

    // MARK: Souris — délégué au contrôleur en GLOBAL

    // Phase 1 (avant le glisser) : juste le réticule, RÉAFFIRMÉ à chaque mouvement — un panneau
    // non-activant n'honore pas toujours les cursor rects, sinon le curseur redevient une flèche
    // tant qu'on n'a pas cliqué. Pas de loupe ni de voile en phase 1.
    // Phase 1 = réticule SEUL. On ne montre NI loupe NI hex NI coordonnées tant qu'aucune zone n'est
    // en cours de sélection (la loupe n'apparaît qu'au glisser, via le contrôleur → refreshDrag).
    override func mouseEntered(with event: NSEvent) { NSCursor.crosshair.set() }
    override func mouseExited(with event: NSEvent) {}
    override func mouseMoved(with event: NSEvent) { NSCursor.crosshair.set() }

    // Le panneau est TRANSPARENT + NON-ACTIVANT au niveau bouclier et l'app n'est pas forcément encore
    // active au moment du clic : le grab implicite d'AppKit (redelivery de mouseDragged/mouseUp via la
    // chaîne de responders) est alors PEU FIABLE → on observait UN SEUL mouseDragged puis un mouseUp
    // synthétique (sélection ~0px, glisser annulé). On pilote donc le glisser par une BOUCLE DE
    // TRACKING MANUELLE qui lit les events DIRECTEMENT dans la file en mode .eventTracking : insensible
    // à l'activation/clé en cours, au hitTest custom et au redraw voile/loupe (servis par le runloop
    // pendant nextEvent). beginDrag/updateDrag/endDrag du contrôleur restent alimentés en COORDONNÉES
    // GLOBALES (multi-écran) via window.convertPoint(toScreen:).
    override func mouseDown(with event: NSEvent) {
        // preventWindowOrdering() : ce clic sert à GLISSER, pas à réordonner/activer la fenêtre — sinon
        // le serveur de fenêtres annule le glisser et émet un mouseUp synthétique.
        NSApp.preventWindowOrdering()
        ESLog.log("OverlayRootView.mouseDown")
        controller?.beginDrag(global: NSEvent.mouseLocation)
        guard let window else { return }
        // SUIVI DU GLISSER PAR POLLING (et non par la livraison des events) : pour un panneau
        // NON-ACTIVANT au-dessus d'une AUTRE app, les .leftMouseDragged/.leftMouseUp n'arrivaient pas de
        // façon fiable dans la file → la sélection restait 0×0 et « ça ne prenait pas le screen ». On lit
        // donc directement la position GLOBALE du curseur (NSEvent.mouseLocation) et l'état du bouton
        // (NSEvent.pressedMouseButtons), insensibles à l'activation. La file n'est consultée (non
        // bloquant, ~8 ms — ce qui throttle aussi le polling) que pour intercepter Échap pendant le glisser.
        trackingLoop: while true {
            controller?.updateDrag(global: NSEvent.mouseLocation)
            // Relâchement détecté sur l'ÉTAT MATÉRIEL du bouton (pas sur un event .leftMouseUp, que le
            // serveur de fenêtres émet parfois de façon SYNTHÉTIQUE et prématurée pour un panneau
            // non-activant → c'était la cause du « glisser annulé / ça ne prend pas le screen »).
            if (NSEvent.pressedMouseButtons & 0x1) == 0 {
                controller?.updateDrag(global: NSEvent.mouseLocation)
                ESLog.log("trackingLoop release global=\(Int(NSEvent.mouseLocation.x)),\(Int(NSEvent.mouseLocation.y))")
                controller?.endDrag()
                break trackingLoop
            }
            // Attente courte (~8 ms, throttle du polling). On DRAINE les events souris pour ne pas les
            // accumuler, mais on ne s'en sert PAS (position + bouton viennent du polling) ; seul Échap agit.
            if let ev = window.nextEvent(matching: [.keyDown, .leftMouseUp, .leftMouseDragged],
                                         until: Date(timeIntervalSinceNow: 0.008),
                                         inMode: .eventTracking, dequeue: true),
               ev.type == .keyDown, ev.keyCode == 53 {            // Échap = annuler immédiatement
                controller?.finish(nil)
                break trackingLoop
            }
        }
    }

    // Complète preventWindowOrdering() pour le « glisser depuis une fenêtre d'arrière-plan / non-
    // activante en UN seul clic » : on DÉLAYE l'ordonnancement de fenêtre pour que le clic démarre le
    // glisser au lieu d'« activer » la fenêtre (ce qui annulerait le suivi).
    override func shouldDelayWindowOrdering(for event: NSEvent) -> Bool { true }

    // MARK: Clavier

    override func keyDown(with event: NSEvent) {
        switch event.keyCode {
        case 53: controller?.finish(nil)                 // Échap = annuler
        case 49: controller?.switchModeRequested()       // Espace = bascule capture fenêtre
        default: super.keyDown(with: event)
        }
    }
    override func cancelOperation(_ sender: Any?) { controller?.finish(nil) }

    // MARK: Pilotage par le contrôleur

    /// Applique la portion LOCALE de la sélection globale (nil = rien) + le texte de dimensions (badge).
    func applySelection(local: NSRect?, dimensionText: String?) {
        veil.dimText = dimensionText
        veil.selection = local
    }
    func showMagnifier(atLocal p: NSPoint) {
        magnifier.isHidden = false
        magnifier.refresh(snapshot: snapshot, bitmapRep: bitmapRep,
                          scaleX: scaleX, scaleY: scaleY, viewHeight: bounds.height,
                          cursor: p, globalTopLeft: globalTopLeft(local: p))
        placeMagnifier(near: p)
    }
    func hideMagnifier() { magnifier.isHidden = true }

    /// Convertit un point LOCAL (origine bas-gauche) en coordonnées GLOBALES bureau (origine
    /// haut-gauche) — pivot sur l'écran primaire, comme les coordonnées affichées par BridgeShot.
    private func globalTopLeft(local p: NSPoint) -> NSPoint {
        let gx = screen.frame.minX + p.x
        let gyBottom = screen.frame.minY + p.y
        let primaryH = NSScreen.screens.first { $0.frame.origin == .zero }?.frame.height
            ?? NSScreen.main?.frame.height ?? screen.frame.height
        return NSPoint(x: gx, y: primaryH - gyBottom)
    }

    /// Place la loupe en diagonale bas-droite du curseur (offset constant), ancrée sur le cercle.
    private func placeMagnifier(near p: NSPoint) {
        let s = MagnifierView.size
        let gap: CGFloat = 16
        let inset = (s.width - MagnifierView.diameter) / 2
        var x = p.x + gap - inset
        var y = p.y - gap - s.height
        if p.x + gap + MagnifierView.diameter > bounds.maxX - 4 { x = p.x - gap - MagnifierView.diameter - inset }
        if y < bounds.minY + 4 { y = p.y + gap }
        x = min(max(x, bounds.minX + 4), bounds.maxX - s.width - 4)
        y = min(max(y, bounds.minY + 4), bounds.maxY - s.height - 4)
        magnifier.frame = NSRect(x: x.rounded(), y: y.rounded(), width: s.width, height: s.height)
    }
}

// MARK: - Sous-vues « transparentes » à la souris

private final class PassthroughImageView: NSImageView {
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}

// MARK: - Voile + sélection + badge dimensions

/// Voile noir ~0.55 troué par la portion locale de la sélection ; bord blanc 1px ; badge « L × H »
/// (dimensions GLOBALES, fournies par le contrôleur) dessiné uniquement sur l'écran du curseur.
private final class VeilView: NSView {
    var selection: NSRect? { didSet { needsDisplay = true } }
    var dimText: String?

    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    override var isFlipped: Bool { false }

    override func draw(_ dirtyRect: NSRect) {
        guard let sel = selection, sel.width > 0, sel.height > 0 else { return }

        NSColor(white: 0, alpha: 0.22).setFill()       // voile SUBTIL hors sélection (comme BridgeShot)
        let path = NSBezierPath(rect: bounds)
        path.appendRect(sel)
        path.windingRule = .evenOdd
        path.fill()

        NSColor.white.setStroke()
        let border = NSBezierPath(rect: sel.insetBy(dx: 0.5, dy: 0.5))
        border.lineWidth = 1
        border.stroke()

        if let dimText { drawDimensionBadge(dimText, for: sel) }
    }

    private func drawDimensionBadge(_ text: String, for sel: NSRect) {
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .semibold),
            .foregroundColor: NSColor.white,
        ]
        let str = NSAttributedString(string: text, attributes: attrs)
        let textSize = str.size()
        let padX: CGFloat = 6, padY: CGFloat = 3
        let badgeW = textSize.width + padX * 2
        let badgeH = textSize.height + padY * 2

        var x = sel.midX - badgeW / 2
        x = min(max(x, bounds.minX + 2), bounds.maxX - badgeW - 2)
        var y = sel.minY - 6 - badgeH
        if y < bounds.minY + 2 { y = sel.minY + 6 }

        let badge = NSRect(x: x, y: y, width: badgeW, height: badgeH)
        NSColor(white: 0, alpha: 0.78).setFill()
        NSBezierPath(roundedRect: badge, xRadius: 4, yRadius: 4).fill()
        str.draw(at: NSPoint(x: badge.minX + padX, y: badge.minY + padY))
    }
}

// MARK: - Loupe

/// Loupe circulaire (zoom 8×) qui suit le curseur : réticule '+' au centre, pilule « #RRGGBB · x, y ».
private final class MagnifierView: NSView {

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

    func refresh(snapshot: CGImage, bitmapRep: NSBitmapImageRep,
                 scaleX: CGFloat, scaleY: CGFloat, viewHeight: CGFloat,
                 cursor: NSPoint, globalTopLeft: NSPoint) {
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
            let r = Int((c.redComponent * 255).rounded())
            let g = Int((c.greenComponent * 255).rounded())
            let b = Int((c.blueComponent * 255).rounded())
            hexString = String(format: "#%02X%02X%02X", r, g, b)
        }
        // Coordonnées GLOBALES bureau (origine haut-gauche, comme BridgeShot : « 3124, 1932 »).
        coordString = "\(Int(globalTopLeft.x.rounded())), \(Int(globalTopLeft.y.rounded()))"
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        let circle = NSRect(x: (bounds.width - Self.diameter) / 2,
                            y: bounds.height - Self.diameter,
                            width: Self.diameter, height: Self.diameter)

        NSGraphicsContext.saveGraphicsState()
        NSBezierPath(ovalIn: circle).addClip()
        NSColor.black.setFill(); circle.fill()
        if let img = zoomImage {
            NSGraphicsContext.current?.imageInterpolation = .none
            img.draw(in: circle, from: .zero, operation: .copy, fraction: 1)
        }
        let c = NSPoint(x: circle.midX, y: circle.midY)
        let cross = NSBezierPath()
        cross.lineWidth = 1
        cross.move(to: NSPoint(x: c.x - 8, y: c.y)); cross.line(to: NSPoint(x: c.x + 8, y: c.y))
        cross.move(to: NSPoint(x: c.x, y: c.y - 8)); cross.line(to: NSPoint(x: c.x, y: c.y + 8))
        NSColor.white.withAlphaComponent(0.9).setStroke()
        cross.stroke()
        NSGraphicsContext.restoreGraphicsState()

        NSColor.white.setStroke()
        let ring = NSBezierPath(ovalIn: circle); ring.lineWidth = 1.5; ring.stroke()

        drawLabels(below: circle.minY)
    }

    private func drawLabels(below circleMinY: CGFloat) {
        let font = NSFont.monospacedSystemFont(ofSize: 11, weight: .medium)
        let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: NSColor.white]
        let str = NSAttributedString(string: "\(hexString) · \(coordString)", attributes: attrs)
        let ts = str.size()
        let padX: CGFloat = 7, padY: CGFloat = 3
        let pillW = ts.width + padX * 2
        let pillH = ts.height + padY * 2
        let pill = NSRect(x: (bounds.width - pillW) / 2,
                          y: circleMinY - Self.labelGap - pillH,
                          width: pillW, height: pillH)
        NSColor(white: 0, alpha: 0.78).setFill()
        NSBezierPath(roundedRect: pill, xRadius: 5, yRadius: 5).fill()
        str.draw(at: NSPoint(x: pill.minX + padX, y: pill.minY + padY))
    }
}
