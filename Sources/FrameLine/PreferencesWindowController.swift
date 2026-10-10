import AppKit
import SwiftUI

/// Fenêtre de réglages — même modèle que Flow : barre d'outils « préférences » (icône + libellé
/// au-dessus), contenu SwiftUI en `Form` groupé. Les libellés d'onglets + le contenu suivent la
/// langue d'affichage (I18n) et se resynchronisent à chaud.
///
/// Apparence : comme Flow, CETTE fenêtre suit l'apparence SYSTÈME (clair/sombre macOS), même quand le
/// reste de l'app est verrouillé sur un thème via `ThemeManager` (NSApp.appearance). On lit le réglage
/// macOS réel (`AppleInterfaceStyle`, insensible à l'override d'app) et on le pose sur la fenêtre ; un
/// override `window.appearance` prime sur `NSApp.appearance` pour cette seule fenêtre.
@MainActor
final class PreferencesWindowController: NSWindowController {
    private let toolbarController = SettingsToolbarController()

    convenience init() {
        let host = NSHostingController(rootView: SettingsView().environmentObject(I18n.shared))
        host.sizingOptions = .preferredContentSize

        // Fenêtre standard (PAS AlwaysActiveWindow) : EXACTEMENT comme Flow / CodexBar. Une fenêtre
        // qui force hasKeyAppearance=true affichait des feux colorés (faux « actif ») PENDANT que le
        // contenu SwiftUI se grisait comme une fenêtre inactive (interrupteurs/pickers ternes au lieu
        // du bleu d'accent). Une NSWindow normale devient réellement key → contrôles bleus, identiques
        // à Flow. (AlwaysActiveWindow reste réservé à la fenêtre principale, utile pendant une capture.)
        let win = NSWindow(contentViewController: host)
        win.title = "FrameLine"
        win.styleMask = [.titled, .closable]
        win.isReleasedWhenClosed = false
        win.setContentSize(host.view.fittingSize)
        win.toolbarStyle = .preference                       // icône au-dessus du libellé
        win.center()

        self.init(window: win)

        // Delegate AVANT d'affecter la barre à la fenêtre → les items sont construits.
        let toolbar = NSToolbar(identifier: "frameline.settings")
        toolbar.delegate = toolbarController
        toolbar.displayMode = .iconAndLabel
        toolbar.allowsUserCustomization = false
        win.toolbar = toolbar
        syncToolbar()
        NotificationCenter.default.addObserver(self, selector: #selector(syncToolbar),
                                               name: .displayLanguageChanged, object: nil)

        // La fenêtre Réglages suit l'apparence système (comme Flow), pas le thème forcé de l'app.
        applySystemAppearance()
        DistributedNotificationCenter.default().addObserver(
            self, selector: #selector(systemAppearanceChanged),
            name: NSNotification.Name("AppleInterfaceThemeChangedNotification"), object: nil)
    }

    /// Sélection courante + libellés d'onglets dans la langue d'affichage active.
    @objc private func syncToolbar() {
        let current = UserDefaults.standard.string(forKey: "settingsTab") ?? "general"
        guard let toolbar = window?.toolbar else { return }
        toolbar.selectedItemIdentifier = NSToolbarItem.Identifier(current)
        for item in toolbar.items {
            if let entry = ES_SETTINGS_TABS.first(where: { $0.id == item.itemIdentifier.rawValue }) {
                item.label = I18n.shared.t(entry.key)
            }
        }
    }

    func reload() { syncToolbar(); applySystemAppearance() }

    /// Applique l'apparence SYSTÈME macOS (clair/sombre) à cette fenêtre — insensible au thème forcé
    /// par `ThemeManager` (NSApp.appearance). `AppleInterfaceStyle` vaut "Dark" en sombre, nil en clair.
    private func applySystemAppearance() {
        let isDark = UserDefaults.standard.string(forKey: "AppleInterfaceStyle") == "Dark"
        window?.appearance = NSAppearance(named: isDark ? .darkAqua : .aqua)
    }

    /// L'utilisateur bascule l'apparence macOS pendant que Réglages est ouvert → resync à chaud.
    @objc private func systemAppearanceChanged() {
        DispatchQueue.main.async { [weak self] in self?.applySystemAppearance() }
    }
}
