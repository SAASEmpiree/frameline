import SwiftUI
import AppKit
import ImageIO

/// Sidebar gauche : titre + compteur, galerie de captures, barre d'outils en bas.
/// Le drop-import est appliqué de l'extérieur via `SidebarView().dropImport()`
/// (cf. MainWindowController) → ce type reste une simple `View`, identique à l'usage actuel.
struct SidebarView: View {
    @EnvironmentObject var coord: AppCoordinator
    @EnvironmentObject var library: CaptureLibrary
    @ObservedObject private var hotkeys = HotkeyStore.shared   // le texte vide cite le raccourci courant
    @Environment(\.colorScheme) private var scheme

    /// Gris OPAQUE de la sidebar (façon BridgeShot). Appliqué via une NSView opaque (cf.
    /// `SolidSidebarBacking`) qui RECOUVRE entièrement le matériau vibrant que macOS place derrière un
    /// item « sidebar » → une seule couche pleine, aucune translucidité (« pas un fond + la colonne
    /// par-dessus »).
    private var sidebarNSColor: NSColor {
        // Gris de la sidebar (façon BridgeShot) : nettement PLUS CLAIR que le détail noir, pour que la barre
        // du haut lise « un côté gris (sidebar) / un côté noir (détail) ». ~0.17 = le gris mesuré sur BridgeShot.
        NSColor(white: scheme == .dark ? 0.17 : 0.95, alpha: 1)
    }

    var body: some View {
        VStack(spacing: 0) {
            // En-tête (décalé pour laisser la place aux feux tricolores de la fenêtre)
            HStack(spacing: 8) {
                Text("Captures").font(.system(size: 15, weight: .bold))
                    .lineLimit(1).fixedSize()               // jamais coupé sur 2 lignes (« Capture / s »)
                Text("\(library.captures.count)")
                    .font(.system(size: 11, weight: .semibold))
                    .lineLimit(1).fixedSize()
                    .padding(.horizontal, 7).padding(.vertical, 2)
                    .background(Capsule().fill(Color.white.opacity(0.12)))
                Spacer(minLength: 6)
                // Dès qu'au moins UNE capture est sélectionnée → bouton « Supprimer » bien visible.
                if !library.selectedURLs.isEmpty {
                    SelectionDeleteButton(count: library.selectedURLs.count) { coord.trashSelection() }
                        .transition(.opacity.combined(with: .move(edge: .trailing)))
                }
            }
            .padding(.horizontal, 16)
            .padding(.top, 18)
            .padding(.bottom, 10)
            .animation(.easeOut(duration: 0.14), value: library.selectedURLs.count)

            if library.captures.isEmpty {
                sidebarEmpty
            } else {
                ScrollView {
                    LazyVStack(spacing: 8) {                  // cartes espacées (façon BridgeShot)
                        ForEach(library.captures) { capture in
                            CaptureRow(capture: capture, selected: library.selectedURLs.contains(capture.url))
                                // Sélection au clic gauche, façon Finder : clic simple = 1 seule ;
                                // Cmd-clic = (dé)sélection multiple ; Maj-clic = plage. On lit les
                                // modificateurs clavier au moment du clic via NSEvent.modifierFlags.
                                .onTapGesture {
                                    let m = NSEvent.modifierFlags
                                    library.handleClick(capture.url,
                                                        command: m.contains(.command),
                                                        shift: m.contains(.shift))
                                }
                        }
                    }
                    .padding(.leading, 12)
                    .padding(.trailing, 22)                    // marge droite = couloir de la scroll bar → écart ~10pt avec les vignettes
                    .padding(.top, 4)
                    .padding(.bottom, 12)
                    .background(SidebarScrollIndicator())      // pouce gris FLOTTANT (overlay), PAS de rail, ZÉRO gouttière
                }
            }

            Spacer(minLength: 0)
            Divider()

            HStack(spacing: 16) {
                ToolButton(system: "photo.on.rectangle", help: "Importer une image") { coord.importImages() }
                ToolButton(system: "folder", help: "Ouvrir le dossier") { coord.openCapturesFolder() }
                ToolButton(system: "arrow.clockwise", help: "Rafraîchir") { library.refresh() }
                // Supprimer vite la/les capture(s) sélectionnée(s) → corbeille (réversible).
                if !library.captures.isEmpty {
                    ToolButton(system: "trash", help: trashHelp, destructive: true,
                               disabled: library.actionableSelection.isEmpty) { coord.trashSelection() }
                }
                Spacer()
                // ⚠️ Différé au tour de run-loop SUIVANT (cf. deferPresentation) : présenter une fenêtre
                // (Réglages) SYNCHRONEMENT depuis l'action d'un Button SwiftUI — en plein traitement du
                // mouseUp — est AVALÉ sur macOS récents (le clic « ne fait rien »). Le même appel depuis le
                // menu de la barre des menus marche car son action tombe APRÈS la fin du suivi du menu
                // (run-loop propre). On reproduit ce contexte propre en différant d'un tour.
                ToolButton(system: "gearshape", help: "Réglages") { deferPresentation { coord.openPreferences() } }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 10)
        }
        // Matériau VIBRANT translucide (.sidebar, behind-window) : la sidebar laisse transparaître le bureau
        // FLOUTÉ (léger dégradé teinté), comme BridgeShot. Sûr maintenant que le Liquid Glass de macOS 26 est
        // désactivé (UIDesignRequiresCompatibility) → plus de « slivers » colorés ni de panneau flottant.
        .background(VibrantSidebarBacking().ignoresSafeArea())
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

    /// Info-bulle du bouton corbeille, adaptée au nombre d'éléments sélectionnés.
    private var trashHelp: String {
        let n = library.selectedURLs.count
        if n == 0 { return "Aucune capture sélectionnée" }
        return n > 1 ? "Supprimer les \(n) captures sélectionnées"
                     : "Supprimer la capture sélectionnée"
    }

}

// MARK: - Ligne de la galerie

private struct CaptureRow: View {
    @EnvironmentObject var library: CaptureLibrary
    @Environment(\.colorScheme) private var scheme
    let capture: Capture
    let selected: Bool
    @State private var hovering = false
    // Résolution en pixels lue depuis le fichier (sans décoder l'image entière), affichée au sous-titre.
    @State private var pixelSize: CGSize?

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            thumbnail
                // Boutons d'action flottants au coin haut-droit de la vignette, au survol.
                .overlay(alignment: .topTrailing) { hoverActions }

