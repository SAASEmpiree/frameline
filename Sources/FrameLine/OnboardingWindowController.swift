import AppKit
import SwiftUI

/// Fenêtre du tutoriel de premier lancement.
@MainActor
final class OnboardingWindowController: NSObject, NSWindowDelegate {
    static let shared = OnboardingWindowController()
    private var window: NSWindow?
    var onComplete: (() -> Void)?

    func show() {
        // Chaque ouverture repart d'un tutoriel NEUF (étape 1) et d'un `didComplete` remis à zéro : sinon,
        // rouvert via « Setup Guide », sa fermeture ne rappelait plus `onComplete` → l'app restait en
        // `.regular` (icône Dock) sans aucune fenêtre, et la bibliothèque ne s'ouvrait pas.
        if window == nil { didComplete = false; build() }
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        window?.collectionBehavior.insert(.moveToActiveSpace)
        window?.centerOnActiveScreen()
        window?.makeKeyAndOrderFront(nil)
    }

    private func build() {
        let root = OnboardingView(
            onFinish: { [weak self] in self?.finish() },
            onStartCapture: { [weak self] in self?.finishCapturing() })
        let hosting = NSHostingController(rootView: root)
        hosting.sizingOptions = []

        // AlwaysActiveWindow : feux tricolores toujours colorés (cf. MainWindowController).
        let w = AlwaysActiveWindow(contentViewController: hosting)
        w.styleMask = [.titled, .closable, .fullSizeContentView]
        w.titlebarAppearsTransparent = true
        w.titleVisibility = .hidden
        w.isReleasedWhenClosed = false
        w.setContentSize(NSSize(width: 660, height: 640))
        w.center()
        w.delegate = self
        window = w
    }

    private var didComplete = false

    func windowWillClose(_ notification: Notification) {
        // Fermer via le bouton rouge équivaut à « Terminer » (sinon : tutoriel rejoué + pas de fenêtre).
        complete()
        // Libère la fenêtre (et son OnboardingView, dont le minuteur interroge la permission chaque
        // seconde) une fois la fermeture terminée — jamais pendant `close()` lui-même.
        let closing = notification.object as? NSWindow
        DispatchQueue.main.async { [weak self] in
            guard let self, let closing, self.window === closing, !closing.isVisible else { return }
            closing.delegate = nil
            self.window = nil
        }
    }

    private func finish() {
        complete()
        window?.close()
    }

    /// « Take Your First Screenshot » : termine l'onboarding (ouvre la bibliothèque) puis lance
    /// immédiatement une capture de zone — la capture atterrit dans la bibliothèque déjà ouverte.
    private func finishCapturing() {
        complete()
        window?.close()
        AppCoordinator.shared.captureRegion()
    }

    private func complete() {
        guard !didComplete else { return }
        didComplete = true
        Preferences.shared.onboardingCompleted = true
        onComplete?()
    }
}
