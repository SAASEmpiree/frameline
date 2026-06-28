import SwiftUI

/// Panneau de contenu : capture sélectionnée OU état vide.
/// (La barre du haut est désormais une NSToolbar native, gérée par MainWindowController.)
struct DetailView: View {
    @EnvironmentObject var library: CaptureLibrary
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        ZStack {
            // Fond noir charbon (#131213) qui remonte SOUS la barre du haut transparente (mode fenêtré)
            // → le haut à droite est noir comme le détail, pas un bandeau gris.
            (scheme == .dark ? Color(white: 0.075) : Color(white: 0.98))
                .ignoresSafeArea()
            Group {
                if let capture = library.selectedCapture {
                    CaptureDetailView(capture: capture)
                } else {
                    EmptyStateView()
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

// MARK: - État vide

private struct EmptyStateView: View {
    @EnvironmentObject var coord: AppCoordinator
    var body: some View {
        VStack(spacing: 0) {
            // Tuile centrale : carré ~96pt, fond charbon à peine plus clair, icône camera.viewfinder.
            ZStack {
                RoundedRectangle(cornerRadius: 22, style: .continuous)
                    .fill(Color.white.opacity(0.06))                       // #1E1F1F sur fond #131213
                    .overlay(RoundedRectangle(cornerRadius: 22, style: .continuous)
                        .strokeBorder(Color.white.opacity(0.05), lineWidth: 1))
                    .frame(width: 96, height: 96)
                    .shadow(color: .black.opacity(0.35), radius: 14, y: 8)
                Image(systemName: "camera.viewfinder")
                    .font(.system(size: 42, weight: .regular)).foregroundStyle(.primary.opacity(0.9))
            }
            Text("Aucune capture")
                .font(.system(size: 26, weight: .bold)).padding(.top, 36)
            Text("Les captures apparaissent ici automatiquement.\nCapturez une première zone pour démarrer.")
                .font(.system(size: 14)).foregroundStyle(.secondary)
                .multilineTextAlignment(.center).padding(.top, 12)

            HStack(spacing: 16) {
                CapturePill(icon: "viewfinder", title: "Capturer une zone", keys: CaptureHotkey.keys(for: CaptureHotkey.region),
                            highlighted: true) { coord.captureRegion() }
                CapturePill(icon: "macwindow", title: "Fenêtre", keys: CaptureHotkey.keys(for: CaptureHotkey.window)) { coord.captureWindow() }
                CapturePill(icon: "display", title: "Plein écran", keys: CaptureHotkey.keys(for: CaptureHotkey.fullScreen)) { coord.captureFull() }
            }
            .padding(.top, 34)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

private struct CapturePill: View {
    let icon: String
    let title: String
    let keys: [String]
    var highlighted: Bool = false
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: icon).font(.system(size: 14))
                Text(title).font(.system(size: 14, weight: .semibold))
                HStack(spacing: 4) {
                    ForEach(keys, id: \.self) { k in
                        Text(k)
                            .font(.system(size: 12, weight: .medium))
                            .frame(minWidth: 16)
                            .padding(.vertical, 3).padding(.horizontal, 5)
                            .background(RoundedRectangle(cornerRadius: 5)
                                .fill(highlighted ? Color.black.opacity(0.06) : Color.white.opacity(0.10)))
                            .overlay(RoundedRectangle(cornerRadius: 5)              // fin liseré clair des keycaps (comme BridgeShot)
                                .strokeBorder(highlighted ? Color.black.opacity(0.14) : Color.white.opacity(0.28), lineWidth: 1))
                    }
                }
                .padding(.leading, 2)
            }
        }
        .buttonStyle(CapturePillStyle(highlighted: highlighted))
    }
}

/// Style des pastilles d'action : fond capsule + hover (éclaircissement + léger zoom) et press
/// (enfoncement) — micro-animations courtes en ease-out, dans l'esprit d'une vraie app macOS.
private struct CapturePillStyle: ButtonStyle {
    let highlighted: Bool
    func makeBody(configuration: Configuration) -> some View {
        PillBody(highlighted: highlighted, configuration: configuration)
    }

    private struct PillBody: View {
        let highlighted: Bool
        let configuration: ButtonStyleConfiguration
        @State private var hovering = false

        var body: some View {
            configuration.label
                .foregroundStyle(highlighted ? Color.black : Color.white.opacity(0.95))
                .padding(.horizontal, 14).padding(.vertical, 8)
                .background(Capsule().fill(background))
                .overlay(Capsule()                                    // fin liseré de la pastille (comme BridgeShot)
                    .strokeBorder(highlighted ? Color.clear : Color.white.opacity(0.14), lineWidth: 1))
                .scaleEffect(configuration.isPressed ? 0.97 : (hovering ? 1.015 : 1.0))
                .opacity(configuration.isPressed ? 0.9 : 1.0)
                .onHover { hovering = $0 }
                .animation(.easeOut(duration: 0.12), value: hovering)
                .animation(.easeOut(duration: 0.10), value: configuration.isPressed)
        }

        private var background: Color {
            if highlighted { return hovering ? Color.white.opacity(0.92) : .white }
            return Color.white.opacity(hovering ? 0.13 : 0.07)
        }
    }
}

// MARK: - Détail d'une capture

private struct CaptureDetailView: View {
    @EnvironmentObject var coord: AppCoordinator
    @EnvironmentObject var library: CaptureLibrary
    let capture: Capture
    @State private var fullImage: NSImage?

    var body: some View {
        VStack(spacing: 0) {
            ZStack {
                Color.black.opacity(0.22)
                if capture.isVideo {
                    VideoPreview(capture: capture)
                } else if let img = fullImage {
                    Image(nsImage: img).resizable().aspectRatio(contentMode: .fit).padding(24)
                } else {
                    ProgressView()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .task(id: "\(capture.url.path)|\(capture.date.timeIntervalSinceReferenceDate)") {
                fullImage = nil
                let url = capture.url
                let data: Data? = await withCheckedContinuation { cont in
                    DispatchQueue.global(qos: .userInitiated).async {
                        cont.resume(returning: try? Data(contentsOf: url))
                    }
                }
                if let data, !Task.isCancelled { fullImage = NSImage(data: data) }
            }

            Divider()
            HStack(spacing: 10) {
                Text(capture.name).font(.system(size: 12)).foregroundStyle(.secondary)
                    .lineLimit(1).truncationMode(.middle)
                Spacer()
                if !capture.isVideo {
                    ActionButton("Copier", "doc.on.doc") { library.copyToPasteboard(capture) }
                    ActionButton("Annoter", "pencil.tip.crop.circle") { coord.annotate(capture.url) }
                }
                ActionButton("Finder", "folder") { library.reveal(capture) }
                ActionButton("Supprimer", "trash", destructive: true) { library.remove(capture) }
            }
            .padding(.horizontal, 16).padding(.vertical, 10)
        }
    }
}

private struct VideoPreview: View {
    @EnvironmentObject var library: CaptureLibrary
    let capture: Capture
    var body: some View {
        ZStack {
            if let thumb = library.thumbnails[capture.url] {
                Image(nsImage: thumb).resizable().aspectRatio(contentMode: .fit).padding(24)
            }
            Button { NSWorkspace.shared.open(capture.url) } label: {
                Image(systemName: "play.circle.fill").font(.system(size: 60))
                    .foregroundStyle(.white.opacity(0.9))
            }
            .buttonStyle(.plain)
            .help("Lire la vidéo")
        }
    }
}

private struct ActionButton: View {
    let title: String
    let icon: String
    var destructive: Bool = false
    let action: () -> Void
    init(_ title: String, _ icon: String, destructive: Bool = false, action: @escaping () -> Void) {
        self.title = title; self.icon = icon; self.destructive = destructive; self.action = action
    }
    var body: some View {
        Button(action: action) {
            HStack(spacing: 5) { Image(systemName: icon); Text(title) }
                .font(.system(size: 12))
                .padding(.horizontal, 10).padding(.vertical, 6)
                .background(RoundedRectangle(cornerRadius: 7).fill(Color.white.opacity(0.08)))
        }
        .buttonStyle(.plain)
        .foregroundStyle(destructive ? Color.red : Color.primary)
    }
}