            VStack(alignment: .leading, spacing: 2) {
                Text(capture.name)
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(.primary)
                    .lineLimit(1).truncationMode(.middle)
                // « il y a X min · L×H » — temps relatif + résolution (quand disponible).
                Text(subtitle)
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            .padding(.horizontal, 3)
        }
        .padding(6)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous)
            .fill(hovering && !selected ? Color.white.opacity(0.05) : Color.clear))
        // Contour clair = carte SÉLECTIONNÉE (comme BridgeShot, cf. image #9).
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous)
            .strokeBorder(selected ? Color.white.opacity(0.85) : Color.clear, lineWidth: 2))
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.12), value: hovering)
        // Clic droit : menu contextuel complet (toutes les actions passent par AppCoordinator).
        .contextMenu { contextMenu }
        // Lecture asynchrone de la résolution → ne bloque pas le défilement. Clé = URL + date de modif :
        // un rognage enregistré sur place (même URL, reloadFile) change la date → la résolution est relue,
        // sinon le sous-titre gardait l'ancienne taille alors que la vignette, elle, est régénérée.
        // La date est relue dans la bibliothèque : `Capture ==` ne compare que l'URL, donc SwiftUI peut
        // garder l'ancienne valeur de `capture` (même URL) et sa date périmée.
        .task(id: [capture.url.path, String(currentDate.timeIntervalSinceReferenceDate)]) { await loadPixelSize() }
    }

    // MARK: Vignette — image en COUVERTURE bord-à-bord (façon BridgeShot), UNE seule couche

    private let cardAspect: CGFloat = 2.6                        // hauteur de carte fixe → plus de cartes visibles

    /// Gris de fond de la vignette = LE MÊME gris que la sidebar (« le gris qu'on a en fond ») → la carte lit
    /// comme UNE seule surface continue, plus de « boîte sombre » distincte à l'intérieur. (Repli au chargement
    /// / capture non-image ; invisible dès que l'image couvre.)
    private var sidebarGray: Color { Color(nsColor: NSColor(white: scheme == .dark ? 0.17 : 0.95, alpha: 1)) }

    /// La capture REMPLIT la vignette bord-à-bord (couverture) → UNE seule couche, comme BridgeShot.
    /// Fiabilité : `Image(nsImage:).scaledToFill()` ne remplit PAS de façon fiable une NSImage issue d'un
    /// CGImage (elle « letterboxe » → l'effet « deux couches »). On PRÉ-RECADRE donc la vignette au ratio exact
    /// de la carte (`coverCropped`) : l'image ayant déjà le bon ratio, un simple `.resizable()` la fait remplir
    /// le cadre, sans letterbox possible.
    private var thumbnail: some View {
        sidebarGray
            .aspectRatio(cardAspect, contentMode: .fit)
            .overlay {
                if let thumb = library.thumbnails[capture.url] {
                    Image(nsImage: coverCropped(thumb, aspect: cardAspect)).resizable()
                } else if !capture.isVideo {
                    Image(systemName: "photo").font(.system(size: 26)).foregroundStyle(.secondary)
                }
                if capture.isVideo { VideoPlayBadge(size: 46) }
            }
            .clipShape(RoundedRectangle(cornerRadius: 9, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 9, style: .continuous)
                .strokeBorder(Color.white.opacity(0.08), lineWidth: 1))
    }

    /// Recadre `img` en COUVERTURE au ratio `aspect` (largeur/hauteur) via un crop du CGImage (lazy, O(1),
    /// aucun pixel copié). Résultat : une NSImage déjà au ratio de la carte → remplit le cadre sans letterbox.
    private func coverCropped(_ img: NSImage, aspect: CGFloat) -> NSImage {
        guard let cg = img.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return img }
        let W = CGFloat(cg.width), H = CGFloat(cg.height)
        guard W > 0, H > 0 else { return img }
        var rect = CGRect(x: 0, y: 0, width: W, height: H)
        if W / H > aspect {                                     // trop large → on rogne la largeur (garde le centre)
            let nw = (H * aspect).rounded()
            rect = CGRect(x: ((W - nw) / 2).rounded(), y: 0, width: nw, height: H)
        } else {                                                // trop haut → on rogne la hauteur (garde le centre)
            let nh = (W / aspect).rounded()
            rect = CGRect(x: 0, y: ((H - nh) / 2).rounded(), width: W, height: nh)
        }
        guard let sub = cg.cropping(to: rect) else { return img }
        return NSImage(cgImage: sub, size: NSSize(width: rect.width, height: rect.height))
    }

    /// Date de modification À JOUR (cf. `.task(id:)`) : celle de la bibliothèque, à défaut celle de la ligne.
    private var currentDate: Date {
        library.captures.first(where: { $0.url == capture.url })?.date ?? capture.date
    }

    private var subtitle: String {
        var s = es_relativeTime(currentDate)
        if let px = pixelSize { s += " · \(Int(px.width))×\(Int(px.height))" }
        return s
    }

    // MARK: Boutons au survol (Restaurer / Copier / Épingler) — coin haut-droit de la vignette

    @ViewBuilder private var hoverActions: some View {
        // Réservé aux images : restaurer/épingler/copier n'ont pas de sens pour une vidéo.
        if !capture.isVideo {
            HStack(spacing: 2) {
                // restaurer/épingler PRÉSENTENT une fenêtre (PinnedShotWindow) → même précaution que les
                // boutons de la barre d'outils : on diffère d'un tour de run-loop (cf. deferPresentation),
                // sinon la présentation synchrone depuis ce Button SwiftUI risque d'être avalée.
                InlineActionButton(system: "arrow.up.left.and.arrow.down.right", help: "Restaurer à l'écran") {
                    deferPresentation { AppCoordinator.shared.restoreToScreen(capture.url) }
                }
                InlineActionButton(system: "doc.on.doc", help: "Copier") {
                    AppCoordinator.shared.copyImage(capture.url)          // presse-papiers seul, aucune fenêtre → pas de report
                }
                InlineActionButton(system: "pin", help: "Épingler à l'écran") {
                    deferPresentation { AppCoordinator.shared.pinToScreen(capture.url) }
                }
            }
            .padding(.horizontal, 5).padding(.vertical, 3)
            .background(Capsule().fill(Color.black.opacity(0.55)))
            .overlay(Capsule().strokeBorder(Color.white.opacity(0.10), lineWidth: 1))
            .padding(8)
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
        Button(role: .destructive) {
            if isInMultiSelection { AppCoordinator.shared.trashSelection() }
            else { AppCoordinator.shared.trash(capture.url) }
        } label: {
            Label(isInMultiSelection ? "Mettre à la corbeille (\(library.selectedURLs.count))"
                                     : "Mettre à la corbeille", systemImage: "trash")
        }
    }

    /// Vrai quand ce clic droit porte sur une carte faisant partie d'une sélection multiple active
    /// → l'action « corbeille » vise toute la sélection (comme Finder).
    private var isInMultiSelection: Bool {
        library.selectedURLs.count > 1 && library.selectedURLs.contains(capture.url)
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
    var destructive: Bool = false            // survol rouge (bouton corbeille)
    var disabled: Bool = false               // grisé + non cliquable (ex. corbeille sans sélection)
    let action: () -> Void
    @State private var hovering = false
    private var showHover: Bool { hovering && !disabled }
    var body: some View {
        Button(action: action) {
            Image(systemName: system).font(.system(size: 14)).frame(width: 24, height: 24)
                .background(RoundedRectangle(cornerRadius: 6)
                    .fill(showHover ? (destructive ? Color.red.opacity(0.16) : Color.white.opacity(0.10)) : Color.clear))
        }
        .buttonStyle(.plain)
        .foregroundStyle(showHover ? (destructive ? Color.red : Color.primary) : Color.secondary)
        .opacity(disabled ? 0.35 : 1)
        .disabled(disabled)
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.12), value: hovering)
        .help(help)
    }
}

