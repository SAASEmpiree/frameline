import AppKit

/// Applique le thème choisi (système / clair / sombre) à toute l'app via NSApp.appearance.
@MainActor
enum ThemeManager {
    static func apply() {
        switch Preferences.shared.theme {
        case "light": NSApp.appearance = NSAppearance(named: .aqua)
        case "dark":  NSApp.appearance = NSAppearance(named: .darkAqua)
        default:      NSApp.appearance = nil        // « système » : suit les Réglages macOS
        }
    }
}
