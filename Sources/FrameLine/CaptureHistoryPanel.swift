import AppKit
import SwiftUI
import ImageIO

/// « Capture History » façon BridgeShot : un panneau flottant ancré au bord DROIT de l'écran,
/// empilant les captures récentes sous forme de cartes (vignette + nom + « il y a X · L×H »).
/// Non activant (ne vole pas le focus), niveau flottant, se ferme au clic extérieur ou sur Échap.
@MainActor
final class CaptureHistoryController: NSObject, NSWindowDelegate {
    static let shared = CaptureHistoryController()

    private var panel: NSPanel?
    private var globalMonitor: Any?
    private var localMonitor: Any?
    /// Incrémenté à chaque show/hide : le `orderOut` différé d'un hide n'agit que s'il est toujours le
    /// dernier geste (sinon un show dans les 0,13 s du fondu se faisait masquer par l'orderOut en attente).
    private var visibilityGeneration = 0

    private var isVisible: Bool { panel?.isVisible ?? false }

    /// Bascule l'affichage (item de menu « Capture History »).
    func toggle() { isVisible ? hide() : show() }

    func show() {
        if panel == nil { build() }
        visibilityGeneration &+= 1                         // annule un orderOut de fondu encore en attente
        position()
        panel?.alphaValue = 0
        panel?.orderFrontRegardless()
        // Statut clé SANS activer l'app (panneau non activant) : sinon les frappes, dont Échap, partent
        // à l'app de premier plan et le moniteur local ne les voit jamais.
        panel?.makeKey()
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.16
            panel?.animator().alphaValue = 1
        }
        installMonitors()
    }

    func hide() {
        removeMonitors()
        guard let panel else { return }
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.12
            panel.animator().alphaValue = 0
        }
        // orderOut après le fondu, sur le main thread (évite la capture Sendable du panneau).
        visibilityGeneration &+= 1
        let generation = visibilityGeneration
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.13) { [weak self] in
            guard let self, self.visibilityGeneration == generation else { return }   // ré-affiché entre-temps
            self.panel?.orderOut(nil)
        }
    }

    // MARK: - Construction

    private func build() {
        let root = CaptureHistoryView(
            onOpen: { [weak self] url in
                AppCoordinator.shared.openInApp(url)
                self?.hide()
            },
            onClose: { [weak self] in self?.hide() })
            .environmentObject(CaptureLibrary.shared)

        let hosting = NSHostingView(rootView: root)
        let p = KeyableHistoryPanel(contentRect: NSRect(x: 0, y: 0, width: 312, height: 520),
                        styleMask: [.borderless, .nonactivatingPanel],
                        backing: .buffered, defer: false)
        p.isFloatingPanel = true
        p.level = .floating
        p.hidesOnDeactivate = false
        p.isMovableByWindowBackground = false
        p.backgroundColor = .clear
        p.isOpaque = false
        p.hasShadow = true
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        p.contentView = hosting
        p.delegate = self
        panel = p
    }

    /// Ancrage bord droit, sous la barre des menus.
    private func position() {
        guard let panel, let screen = NSScreen.main else { return }
        let vf = screen.visibleFrame
        let size = panel.frame.size
        let margin: CGFloat = 14
        let x = vf.maxX - size.width - margin
        let y = vf.maxY - size.height - margin
        panel.setFrameOrigin(NSPoint(x: x, y: y))
    }

    // MARK: - Fermeture au clic extérieur / Échap

    private func installMonitors() {
        removeMonitors()
        globalMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] _ in
            self?.hide()
        }
        localMonitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown, .keyDown]) { [weak self] event in
            guard let self, let panel = self.panel else { return event }
            if event.type == .keyDown {
                if event.keyCode == 53 { self.hide(); return nil }   // Échap
                return event
            }
            // Clic hors du panneau → fermer (laisse passer les clics internes).
            if event.window != panel { self.hide() }
            return event
        }
    }

    private func removeMonitors() {
        if let g = globalMonitor { NSEvent.removeMonitor(g); globalMonitor = nil }
        if let l = localMonitor { NSEvent.removeMonitor(l); localMonitor = nil }
    }
}

/// Un NSPanel `.borderless` refuse par défaut le statut clé (`canBecomeKey == false`) → Échap n'arrivait
/// jamais au panneau. Combiné à `.nonactivatingPanel`, devenir clé n'active pas l'app (pas de vol de focus
/// de l'app de premier plan ni de sa barre des menus).
private final class KeyableHistoryPanel: NSPanel {
    override var canBecomeKey: Bool { true }
}

// MARK: - Vue SwiftUI