// MARK: - Bouton « Supprimer N » (multi-sélection, en-tête)

/// Pastille affichée dans l'en-tête dès qu'une capture est sélectionnée : supprime toute la sélection
/// d'un clic (façon barre d'action de Finder/Photos). MONOCHROME, cohérente avec la DA sombre de l'app :
/// même gris clair translucide que le badge du compteur (« 113 »), texte + icône blancs, une SEULE ligne,
/// éclaircissement au survol. Pas de rouge (qui jurait avec le reste de l'interface).
private struct SelectionDeleteButton: View {
    let count: Int
    let action: () -> Void
    @State private var hovering = false

    private var label: String { count > 1 ? "Supprimer \(count)" : "Supprimer" }

    var body: some View {
        Button(action: action) {
            HStack(spacing: 5) {
                Image(systemName: "trash").font(.system(size: 10, weight: .semibold))
                Text(label)
                    .font(.system(size: 11.5, weight: .semibold))
                    .lineLimit(1)
                    .fixedSize()                        // garantit une seule ligne, quelle que soit la largeur dispo
            }
            .foregroundStyle(.white)
            .padding(.horizontal, 11).padding(.vertical, 5)
            .background(Capsule(style: .continuous).fill(Color.white.opacity(hovering ? 0.22 : 0.13)))
            .overlay(Capsule(style: .continuous)
                .strokeBorder(Color.white.opacity(hovering ? 0.24 : 0.14), lineWidth: 0.5))
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.12), value: hovering)
        .help(count > 1 ? "Mettre les \(count) captures sélectionnées à la corbeille"
                        : "Mettre la capture sélectionnée à la corbeille")
    }
}

