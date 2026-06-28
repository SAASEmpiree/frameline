import AppKit
import UserNotifications

/// Notifications « capture enregistrée ». Inactif si l'exécutable n'est pas dans un vrai bundle.
enum Notifier {
    private static var available: Bool { Bundle.main.bundleIdentifier != nil }

    static func requestAuth() {
        guard available else { return }
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { _, _ in }
    }

    static func notifyCapture(url: URL, isVideo: Bool) {
        guard available else { return }
        let content = UNMutableNotificationContent()
        content.title = isVideo ? "Enregistrement terminé" : "Capture enregistrée"
        content.body = url.lastPathComponent
        let req = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(req, withCompletionHandler: nil)
    }
}
