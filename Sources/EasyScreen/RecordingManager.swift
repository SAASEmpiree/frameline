import AppKit

extension Notification.Name {
    static let recordingStateChanged = Notification.Name("EasyScreen.recordingStateChanged")
}

/// Enregistrement vidéo de l'écran via `screencapture -v`. Arrêt = SIGINT (interrupt),
/// que screencapture intercepte pour finaliser proprement le .mov.
final class RecordingManager {
    static let shared = RecordingManager()
    private let tool = URL(fileURLWithPath: "/usr/sbin/screencapture")
    private var process: Process?
    private(set) var currentURL: URL?

    var isRecording: Bool { process != nil }

    func toggleFullScreen() { isRecording ? stop() : startFullScreen() }

    func startFullScreen() {
        guard !isRecording, CaptureManager.shared.ensureScreenRecordingPermission() else { return }
        let url = Preferences.shared.newCaptureURL(ext: "mov")
        launch(["-v", "-k", "-D", "1", url.path], url: url)       // vidéo, montre les clics, écran 1
    }

    func startRegion() {
        guard !isRecording, CaptureManager.shared.ensureScreenRecordingPermission() else { return }
        let url = Preferences.shared.newCaptureURL(ext: "mov")
        launch(["-v", "-k", "-i", url.path], url: url)            // sélection interactive de la zone
    }

    private func launch(_ args: [String], url: URL) {
        let p = Process()
        p.executableURL = tool
        p.arguments = args
        p.terminationHandler = { [weak self] _ in
            DispatchQueue.main.async {
                self?.process = nil
                self?.currentURL = nil
                let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int ?? 0) ?? 0
                if FileManager.default.fileExists(atPath: url.path), size > 0 {
                    Notifier.notifyCapture(url: url, isVideo: true)
                    MainActor.assumeIsolated {
                        CaptureLibrary.shared.add(url)            // l'enregistrement atterrit dans la galerie + sélection
                        MainWindowController.shared.show()
                    }
                }
                NotificationCenter.default.post(name: .recordingStateChanged, object: nil)
            }
        }
        do {
            try p.run()
            process = p
            currentURL = url
            NotificationCenter.default.post(name: .recordingStateChanged, object: nil)
        } catch {
            NSLog("Easy Screen — recording error: \(error.localizedDescription)")
            process = nil
        }
    }

    func stop() {
        process?.interrupt()                                      // SIGINT → screencapture finalise le fichier
    }
}