// MARK: - Helpers fichier-privés

/// Diffère une présentation de fenêtre/panneau au tour de run-loop SUIVANT. Indispensable quand l'action
/// part d'un Button SwiftUI : présenter (makeKeyAndOrderFront / NSOpenPanel.runModal) SYNCHRONEMENT pendant
/// le traitement du clic est avalé sur macOS récents → « le clic ne fait rien ». Déférer d'un tour replace
/// la présentation dans un contexte de run-loop propre (comme une action de menu, qui elle fonctionne).
private func deferPresentation(_ body: @escaping () -> Void) {
    DispatchQueue.main.async { body() }
}

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

/// Matériau VIBRANT de la sidebar (façon BridgeShot) : `NSVisualEffectView` en `.sidebar` + `.behindWindow`
/// → le bureau transparaît FLOUTÉ et TEINTÉ derrière la colonne (léger dégradé), au lieu d'un gris plat.
/// Suit l'état actif de la fenêtre. N'est PLUS parasité par le « Liquid Glass » de macOS 26 : l'app l'a
/// désactivé (Info.plist `UIDesignRequiresCompatibility`), donc pas de panneau flottant ni de « slivers ».
private struct VibrantSidebarBacking: NSViewRepresentable {
    func makeNSView(context: Context) -> NSVisualEffectView {
        let v = NSVisualEffectView()
        v.material = .sidebar
        v.blendingMode = .behindWindow
        v.state = .followsWindowActiveState
        v.isEmphasized = true
        return v
    }
    func updateNSView(_ nsView: NSVisualEffectView, context: Context) {}
}

