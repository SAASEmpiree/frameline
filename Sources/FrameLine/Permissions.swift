import AppKit
import CoreGraphics

/// Autorisation « Enregistrement de l'écran » (TCC). Pas de clé Info.plist : tout est runtime.
enum Permissions {
    /// Détecteur non bloquant (ne déclenche aucune fenêtre).
    static func hasScreenRecording() -> Bool { CGPreflightScreenCaptureAccess() }

    /// Demande système ponctuelle (n'affiche le dialogue que si statut « indéterminé »).
    @discardableResult
    static func requestScreenRecording() -> Bool { CGRequestScreenCaptureAccess() }

    /// Ouvre directement le volet Confidentialité › Enregistrement de l'écran.
    static func openScreenRecordingSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
            NSWorkspace.shared.open(url)
        }
    }

    /// Relance proprement l'app : seule voie fiable pour rafraîchir l'état TCC « Enregistrement de
    /// l'écran » en cours de session (CGPreflightScreenCaptureAccess est caché par process).
    static func relaunch() {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        p.arguments = ["-n", Bundle.main.bundleURL.path]
        try? p.run()
        NSApp.terminate(nil)
    }
}
