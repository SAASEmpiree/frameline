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
                    // Des captures existent mais aucune n'est sélectionnée (Cmd-clic sur la dernière carte
                    // sélectionnée) : ne pas afficher « Aucune capture » à côté d'une liste pleine.
                    EmptyStateView(hasCaptures: !library.captures.isEmpty)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

// MARK: - État vide

private struct EmptyStateView: View {
    @EnvironmentObject var coord: AppCoordinator
    var hasCaptures = false
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
            Text(hasCaptures ? "Aucune capture sélectionnée" : "Aucune capture")
                .font(.system(size: 26, weight: .bold)).padding(.top, 36)
            Text(hasCaptures ? "Sélectionnez une capture dans la barre latérale,\nou capturez une nouvelle zone."
                             : "Les captures apparaissent ici automatiquement.\nCapturez une première zone pour démarrer.")
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
    let capture: Capture

    var body: some View {
        Group {
            if capture.isVideo {
                // ÉDITEUR DE ROGNAGE EMBARQUÉ (lecteur + pellicule) — jumeau temporel de l'éditeur
                // d'annotation : mêmes capsules flottantes, même charbon. Il peint son propre fond.
                VideoTrimmerHost(url: capture.url)
                    .id(capture.url)        // recrée le contrôleur (et son lecteur) quand la sélection change
            } else {
                // ÉDITEUR d'annotation EMBARQUÉ (barre d'outils en haut + canevas zoomable + barre de
                // zoom en bas), façon BridgeShot (cf. images #10/#11/#12) — plus une simple fenêtre à part.
                MarkupEditorHost(url: capture.url)
                    .id(capture.url)        // recharge l'éditeur quand la capture sélectionnée change
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// Héberge l'éditeur AppKit (`MarkupEditorViewController`) dans le détail SwiftUI.
private struct MarkupEditorHost: NSViewControllerRepresentable {
    let url: URL
    func makeNSViewController(context: Context) -> MarkupEditorViewController { MarkupEditorViewController(url: url) }
    func updateNSViewController(_ nsViewController: MarkupEditorViewController, context: Context) {}
    /// `.id(capture.url)` démonte l'éditeur dès que la sélection change : on enregistre ce qui a été
    /// annoté. Différé d'un tour, sinon `reloadFile` modifie la bibliothèque pendant la mise à jour SwiftUI.
    static func dismantleNSViewController(_ nsViewController: MarkupEditorViewController, coordinator: ()) {
        DispatchQueue.main.async { nsViewController.saveIfEdited() }
    }
}

/// Héberge l'éditeur de rognage AppKit (`VideoTrimmerViewController`) dans le détail SwiftUI.
/// Même forme que `MarkupEditorHost` : `.id(capture.url)` force un contrôleur neuf par vidéo
/// (l'ancien est démonté → son `deinit` met en pause + libère le fichier).
private struct VideoTrimmerHost: NSViewControllerRepresentable {
    let url: URL
    func makeNSViewController(context: Context) -> VideoTrimmerViewController { VideoTrimmerViewController(url: url) }
    func updateNSViewController(_ nsViewController: VideoTrimmerViewController, context: Context) {}
}