/// Fond OPAQUE plein de la sidebar au niveau AppKit : une NSView opaque qui DESSINE le gris et COUVRE
/// totalement le matériau vibrant (`NSVisualEffectView`) que macOS insère derrière un item « sidebar »,
/// et le désactive en prime (`state = .inactive`). Résultat : une SEULE couche opaque, zéro translucidité,
/// pas de « fond » qui transparaît sous la colonne. Placé en `.background` → il est DEVANT le matériau
/// vibrant mais DERRIÈRE le contenu de la sidebar.
private struct SolidSidebarBacking: NSViewRepresentable {
    let nsColor: NSColor
    func makeNSView(context: Context) -> NSView {
        let v = OpaqueColorView(color: nsColor)
        DispatchQueue.main.async { [weak v] in Self.neutralizeVibrancy(behind: v) }
        return v
    }
    func updateNSView(_ nsView: NSView, context: Context) {
        (nsView as? OpaqueColorView)?.color = nsColor
        DispatchQueue.main.async { [weak nsView] in Self.neutralizeVibrancy(behind: nsView) }
    }
    private static func neutralizeVibrancy(behind view: NSView?) {
        var s = view?.superview
        while let cur = s {
            if let fx = cur as? NSVisualEffectView { fx.state = .inactive }   // n'aspire plus le bureau
            s = cur.superview
        }
    }
}

/// NSView 100 % opaque qui remplit son cadre d'une couleur unie (aucune transparence).
private final class OpaqueColorView: NSView {
    var color: NSColor { didSet { needsDisplay = true } }
    init(color: NSColor) { self.color = color; super.init(frame: .zero) }
    required init?(coder: NSCoder) { fatalError("no coder") }
    override var isOpaque: Bool { true }
    override func draw(_ dirtyRect: NSRect) { color.setFill(); dirtyRect.fill() }
}

