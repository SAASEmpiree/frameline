import SwiftUI
import AppKit
import ImageIO

/// Sidebar gauche : titre + compteur, galerie de captures, barre d'outils en bas.
/// Le drop-import est appliqué de l'extérieur via `SidebarView().dropImport()`
/// (cf. MainWindowController) → ce type reste une simple `View`, identique à l'usage actuel.
struct SidebarView: View {
    @EnvironmentObject var coord: AppCoordinator
    @EnvironmentObject var library: CaptureLibrary

    var body: some View {
        VStack(spacing: 0) {
            // En-tête (décalé pour laisser la place aux feux tricolores de la fenêtre)
            HStack(spacing: 8) {
                Text("Captures").font(.system(size: 15, weight: .bold))
                Text("\(library.captures.count)")
                    .font(.system(size: 11, weight: .semibold))
                    .padding(.horizontal, 7).padding(.vertical, 2)
                    .background(Capsule().fill(Color.white.opacity(0.12)))
                Spacer()
            }
            .padding(.horizontal, 16)
            .padding(.top, 18)
            .padding(.bottom, 10)

            if library.captures.isEmpty {
                sidebarEmpty
            } else {
                ScrollView {
                    LazyVStack(spacing: 3) {
                        ForEach(library.captures) { capture in
                            CaptureRow(capture: capture, selected: library.selection == capture.url)
                                // Sélection au clic gauche (le menu contextuel/les boutons au survol
                                // pilotent les actions via AppCoordinator).
                                .onTapGesture { library.selection = capture.url }
                        }
                    }
                    .padding(.horizontal, 10)
                    .padding(.bottom, 10)
                }
            }

            Spacer(minLength: 0)
            Divider()

            HStack(spacing: 16) {
                ToolButton(system: "photo.on.rectangle", help: "Importer une image") { importPanel() }
                ToolButton(system: "folder", help: "Ouvrir le dossier") { NSWorkspace.shared.open(library.directory) }
                ToolButton(system: "arrow.clockwise", help: "Rafraîchir") { library.refresh() }
                Spacer()
                ToolButton(system: "gearshape", help: "Réglages") { coord.openPreferences() }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 10)
        }
        // Aucun fond opaque : la sidebar laisse voir le matériau vibrant natif (translucide, flush),
        // exactement comme la référence — pas de carte en retrait.
    }

    private var sidebarEmpty: some View {
        VStack(spacing: 8) {
            Spacer()
            Image(systemName: "photo.on.rectangle.angled")
                .font(.system(size: 32)).foregroundStyle(.secondary)
            Text("Aucune capture").font(.system(size: 13, weight: .semibold))
            Text("\(CaptureHotkey.label(for: CaptureHotkey.region)) pour capturer — ou glissez des images ici pour les importer.")
                .font(.system(size: 11)).foregroundStyle(.secondary)
                .multilineTextAlignment(.center).padding(.horizontal, 22)
            Spacer()
        }
        .frame(maxWidth: .infinity)
    }

    private func importPanel() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = true
        panel.allowedContentTypes = [.image, .movie]
        if panel.runModal() == .OK { _ = library.importFiles(panel.urls) }
    }
}

// MARK: - Ligne de la galerie

private struct CaptureRow: View {
    @EnvironmentObject var library: CaptureLibrary
    let capture: Capture
    let selected: Bool
    @State private var hovering = false
    // Résolution en pixels lue depuis le fichier (sans décoder l'image entière), affichée au sous-titre.
    @State private var pixelSize: CGSize?

