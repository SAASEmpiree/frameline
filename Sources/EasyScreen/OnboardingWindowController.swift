import AppKit
import SwiftUI

/// Fenêtre du tutoriel de premier lancement.
@MainActor
final class OnboardingWindowController: NSObject, NSWindowDelegate {
    static let shared = OnboardingWindowController()
    private var window: NSWindow?
    var onComplete: (() -> Void)?

    func show() {
        if window == nil { build() }
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        window?.center()
        window?.makeKeyAndOrderFront(nil)
    }

    private func build() {
        let root = OnboardingView(onFinish: { [weak self] in self?.finish() })
        let hosting = NSHostingController(rootView: root)
        hosting.sizingOptions = []

        // AlwaysActiveWindow : feux tricolores toujours colorés (cf. MainWindowController).
        let w = AlwaysActiveWindow(contentViewController: hosting)
        w.styleMask = [.titled, .closable, .fullSizeContentView]
        w.titlebarAppearsTransparent = true
        w.titleVisibility = .hidden
        w.isReleasedWhenClosed = false
        w.setContentSize(NSSize(width: 640, height: 500))
        w.center()
        w.delegate = self
        window = w
    }

    private var didComplete = false

    func windowWillClose(_ notification: Notification) {
        // Fermer via le bouton rouge équivaut à « Terminer » (sinon : tutoriel rejoué + pas de fenêtre).
        complete()
    }

    private func finish() {
        complete()
        window?.close()
    }

    private func complete() {
        guard !didComplete else { return }
        didComplete = true
        Preferences.shared.onboardingCompleted = true
        onComplete?()
    }
}