// MARK: - Indicateur de défilement FLOTTANT maison (pouce en pilule, zéro gouttière)
//
// POURQUOI PAS un NSScroller custom : c'est un choix EXCLUSIF impossible à concilier avec nos 4 objectifs.
//   • En `.legacy`, `drawKnob`/`drawKnobSlot` SONT honorés (pouce 2×, rail vide) MAIS le scroller RÉSERVE
//     de la largeur de contenu → la colonne se rétrécit/reflow quand il apparaît/disparaît (le bug « tout
//     buggé ») et laisse une gouttière permanente si « Toujours afficher les barres ».
//   • En `.overlay` (zéro largeur réservée, pas de reflow), le pouce est peint par le PRIVÉ NSScrollerImp :
//     nos overrides ne sont JAMAIS appelés (confirmé en live) → dessin mort.
// SOLUTION ROBUSTE (100 % API publique) : on garde le scroller natif en `.overlay` (géométrie flottante,
// zéro largeur) mais on le REND INVISIBLE (alpha 0), et on dessine NOTRE pouce dans une NSView qu'on épingle
// en sous-vue du NSScrollView (PAS de la vue document → il ne défile jamais, survit resize/collapse). On suit
// le défilement via `contentView.boundsDidChange` et on calcule pouce+position depuis document/clip. Aucun
// impact de layout → JAMAIS de reflow ni de gouttière. Longueur/couleur/rail 100 % maîtrisés.

/// Représentable posé en `.background` du contenu scrollé : atteint `enclosingScrollView`, force le style
/// overlay + masque le scroller natif, puis installe/mantient le pouce flottant (`ScrollThumbView`).
private struct SidebarScrollIndicator: NSViewRepresentable {
    final class Coordinator {
        weak var bar: ScrollThumbView?
        weak var clipView: NSClipView?
        var boundsObserver: NSObjectProtocol?
        var didFlashOnce = false
        deinit { if let o = boundsObserver { NotificationCenter.default.removeObserver(o) } }
    }
    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> NSView {
        let v = NSView()
        let coord = context.coordinator
        DispatchQueue.main.async { [weak v] in guard let v else { return }; install(from: v, coord: coord) }
        return v
    }
    func updateNSView(_ nsView: NSView, context: Context) {
        let coord = context.coordinator
        DispatchQueue.main.async { [weak nsView] in guard let nsView else { return }; install(from: nsView, coord: coord) }
    }

    private func install(from view: NSView, coord: Coordinator) {
        guard let scroll = view.enclosingScrollView else { return }

        // Overlay = géométrie flottante, ZÉRO largeur réservée → aucun reflow, aucune gouttière.
        scroll.scrollerStyle = .overlay
        scroll.hasVerticalScroller = true
        scroll.hasHorizontalScroller = false
        scroll.drawsBackground = false                 // garde le fond gris opaque visible dessous
        scroll.verticalScroller?.alphaValue = 0        // on masque le pouce natif, on dessine le nôtre
        scroll.contentView.postsBoundsChangedNotifications = true

        // (Ré)installe le pouce flottant s'il manque (ou si SwiftUI l'a retiré).
        // NB : NSScrollView « tile » manuellement ses sous-vues et n'honore PAS de façon fiable des
        // contraintes Auto Layout pour une sous-vue arbitraire (mesuré : bounds restait (0,0)). On utilise
        // donc un cadre manuel + autoresizing : bande de 14pt collée au bord droit, pleine hauteur.
        let barWidth: CGFloat = 14
        if coord.bar == nil || coord.bar?.superview == nil {
            let bar = ScrollThumbView(scrollView: scroll)
            bar.translatesAutoresizingMaskIntoConstraints = true
            bar.autoresizingMask = [.minXMargin, .height]   // reste à droite (marge gauche flexible) + suit la hauteur
            bar.frame = NSRect(x: scroll.bounds.width - barWidth, y: 0, width: barWidth, height: scroll.bounds.height)
            scroll.addSubview(bar)                     // sous-vue du SCROLLVIEW, pas de la vue document
            coord.bar = bar
        } else {
            coord.bar?.scrollView = scroll
            // Recolle au bord droit (au cas où la largeur du scrollview a changé hors resize).
            coord.bar?.frame = NSRect(x: scroll.bounds.width - barWidth, y: 0, width: barWidth, height: scroll.bounds.height)
        }

        // (Ré)accroche l'observateur de défilement si le clip view a changé (recréation SwiftUI).
        if coord.clipView !== scroll.contentView {
            if let o = coord.boundsObserver { NotificationCenter.default.removeObserver(o) }
            let clip = scroll.contentView
            coord.clipView = clip
            coord.boundsObserver = NotificationCenter.default.addObserver(
                forName: NSView.boundsDidChangeNotification, object: clip, queue: .main
            ) { [weak coord] _ in coord?.bar?.scrollDidChange() }
        }

        // Rafraîchit la géométrie à chaque update SwiftUI (ajout/suppression de captures, resize).
        coord.bar?.refreshGeometry()

        // Un flash discret la 1re fois → l'utilisateur voit que le pouce existe (comme l'overlay natif).
        if !coord.didFlashOnce { coord.didFlashOnce = true; coord.bar?.flashIfScrollable() }
    }
}

