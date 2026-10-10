import AppKit
import SwiftUI

extension NSToolbarItem.Identifier {
    static let esRegion = NSToolbarItem.Identifier("esRegion")
    static let esWindow = NSToolbarItem.Identifier("esWindow")
    static let esFull = NSToolbarItem.Identifier("esFull")
    static let esTitle = NSToolbarItem.Identifier("esTitle")
    static let esToggleSidebar = NSToolbarItem.Identifier("esToggleSidebar")
    static let esSidebarSpace = NSToolbarItem.Identifier("esSidebarSpace")
}

/// Fenêtre principale = vraie app macOS : NSSplitViewController (sidebar + détail) + NSToolbar UNIFIÉE.
/// macOS gère alors NATIVEMENT : centrage des feux tricolores sur la barre, alignement sidebar
/// (sidebarTrackingSeparator), et le comportement plein écran (apparition/décalage au survol) —
/// exactement comme la référence.
@MainActor
final class MainWindowController: NSObject, NSWindowDelegate, NSToolbarDelegate, NSMenuItemValidation {
    static let shared = MainWindowController()
    private var window: NSWindow?
    private var splitVC: NSSplitViewController?
    private var sidebarItem: NSSplitViewItem?        // colonne des captures (item de split ORDINAIRE, cf. build)
    private var menuInstalled = false
    // Espaceur maison (remplace NSTrackingSeparatorToolbarItem, qui ne s'accroche pas à un split ORDINAIRE) :
    // sa largeur comble l'espace entre la bascule et le diviseur → les icônes + titre démarrent au bord de la
    // zone contenu (noire). Sidebar REPLIÉE : l'item est RETIRÉ (icônes groupées à gauche, façon BridgeShot) ;
    // DÉPLIÉE : ré-inséré. Largeur pilotée par le SEUL intrinsicContentSize (cf. ToolbarSpacer + syncSidebarSpacer).
    private weak var sidebarSpacerView: ToolbarSpacer?

    func show() {
        ESLog.snapshot("MainWindowController.show ENTER (window=\(window != nil ? "exists" : "nil"))")
        ESLog.stack("MainWindowController.show")
        if window == nil { build() }
        NSApp.setActivationPolicy(.regular)
        installMainMenuIfNeeded()
        // Recette Flow (cf. AppDelegate.showPreferences) : makeKeyAndOrderFront AVANT NSApp.activate. C'est
        // l'activation FINALE qui tire la fenêtre déjà ordonnée sur le bureau (Space) courant ; activer
        // d'abord (app accessoire) laissait la galerie derrière l'app active ou sur son ancien Space.
        if let w = window, w.isMiniaturized { w.deminiaturize(nil) }
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        CaptureLibrary.shared.refresh()
        DispatchQueue.main.async { [weak self] in self?.syncSidebarSpacer() }
        #if DEBUG
        if ProcessInfo.processInfo.environment["ES_DEBUG"] != nil {
            NSLog("ES-DEBUG window visible=\(window?.isVisible ?? false) frame=\(NSStringFromRect(window?.frame ?? .zero)) policy=\(NSApp.activationPolicy().rawValue)")
        }
        #endif
    }

    func toggle() {
        if let w = window, w.isVisible { w.performClose(nil) } else { show() }
    }

