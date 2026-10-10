import AppKit

// Point d'entrée. Un fichier nommé main.swift EST le point d'entrée (ne pas ajouter @main).
// Tout le démarrage AppKit est sur le main actor → on s'y isole explicitement.
MainActor.assumeIsolated {
    let app = NSApplication.shared
    app.setActivationPolicy(.accessory)          // app de barre des menus, sans icône Dock

    // NSApplication.delegate est WEAK : withExtendedLifetime garde le delegate vivant
    // pendant toute la durée de app.run() (qui ne retourne jamais).
    let delegate = AppDelegate()
    app.delegate = delegate
    withExtendedLifetime(delegate) {
        app.run()
    }
}