/// Pouce (« knob ») flottant maison : une pilule gris translucide, sans rail, ~2× sa longueur naturelle,
/// épinglée au bord droit du scrollview. Vue FLIPPED (origine en haut) → maths de position simples.
/// Gère le suivi du défilement, le drag-to-scroll, et un auto-fade. Ne réserve AUCUNE largeur de layout.
final class ScrollThumbView: NSView {
    weak var scrollView: NSScrollView?
    private var isDragging = false
    private var dragGrabOffset: CGFloat = 0          // décalage du point de saisie dans le pouce (coords flipped)
    private var fadeTimer: Timer?

    override var isFlipped: Bool { true }            // y croît vers le bas, 0 = haut

    init(scrollView: NSScrollView) {
        self.scrollView = scrollView
        super.init(frame: .zero)
        wantsLayer = true
        alphaValue = 0
    }
    required init?(coder: NSCoder) { fatalError("no coder") }
    deinit { fadeTimer?.invalidate() }

    // MARK: Métriques (hauteur document, hauteur visible, offset de défilement)

    private func metrics() -> (doc: CGFloat, vis: CGFloat, off: CGFloat)? {
        guard let s = scrollView, let doc = s.documentView else { return nil }
        return (doc.bounds.height, s.contentView.bounds.height, s.contentView.bounds.origin.y)
    }

    private var canScroll: Bool {
        guard let m = metrics() else { return false }
        return m.doc > m.vis + 0.5
    }

    /// Fraction de défilement 0…1 (0 = haut, 1 = bas), robuste au sens de flip de la vue document.
    private func fraction() -> CGFloat {
        guard let s = scrollView, let doc = s.documentView, let m = metrics() else { return 0 }
        let maxOff = m.doc - m.vis
        guard maxOff > 0 else { return 0 }
        // Clip flipped (cas SwiftUI) : origin.y = 0 en haut, croît en descendant → frac = off/maxOff.
        // Clip non-flipped : origin.y = maxOff en haut, 0 en bas → frac = 1 - off/maxOff.
        let raw = doc.isFlipped ? m.off / maxOff : 1 - m.off / maxOff
        return min(max(raw, 0), 1)
    }

    /// Applique une fraction 0…1 au défilement réel (reconvertie selon le flip).
    private func scroll(toFraction frac: CGFloat) {
        guard let s = scrollView, let doc = s.documentView, let m = metrics() else { return }
        let maxOff = m.doc - m.vis
        guard maxOff > 0 else { return }
        let f = min(max(frac, 0), 1)
        var pt = s.contentView.bounds.origin
        pt.y = doc.isFlipped ? f * maxOff : (1 - f) * maxOff
        s.contentView.scroll(to: pt)
        s.reflectScrolledClipView(s.contentView)     // INDISPENSABLE : sinon le clip ne bouge pas
    }

    // MARK: Géométrie du pouce

    private func trackRect() -> NSRect { bounds.insetBy(dx: 0, dy: 4) }   // 4pt de respiration haut/bas

    private func thumbRect() -> NSRect {
        guard let m = metrics(), m.doc > m.vis + 0.5 else { return .zero }
        let track = trackRect()
        let proportion = m.vis / m.doc
        let natural = track.height * proportion
        let h = min(max(natural * 2, 40), track.height)   // ~2× la longueur naturelle, mini 40, borné à la piste
        let travel = track.height - h
        let y = track.minY + fraction() * travel          // vue flipped → y depuis le haut
        let w: CGFloat = 9   // plus épais → cible facile à viser (max qui reste dans la marge 12pt → jamais sur les cartes)
        return NSRect(x: bounds.width - w - 3, y: y, width: w, height: h)   // pilule, à 3pt du bord
    }