    private func build() {
        let coord = AppCoordinator.shared, lib = CaptureLibrary.shared

        let sidebar = NSHostingController(rootView: SidebarView().dropImport()
            .environmentObject(coord).environmentObject(lib))
        // ⚠️ macOS 26 (Tahoe) : `sidebarWithViewController:` déclenche le traitement « Liquid Glass » — AppKit
        // insère un panneau FLOTTANT arrondi et ENCASTRÉ pour la sidebar. La liste des captures apparaît alors
        // comme une 2e couche posée SUR la fenêtre, et le verre de la fenêtre/bureau perce dans l'encart (les
        // « slivers » colorés au bord gauche). On utilise donc un item de split ORDINAIRE : la colonne est
        // pleine largeur, à ras des bords, et le fond gris opaque (`SolidSidebarBacking`) est l'UNIQUE couche.
        let sidebarItem = NSSplitViewItem(viewController: sidebar)
        sidebarItem.minimumThickness = 260
        sidebarItem.maximumThickness = 400
        sidebarItem.canCollapse = true
        sidebarItem.holdingPriority = NSLayoutConstraint.Priority(260)   // le détail absorbe le redimensionnement
        self.sidebarItem = sidebarItem

        let detail = NSHostingController(rootView: DetailView().dropImport()
            .environmentObject(coord).environmentObject(lib))
        let detailItem = NSSplitViewItem(viewController: detail)

        let split = NSSplitViewController()
        split.addSplitViewItem(sidebarItem)
        split.addSplitViewItem(detailItem)
        splitVC = split

        // L'espaceur suit le diviseur quand il bouge (drag/resize fenêtre). Observateur UNIQUE ici (PAS dans la
        // fabrique de l'item, qui peut être appelée plusieurs fois au repli/dépli → sinon doublons d'observateur).
        NotificationCenter.default.addObserver(self, selector: #selector(syncSidebarSpacer),
            name: NSSplitView.didResizeSubviewsNotification, object: split.splitView)

        // AlwaysActiveWindow (fichier voisin) force hasKeyAppearance=true : les feux
        // tricolores restent colorés même fenêtre inactive / app désactivée par l'overlay
        // de capture, là où le gris système était invisible sur la barre de titre sombre.
        let w = AlwaysActiveWindow(contentRect: NSRect(x: 0, y: 0, width: 1080, height: 700),
                         styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                         backing: .buffered, defer: false)
        w.title = "FrameLine"
        w.titleVisibility = .hidden                      // on affiche notre propre titre dans la toolbar
        w.contentViewController = split
        // Établir la position du diviseur + forcer le layout AVANT d'installer la toolbar : le
        // NSTrackingSeparatorToolbarItem s'accroche au diviseur À SA CRÉATION. Si le split n'a pas encore
        // de frame/diviseur valide, il retombe en séparateur STATIQUE → les items « côté contenu » (les 3
        // icônes de capture) se collent à gauche, PAR-DESSUS la sidebar grise, et seul le titre déborde sur
        // le noir. On lui donne donc un diviseur déjà posé pour qu'il aligne bien tout le groupe sur le contenu.
        split.view.layoutSubtreeIfNeeded()
        split.splitView.setPosition(292, ofDividerAt: 0)
        split.splitView.layoutSubtreeIfNeeded()

        let toolbar = NSToolbar(identifier: "FrameLineToolbar")
        toolbar.delegate = self
        toolbar.displayMode = .iconOnly
        toolbar.allowsUserCustomization = false
        w.toolbar = toolbar
        w.toolbarStyle = .unified
        // Barre du haut TRANSPARENTE : le contenu remonte dessous → noir du détail à droite,
        // matériau vibrant de la sidebar à gauche (pas de bandeau gris). Comme la référence.
        w.titlebarAppearsTransparent = true

        w.isReleasedWhenClosed = false
        // Après une capture (showPreview), si la galerie vit sur un AUTRE Space,
        // .moveToActiveSpace la fait REJOINDRE le Space actif au lieu de faire
        // BASCULER macOS vers son ancien Space (animation 'swoosh' / 'espace de
        // retour' indésirable). .fullScreenPrimary conserve le bouton plein écran.
        w.collectionBehavior = [.moveToActiveSpace, .fullScreenPrimary]
        w.minSize = NSSize(width: 860, height: 540)
        w.setFrameAutosaveName("FrameLineMain")
        w.delegate = self
        w.centerOnActiveScreen()      // 1re ouverture : centrée sur l'écran actif (l'autosave mémorise ensuite)
        window = w
    }

    // Plein écran → bandeau gris classique (comme la référence). Fenêtré → bandeau transparent
    // (le noir du détail / le vibrant de la sidebar remonte dessous).
    func windowWillEnterFullScreen(_ notification: Notification) {
        window?.titlebarAppearsTransparent = false
        DispatchQueue.main.async { [weak self] in self?.syncSidebarSpacer() }
    }
    func windowWillExitFullScreen(_ notification: Notification) {
        window?.titlebarAppearsTransparent = true
        DispatchQueue.main.async { [weak self] in self?.syncSidebarSpacer() }
    }
    // La largeur du diviseur en coordonnées fenêtre change à chaque resize → l'espaceur doit suivre.
    func windowDidResize(_ notification: Notification) { syncSidebarSpacer() }