    var body: some View {
        HStack(spacing: 10) {
            thumbnail

            VStack(alignment: .leading, spacing: 2) {
                Text(capture.name).font(.system(size: 12)).lineLimit(1).truncationMode(.middle)
                // « il y a X min · L×H » — temps relatif + résolution (quand disponible).
                Text(subtitle).font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 0)
        }
        .padding(6)
        .background(RoundedRectangle(cornerRadius: 8)
            .fill(selected ? Color.accentColor.opacity(0.28)
                           : (hovering ? Color.white.opacity(0.06) : Color.clear)))
        // Boutons inline au survol (façon référence photo #5), flottant à droite sans pousser la mise en page.
        .overlay(alignment: .trailing) { hoverActions }
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.12), value: hovering)
        // Clic droit : menu contextuel complet (toutes les actions passent par AppCoordinator).
        .contextMenu { contextMenu }
        // Lecture asynchrone de la résolution → ne bloque pas le défilement.
        .task(id: capture.url) { await loadPixelSize() }
    }

    // MARK: Vignette

    private var thumbnail: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 6).fill(Color.black.opacity(0.35))
            if let thumb = library.thumbnails[capture.url] {
                Image(nsImage: thumb).resizable().aspectRatio(contentMode: .fill)
                    .frame(width: 58, height: 42).clipped()
                    .clipShape(RoundedRectangle(cornerRadius: 6))
            } else {
                Image(systemName: capture.isVideo ? "film" : "photo").foregroundStyle(.secondary)
            }
            if capture.isVideo {
                Image(systemName: "play.circle.fill").font(.system(size: 16))
                    .foregroundStyle(.white).opacity(0.9)
            }
        }
        .frame(width: 58, height: 42)
    }

    private var subtitle: String {
        var s = es_relativeTime(capture.date)
        if let px = pixelSize { s += " · \(Int(px.width))×\(Int(px.height))" }
        return s
    }

    // MARK: Boutons au survol (Restaurer / Copier / Épingler)

    @ViewBuilder private var hoverActions: some View {
        // Réservé aux images : restaurer/épingler/copier n'ont pas de sens pour une vidéo.
        if !capture.isVideo {
            HStack(spacing: 2) {
                InlineActionButton(system: "arrow.up.left.and.arrow.down.right", help: "Restaurer à l'écran") {
                    AppCoordinator.shared.restoreToScreen(capture.url)
                }
                InlineActionButton(system: "doc.on.doc", help: "Copier") {
                    AppCoordinator.shared.copyImage(capture.url)
                }
                InlineActionButton(system: "pin", help: "Épingler à l'écran") {
                    AppCoordinator.shared.pinToScreen(capture.url)
                }
            }
            .padding(.horizontal, 5).padding(.vertical, 3)
            .background(Capsule().fill(Color.black.opacity(0.55)))
            .overlay(Capsule().strokeBorder(Color.white.opacity(0.08), lineWidth: 1))
            .padding(.trailing, 8)
            .opacity(hovering ? 1 : 0)
            .allowsHitTesting(hovering)          // invisibles = non cliquables
            .animation(.easeOut(duration: 0.12), value: hovering)
        }
    }

    // MARK: Menu contextuel

    @ViewBuilder private var contextMenu: some View {
        if !capture.isVideo {
            Button { AppCoordinator.shared.annotate(capture.url) } label: {
                Label("Annoter", systemImage: "pencil.tip.crop.circle")
            }
            Button { AppCoordinator.shared.copyImage(capture.url) } label: {
                Label("Copier l'image", systemImage: "doc.on.doc")
            }
            Button { AppCoordinator.shared.copyText(capture.url) } label: {
                Label("Copier le texte", systemImage: "text.viewfinder")
            }
            Divider()
            Button { AppCoordinator.shared.restoreToScreen(capture.url) } label: {
                Label("Restaurer à l'écran", systemImage: "arrow.up.left.and.arrow.down.right")
            }
            Button { AppCoordinator.shared.pinToScreen(capture.url) } label: {
                Label("Épingler à l'écran", systemImage: "pin")
            }
            Divider()
        }
        Button { AppCoordinator.shared.revealInFinder(capture.url) } label: {
            Label("Afficher dans le Finder", systemImage: "folder")
        }
        Button { AppCoordinator.shared.exportCapture(capture.url) } label: {
            Label("Exporter…", systemImage: "square.and.arrow.up")
        }
        Divider()
        Button(role: .destructive) { AppCoordinator.shared.trash(capture.url) } label: {
            Label("Mettre à la corbeille", systemImage: "trash")
        }
    }

    // MARK: Résolution (asynchrone)

    private func loadPixelSize() async {
        guard !capture.isVideo else { pixelSize = nil; return }
        let url = capture.url
        // Lecture hors du main thread (cohérent avec la génération de vignettes / DetailView).
        let size: CGSize? = await withCheckedContinuation { cont in
            DispatchQueue.global(qos: .utility).async {
                cont.resume(returning: es_imagePixelSize(url))
            }
        }
        if !Task.isCancelled { pixelSize = size }
    }
}

// MARK: - Petit bouton inline (survol)

private struct InlineActionButton: View {
    let system: String
    let help: String
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Image(systemName: system).font(.system(size: 11, weight: .semibold))
                .frame(width: 22, height: 22)
                .background(RoundedRectangle(cornerRadius: 6)
                    .fill(hovering ? Color.white.opacity(0.18) : Color.clear))
        }
        .buttonStyle(.plain)
        .foregroundStyle(hovering ? Color.white : Color.white.opacity(0.82))
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.10), value: hovering)
        .help(help)
    }
}

// MARK: - Barre d'outils du bas

private struct ToolButton: View {
    let system: String
    let help: String
    let action: () -> Void
    @State private var hovering = false
    var body: some View {
        Button(action: action) {
            Image(systemName: system).font(.system(size: 14)).frame(width: 24, height: 24)
                .background(RoundedRectangle(cornerRadius: 6)
                    .fill(hovering ? Color.white.opacity(0.10) : Color.clear))
        }
        .buttonStyle(.plain)
        .foregroundStyle(hovering ? Color.primary : Color.secondary)
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.12), value: hovering)
        .help(help)
    }
}

// MARK: - Helpers fichier-privés

/// Temps relatif court et localisé, ex. « il y a 5 min » (l'app est en français).
private func es_relativeTime(_ date: Date) -> String {
    let f = RelativeDateTimeFormatter()
    f.unitsStyle = .short
    f.locale = Locale(identifier: "fr_FR")
    return f.localizedString(for: date, relativeTo: Date())
}

/// Dimensions en pixels via ImageIO (sans décoder l'image entière → rapide et thread-safe).
private func es_imagePixelSize(_ url: URL) -> CGSize? {
    guard let src = CGImageSourceCreateWithURL(url as CFURL, nil),
          let props = CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any],
          let w = props[kCGImagePropertyPixelWidth] as? Int,
          let h = props[kCGImagePropertyPixelHeight] as? Int else { return nil }
    return CGSize(width: w, height: h)
}