    override func draw(_ dirtyRect: NSRect) {
        let k = thumbRect()
        guard k.height > 1 else { return }
        NSColor.white.withAlphaComponent(0.32).setFill()
        NSBezierPath(roundedRect: k, xRadius: k.width / 2, yRadius: k.width / 2).fill()
    }

    // MARK: Suivi / rafraîchissement

    @objc func scrollDidChange() {
        scrollView?.verticalScroller?.alphaValue = 0     // ré-écrase le pouce natif (évite qu'il refade in)
        needsDisplay = true
        showThumb()
    }

    /// Rafraîchit sans montrer (appelé à chaque update SwiftUI : chargement de vignettes, ajout de captures…).
    func refreshGeometry() { needsDisplay = true }

    func flashIfScrollable() { if canScroll { showThumb() } }

    // MARK: Fade

    private func showThumb() {
        fadeTimer?.invalidate()
        NSAnimationContext.runAnimationGroup { $0.duration = 0.12; animator().alphaValue = 1 }
        scheduleFade()
    }
    private func scheduleFade() {
        fadeTimer?.invalidate()
        fadeTimer = Timer.scheduledTimer(withTimeInterval: 1.1, repeats: false) { [weak self] _ in
            guard let self, !self.isDragging else { return }
            NSAnimationContext.runAnimationGroup { $0.duration = 0.4; self.animator().alphaValue = 0 }
        }
    }

    // MARK: Hit-test (seul le pouce est cliquable → le reste laisse passer clics/molette au contenu)

    override func hitTest(_ point: NSPoint) -> NSView? {
        guard canScroll else { return nil }
        let local = convert(point, from: superview)
        return thumbRect().insetBy(dx: -6, dy: -4).contains(local) ? self : nil
    }

    // La molette au-dessus du pouce doit quand même défiler le contenu.
    override func scrollWheel(with event: NSEvent) { scrollView?.scrollWheel(with: event) }

    // MARK: Drag-to-scroll

    override func mouseDown(with event: NSEvent) {
        guard canScroll else { return }
        isDragging = true
        let p = convert(event.locationInWindow, from: nil)
        let thumb = thumbRect()
        dragGrabOffset = thumb.contains(p) ? (p.y - thumb.minY) : thumb.height / 2
        showThumb()
    }

    override func mouseDragged(with event: NSEvent) {
        guard isDragging, canScroll else { return }
        let track = trackRect()
        let travel = track.height - thumbRect().height
        guard travel > 0 else { return }
        let p = convert(event.locationInWindow, from: nil)
        let top = min(max(p.y - dragGrabOffset, track.minY), track.minY + travel)
        scroll(toFraction: (top - track.minY) / travel)
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        isDragging = false
        scheduleFade()
    }

    // Redessine quand le scrollview est redimensionné (fenêtre, collapse/expand de la sidebar).
    override func setFrameSize(_ newSize: NSSize) { super.setFrameSize(newSize); needsDisplay = true }
}

/// Badge « lecture » centré sur une vignette VIDÉO : cercle sombre translucide + triangle blanc, façon
/// lecteur vidéo. Permet de distinguer d'un coup d'œil une vidéo d'une capture d'image dans les galeries
/// (sidebar + historique). `size` = diamètre du cercle.
struct VideoPlayBadge: View {
    var size: CGFloat = 44
    var body: some View {
        ZStack {
            Circle().fill(Color.black.opacity(0.5))
            Image(systemName: "play.fill")
                .font(.system(size: size * 0.42, weight: .semibold))
                .foregroundStyle(.white)
                .offset(x: size * 0.03)                 // centrage OPTIQUE du triangle (il « penche » à droite)
        }
        .frame(width: size, height: size)
        .shadow(color: .black.opacity(0.35), radius: size * 0.09, y: 1)
    }
}