    func windowWillClose(_ notification: Notification) {
        DispatchQueue.main.async {
            // Dès qu'il n'y a plus de VRAIE fenêtre UI visible, on redevient un pur agent menubar
            // (.accessory) : pas d'icône Dock, et surtout la capture ne ramène plus l'app au premier
            // plan. ⚠️ L'ancien test comptait NSStatusBarWindow (TOUJOURS visible et PAS un NSPanel) →
            // `anyVisible` restait true → l'app ne repassait JAMAIS en .accessory. On exige donc en plus
            // `canBecomeMain` (false pour la barre de menus et les panneaux flottants/overlays) : seules
            // la galerie, l'aperçu et l'onboarding comptent.
            let anyVisible = NSApp.windows.contains {
                $0 != self.window && $0.isVisible && !($0 is NSPanel) && $0.canBecomeMain
            }
            if !anyVisible { NSApp.setActivationPolicy(.accessory) }
        }
    }

    // MARK: - NSToolbarDelegate

    // .esToggleSidebar = notre bouton maison (le .toggleSidebar SYSTÈME n'agit que sur un item de
    // comportement « sidebar » ; comme on utilise un item ordinaire, on replie/déplie nous-mêmes).
    private let toolbarItems: [NSToolbarItem.Identifier] =
        [.esToggleSidebar, .esSidebarSpace, .esRegion, .esWindow, .esFull, .esTitle]

