import AppKit
import SwiftUI

/// Fenêtre de réglages — héberge SettingsView (SwiftUI).
@MainActor
final class PreferencesWindowController: NSWindowController {
    convenience init() {
        // AlwaysActiveWindow : feux tricolores toujours colorés (cf. MainWindowController).
        let win = AlwaysActiveWindow(contentRect: NSRect(x: 0, y: 0, width: 560, height: 540),
                           styleMask: [.titled, .closable, .fullSizeContentView],
                           backing: .buffered, defer: false)
        win.titlebarAppearsTransparent = true
        win.titleVisibility = .hidden
        win.isReleasedWhenClosed = false
        win.center()
        self.init(window: win)
        win.contentViewController = NSHostingController(
            rootView: SettingsView(onClose: { [weak win] in win?.performClose(nil) }))
    }

    /// @AppStorage se resynchronise tout seul ; rien à recharger.
    func reload() {}
}
