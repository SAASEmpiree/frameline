import AppKit
import SwiftUI

extension NSToolbarItem.Identifier {
    static let esRegion = NSToolbarItem.Identifier("esRegion")
    static let esWindow = NSToolbarItem.Identifier("esWindow")
    static let esFull = NSToolbarItem.Identifier("esFull")
    static let esTitle = NSToolbarItem.Identifier("esTitle")
}

/// Fenêtre principale = vraie app macOS : NSSplitViewController (sidebar + détail) + NSToolbar UNIFIÉE.
/// macOS gère alors NATIVEMENT : centrage des feux tricolores sur la barre, alignement sidebar
/// (sidebarTrackingSeparator), et le comportement plein écran (apparition/décalage au survol) —
/// exactement comme la référence.
@MainActor
final class MainWindowController: NSObject, NSWindowDelegate, NSToolbarDelegate {
    static let shared = MainWindowController()
    private var window: NSWindow?
    private var splitVC: NSSplitViewController?
    private var menuInstalled = false

    func show() {
        ESLog.snapshot("MainWindowController.show ENTER (window=\(window != nil ? "exists" : "nil"))")
        ESLog.stack("MainWindowController.show")
        if window == nil { build() }
        NSApp.setActivationPolicy(.regular)
        installMainMenuIfNeeded()
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
        CaptureLibrary.shared.refresh()
        if ProcessInfo.processInfo.environment["ES_DEBUG"] != nil {
            NSLog("ES-DEBUG window visible=\(window?.isVisible ?? false) frame=\(NSStringFromRect(window?.frame ?? .zero)) policy=\(NSApp.activationPolicy().rawValue)")
        }
    }

    func toggle() {
        if let w = window, w.isVisible { w.performClose(nil) } else { show() }
    }

    private func build() {
        let coord = AppCoordinator.shared, lib = CaptureLibrary.shared

        let sidebar = NSHostingController(rootView: SidebarView().dropImport()
            .environmentObject(coord).environmentObject(lib))
        let sidebarItem = NSSplitViewItem(sidebarWithViewController: sidebar)
        sidebarItem.minimumThickness = 240
        sidebarItem.maximumThickness = 320
        sidebarItem.canCollapse = true

        let detail = NSHostingController(rootView: DetailView().dropImport()
            .environmentObject(coord).environmentObject(lib))
        let detailItem = NSSplitViewItem(viewController: detail)

        let split = NSSplitViewController()
        split.addSplitViewItem(sidebarItem)
        split.addSplitViewItem(detailItem)
        splitVC = split

        // AlwaysActiveWindow (fichier voisin) force hasKeyAppearance=true : les feux
        // tricolores restent colorés même fenêtre inactive / app désactivée par l'overlay
        // de capture, là où le gris système était invisible sur la barre de titre sombre.
        let w = AlwaysActiveWindow(contentRect: NSRect(x: 0, y: 0, width: 1080, height: 700),
                         styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                         backing: .buffered, defer: false)
        w.title = "Easy Screen"
        w.titleVisibility = .hidden                      // on affiche notre propre titre dans la toolbar
        w.contentViewController = split

        let toolbar = NSToolbar(identifier: "EasyScreenToolbar")
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
        w.setFrameAutosaveName("EasyScreenMain")
        w.delegate = self
        w.center()
        window = w
        split.splitView.setPosition(272, ofDividerAt: 0)
    }

    // Plein écran → bandeau gris classique (comme la référence). Fenêtré → bandeau transparent
    // (le noir du détail / le vibrant de la sidebar remonte dessous).
    func windowWillEnterFullScreen(_ notification: Notification) {
        window?.titlebarAppearsTransparent = false
    }
    func windowWillExitFullScreen(_ notification: Notification) {
        window?.titlebarAppearsTransparent = true
    }

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

    private let toolbarItems: [NSToolbarItem.Identifier] =
        [.toggleSidebar, .sidebarTrackingSeparator, .esRegion, .esWindow, .esFull, .esTitle]

    func toolbarDefaultItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] { toolbarItems }
    func toolbarAllowedItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] { toolbarItems }

    func toolbar(_ toolbar: NSToolbar, itemForItemIdentifier id: NSToolbarItem.Identifier,
                 willBeInsertedIntoToolbar flag: Bool) -> NSToolbarItem? {
        switch id {
        case .sidebarTrackingSeparator:
            guard let split = splitVC else { return nil }
            return NSTrackingSeparatorToolbarItem(identifier: id, splitView: split.splitView, dividerIndex: 0)
        case .esRegion: return captureItem(id, "viewfinder", "Capturer une zone", #selector(tbRegion))
        case .esWindow: return captureItem(id, "macwindow", "Capturer une fenêtre", #selector(tbWindow))
        case .esFull:   return captureItem(id, "display", "Capturer tout l'écran", #selector(tbFull))
        case .esTitle:  return titleItem(id)
        default: return nil            // .toggleSidebar est fourni par le système (NSSplitViewController)
        }
    }

    private func captureItem(_ id: NSToolbarItem.Identifier, _ symbol: String,
                             _ label: String, _ action: Selector) -> NSToolbarItem {
        let item = NSToolbarItem(itemIdentifier: id)
        item.label = label
        item.toolTip = label
        item.image = NSImage(systemSymbolName: symbol, accessibilityDescription: label)
        item.isBordered = true
        item.target = self
        item.action = action
        return item
    }

    private func titleItem(_ id: NSToolbarItem.Identifier) -> NSToolbarItem {
        let item = NSToolbarItem(itemIdentifier: id)
        let label = NSTextField(labelWithString: "Easy Screen")
        label.font = .systemFont(ofSize: 14, weight: .bold)
        item.view = label
        return item
    }

    @objc private func tbRegion() { AppCoordinator.shared.captureRegion() }
    @objc private func tbWindow() { AppCoordinator.shared.captureWindow() }
    @objc private func tbFull() { AppCoordinator.shared.captureFull() }

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
        appMenu.addItem(withTitle: "Masquer Easy Screen",
                        action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(withTitle: "Quitter Easy Screen",
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