private struct CaptureHistoryView: View {
    @EnvironmentObject var library: CaptureLibrary
    let onOpen: (URL) -> Void
    let onClose: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 8) {
                Image(systemName: "clock.arrow.circlepath").font(.system(size: 13, weight: .semibold))
                Text("Capture History").font(.system(size: 14, weight: .bold))
                Spacer()
                Text("\(library.captures.count)")
                    .font(.system(size: 11, weight: .semibold))
                    .padding(.horizontal, 7).padding(.vertical, 2)
                    .background(Capsule().fill(Color.white.opacity(0.12)))
            }
            .padding(.horizontal, 14).padding(.top, 13).padding(.bottom, 10)

            Divider().opacity(0.4)

            if library.captures.isEmpty {
                VStack(spacing: 8) {
                    Spacer()
                    Image(systemName: "clock.badge.questionmark")
                        .font(.system(size: 30)).foregroundStyle(.secondary)
                    Text("No captures yet").font(.system(size: 13, weight: .semibold))
                    Text("Your recent captures will show up here.")
                        .font(.system(size: 11)).foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                    Spacer()
                }
                .frame(maxWidth: .infinity)
            } else {
                ScrollView {
                    LazyVStack(spacing: 7) {
                        ForEach(library.captures.prefix(12)) { capture in
                            HistoryCard(capture: capture) { onOpen(capture.url) }
                        }
                    }
                    .padding(.horizontal, 10).padding(.vertical, 10)
                }
            }
        }
        .frame(width: 312, height: 520, alignment: .top)
        .background(
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .fill(.ultraThinMaterial)
                .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .strokeBorder(Color.white.opacity(0.12), lineWidth: 1))
        )
        .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
        .preferredColorScheme(.dark)
    }
}

// MARK: - Carte d'historique

private struct HistoryCard: View {
    @EnvironmentObject var library: CaptureLibrary
    let capture: Capture
    let action: () -> Void
    @State private var hovering = false
    @State private var pixelSize: CGSize?

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                thumbnail
                VStack(alignment: .leading, spacing: 3) {
                    Text(capture.name)
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(.primary)
                        .lineLimit(1).truncationMode(.middle)
                    Text(subtitle)
                        .font(.system(size: 10.5))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                Spacer(minLength: 0)
            }
            .padding(7)
            .background(RoundedRectangle(cornerRadius: 11, style: .continuous)
                .fill(hovering ? Color.white.opacity(0.10) : Color.white.opacity(0.04)))
            .overlay(RoundedRectangle(cornerRadius: 11, style: .continuous)
                .strokeBorder(Color.white.opacity(hovering ? 0.18 : 0.07), lineWidth: 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.12), value: hovering)
        // Clé = URL + date : une capture réécrite sur place (annotations, fond, recadrage) relit ses L×H.
        .task(id: "\(capture.url.path)|\(capture.date.timeIntervalSinceReferenceDate)") { await loadPixelSize() }
    }

    private var thumbnail: some View {
        RoundedRectangle(cornerRadius: 8, style: .continuous)
            .fill(Color.black.opacity(0.35))
            .frame(width: 78, height: 52)
            .overlay {
                ZStack {
                    if let thumb = library.thumbnails[capture.url] {
                        Image(nsImage: thumb).resizable().aspectRatio(contentMode: .fit)   // capture ENTIÈRE, pas rognée
                    } else if !capture.isVideo {
                        Image(systemName: "photo").font(.system(size: 18)).foregroundStyle(.secondary)
                    }
                    if capture.isVideo {
                        VideoPlayBadge(size: 28)
                    }
                }
            }
            .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous)
                .strokeBorder(Color.white.opacity(0.10), lineWidth: 1))
    }

    private var subtitle: String {
        var s = es_relativeTimeEN(capture.date)
        if let px = pixelSize { s += " · \(Int(px.width))×\(Int(px.height))" }
        return s
    }

    private func loadPixelSize() async {
        guard !capture.isVideo else { pixelSize = nil; return }
        let url = capture.url
        let size: CGSize? = await withCheckedContinuation { cont in
            DispatchQueue.global(qos: .utility).async { cont.resume(returning: es_imagePixelSizeHist(url)) }
        }
        if !Task.isCancelled { pixelSize = size }
    }
}

// MARK: - Helpers fichier-privés

/// Temps relatif court EN ANGLAIS (« 5 sec ago », « 3 min ago ») — le panneau Capture History
/// reprend les libellés anglais de BridgeShot.
private func es_relativeTimeEN(_ date: Date) -> String {
    let f = RelativeDateTimeFormatter()
    f.unitsStyle = .abbreviated
    f.locale = Locale(identifier: "en_US")
    return f.localizedString(for: date, relativeTo: Date())
}

private func es_imagePixelSizeHist(_ url: URL) -> CGSize? {
    guard let src = CGImageSourceCreateWithURL(url as CFURL, nil),
          let props = CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any],
          let w = props[kCGImagePropertyPixelWidth] as? Int,
          let h = props[kCGImagePropertyPixelHeight] as? Int else { return nil }
    return CGSize(width: w, height: h)
}