    func toolbarDefaultItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] { toolbarItems }
    func toolbarAllowedItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] { toolbarItems }

    func toolbar(_ toolbar: NSToolbar, itemForItemIdentifier id: NSToolbarItem.Identifier,
                 willBeInsertedIntoToolbar flag: Bool) -> NSToolbarItem? {
        switch id {
        case .esToggleSidebar:
            return captureItem(id, "sidebar.left", "Masquer/afficher la barre latérale", #selector(tbToggleSidebar))
        case .esSidebarSpace:
            return sidebarSpacerItem(id)
        case .esRegion: return captureItem(id, "viewfinder", "Capturer une zone", #selector(tbRegion))
        case .esWindow: return captureItem(id, "macwindow", "Capturer une fenêtre", #selector(tbWindow))
        case .esFull:   return captureItem(id, "display", "Capturer tout l'écran", #selector(tbFull))
        case .esTitle:  return titleItem(id)
        default: return nil
        }
    }

    private func captureItem(_ id: NSToolbarItem.Identifier, _ symbol: String,
                             _ label: String, _ action: Selector) -> NSToolbarItem {
        let item = NSToolbarItem(itemIdentifier: id)
        item.label = label
        item.toolTip = label
        item.image = NSImage(systemSymbolName: symbol, accessibilityDescription: label)
        // Icônes PLATES (façon BridgeShot) : PAS de `isBordered` → macOS 26 (Tahoe) ne pose plus la
        // capsule « Liquid Glass » derrière chaque bouton (le « truc par-dessus » visible surtout sur
        // la bascule sidebar posée sur le vibrant gris). Simple glyphe cliquable + surbrillance au survol.
        item.isBordered = false
        item.target = self
        item.action = action
        return item
    }

    private func titleItem(_ id: NSToolbarItem.Identifier) -> NSToolbarItem {
        let item = NSToolbarItem(itemIdentifier: id)
        let label = NSTextField(labelWithString: "FrameLine")
        label.font = .systemFont(ofSize: 14, weight: .bold)
        item.view = label
        return item
    }

    /// Espaceur à largeur PILOTÉE (via `intrinsicContentSize`, cf. `ToolbarSpacer`) : sa largeur est recalculée
    /// (`syncSidebarSpacer`) pour que son bord droit tombe pile sur le diviseur sidebar↔détail. Résultat : les
    /// icônes qui le suivent démarrent au bord de la zone contenu (noire) quand la sidebar est ouverte, et se
    /// regroupent à gauche (largeur 0) quand elle est repliée — comme BridgeShot, sans NSTrackingSeparatorToolbarItem.
    private func sidebarSpacerItem(_ id: NSToolbarItem.Identifier) -> NSToolbarItem {
        let item = NSToolbarItem(itemIdentifier: id)
        let v = ToolbarSpacer()
        v.translatesAutoresizingMaskIntoConstraints = false
        // Largeur initiale ≈ diviseur ACTUEL : correcte au build (sidebar ouverte), et ≈ 0 si ré-inséré pendant
        // un dépli (pane encore replié) → l'insertion NSToolbar ne pousse AUCUN voisin (delta nul = invisible).
        v.spacerWidth = max(0, (splitVC?.splitView.arrangedSubviews.first?.bounds.width ?? 260) - 128)
        item.view = v
        // PAS de minSize/maxSize : l'espaceur est piloté par le SEUL intrinsicContentSize (un pilote unique évite
        // deux invalidations de layout concurrentes par image). NSZeroSize par défaut = « taille = celle de la vue ».
        sidebarSpacerView = v
        return item
    }

    /// Ajuste la largeur de l'espaceur pour combler l'espace [bord droit de l'espaceur → diviseur], mesuré
    /// sur les positions RÉELLES des vues (coordonnées fenêtre). Idempotent, converge en une passe car la
    /// position de DÉPART de l'espaceur (après le bouton bascule) ne dépend pas de sa largeur.
    @objc private func syncSidebarSpacer() {
        guard let spacer = sidebarSpacerView, spacer.window != nil,
              let pane = splitVC?.splitView.arrangedSubviews.first else { return }
        // Suit le diviseur d'après la GÉOMÉTRIE réelle du pane → pendant l'animation de repli/dépli
        // (didResizeSubviews, image par image), l'espaceur rétrécit/grandit AVEC le pane et les icônes glissent
        // en suivant. UN SEUL pilote de largeur = intrinsicContentSize (via spacerWidth).
        let dividerX = pane.convert(NSPoint(x: pane.bounds.maxX, y: 0), to: nil).x
        let spacerLeadingX = spacer.convert(.zero, to: nil).x
        let newW = max(0, dividerX - spacerLeadingX)
        // Écriture INSTANTANÉE : on neutralise toute animation implicite du re-tuilage NSToolbar → l'espaceur
        // SNAPPE sur la géométrie réelle du diviseur à chaque image. La seule courbe visible reste celle du split.
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0
            ctx.allowsImplicitAnimation = false
            spacer.spacerWidth = newW
        }
    }

    @objc private func tbRegion() { AppCoordinator.shared.captureRegion() }
    @objc private func tbWindow() { AppCoordinator.shared.captureWindow() }
    @objc private func tbFull() { AppCoordinator.shared.captureFull() }
    @objc private func tbToggleSidebar() {
        guard let sidebarItem, let tb = window?.toolbar else { return }
        let willCollapse = !sidebarItem.isCollapsed

        // DÉPLI : ré-insérer l'espaceur AVANT d'animer, tant que le pane est encore replié. La fabrique le crée
        // à w0 ≈ 0 → l'insertion NSToolbar ne pousse AUCUN voisin (delta nul = invisible). Il GRANDIT ensuite
        // via syncSidebarSpacer, image par image, sur la SEULE courbe du split.
        if !willCollapse, !tb.items.contains(where: { $0.itemIdentifier == .esSidebarSpace }) {
            let idx = (tb.items.firstIndex { $0.itemIdentifier == .esToggleSidebar }).map { $0 + 1 } ?? 1
            tb.insertItem(withItemIdentifier: .esSidebarSpace, at: idx)
        }

        // UNE SEULE animation visible : le split. didResizeSubviews (par image) fait glisser l'espaceur AVEC le
        // diviseur. Le completionHandler fige l'état final SANS relancer d'animation ni « nombre magique » :
        //   • repli → removeItem alors que la largeur vaut déjà ≈ 0 (retrait invisible) → icônes groupées à gauche ;
        //   • dépli → un dernier sync instantané cale pile sur le diviseur.
        NSAnimationContext.runAnimationGroup({ _ in
            sidebarItem.animator().isCollapsed = willCollapse
        }, completionHandler: { [weak self] in
            guard let self else { return }
            if sidebarItem.isCollapsed {
                if let idx = tb.items.firstIndex(where: { $0.itemIdentifier == .esSidebarSpace }) {
                    tb.removeItem(at: idx)
                }
            } else {
                self.syncSidebarSpacer()
            }
        })
    }
    @objc private func tbTrashSelection() { AppCoordinator.shared.trashSelection() }

    /// Le raccourci ⌘⌫ « corbeille » ne doit PAS s'activer pendant une édition de texte (champ de l'outil
    /// texte de l'éditeur d'annotation : ⌘⌫ = « supprimer jusqu'au début de ligne » y est légitime), ni
    /// quand rien n'est sélectionné. On désactive alors l'item pour laisser le raccourci au responder texte.
    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        guard menuItem.action == #selector(tbTrashSelection) else { return true }
        // Le raccourci ⌘⌫ est réservé à la GALERIE au premier plan. Si une AUTRE fenêtre est active
        // (éditeur d'annotation flottant, réglages, aperçu épinglé…), on le désactive → il ne détruit
        // jamais la sélection depuis un autre contexte, et ⌘⌫ y reste disponible à l'édition de texte.
        guard let win = window, NSApp.keyWindow === win else { return false }
        // Ni pendant une édition de texte DANS la galerie (outil texte embarqué : ⌘⌫ = suppr. début de ligne).
        if win.firstResponder is NSText { return false }
        return !CaptureLibrary.shared.actionableSelection.isEmpty
    }

    // MARK: - Barre de menus (app LSUIElement → aucune par défaut)

    func installMainMenuIfNeeded() {
        guard !menuInstalled else { return }
        menuInstalled = true
        let main = NSMenu()

        let appItem = NSMenuItem(); main.addItem(appItem)
        let appMenu = NSMenu()
        let prefs = NSMenuItem(title: "Réglages…", action: #selector(openPrefs), keyEquivalent: ",")
        prefs.target = self
        appMenu.addItem(prefs)
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Masquer FrameLine",
                        action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(withTitle: "Quitter FrameLine",
                        action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu

        let editItem = NSMenuItem(); main.addItem(editItem)
        let editMenu = NSMenu(title: "Édition")
        editMenu.addItem(withTitle: "Annuler", action: Selector(("undo:")), keyEquivalent: "z")
        editMenu.addItem(withTitle: "Rétablir", action: Selector(("redo:")), keyEquivalent: "Z")
        editMenu.addItem(.separator())
        editMenu.addItem(withTitle: "Couper", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        editMenu.addItem(withTitle: "Copier", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "Coller", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: "Tout sélectionner",
                         action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editMenu.addItem(.separator())
        // ⌘⌫ = mettre la sélection (galerie) à la corbeille, façon Finder.
        let trashSel = NSMenuItem(title: "Mettre la sélection à la corbeille",
                                  action: #selector(tbTrashSelection), keyEquivalent: "\u{8}")
        trashSel.keyEquivalentModifierMask = [.command]
        trashSel.target = self
        editMenu.addItem(trashSel)
        editItem.submenu = editMenu

        let winItem = NSMenuItem(); main.addItem(winItem)
        let winMenu = NSMenu(title: "Fenêtre")
        winMenu.addItem(withTitle: "Fermer",
                        action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        winMenu.addItem(withTitle: "Réduire",
                        action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        winItem.submenu = winMenu
        NSApp.windowsMenu = winMenu

        NSApp.mainMenu = main
    }

    @objc private func openPrefs() { AppCoordinator.shared.openPreferences() }
}

/// Espaceur de barre d'outils à largeur PILOTÉE via `intrinsicContentSize`. Changer `spacerWidth` invalide la
/// taille intrinsèque → NSToolbar RE-TUILE l'item et repositionne les icônes qui suivent. ⚠️ Diagnostiqué au
/// log : modifier seulement la CONSTANTE d'une contrainte de largeur ne déclenche PAS ce reflux hors
/// redimensionnement de fenêtre (l'écart restait donc figé au repli de la sidebar). La taille intrinsèque, si.
private final class ToolbarSpacer: NSView {
    var spacerWidth: CGFloat = 260 {
        didSet { if abs(spacerWidth - oldValue) > 0.5 { invalidateIntrinsicContentSize() } }
    }
    override var intrinsicContentSize: NSSize { NSSize(width: max(0, spacerWidth), height: 28) }
}
