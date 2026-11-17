import SwiftUI
import AppKit
import Carbon.HIToolbox

// MARK: - Raccourcis  (Réglages → Raccourcis : les 4 raccourcis globaux, réglables)

/// Écoute la prochaine combinaison tapée pour UNE action à la fois. Pendant l'écoute, les raccourcis
/// globaux sont suspendus (sinon Carbon avalerait la frappe et lancerait une capture) ; l'écoute
/// s'arrête sur Échap, sur un raccourci valide, ou dès que la fenêtre perd le focus.
@MainActor
final class HotkeyRecorder: ObservableObject {
    struct Message { let text: String; let isError: Bool }

    @Published private(set) var recording: HotkeyAction?
    @Published private(set) var messages: [HotkeyAction: Message] = [:]

    private var monitor: Any?
    private var resignObserver: NSObjectProtocol?

    func toggle(_ action: HotkeyAction) {
        if recording == action { stop() } else { start(action) }
    }

    func start(_ action: HotkeyAction) {
        stop()
        recording = action
        messages[action] = nil
        HotKeyManager.shared.suspend()
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            self?.handle(event)
            return nil                       // la frappe est consommée : rien n'atteint la fenêtre
        }
        resignObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didResignActiveNotification, object: nil, queue: .main
        ) { [weak self] _ in MainActor.assumeIsolated { self?.stop() } }
    }

    func stop() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        if let resignObserver { NotificationCenter.default.removeObserver(resignObserver) }
        monitor = nil
        resignObserver = nil
        recording = nil
        HotKeyManager.shared.resume()
    }

    func resetToDefaults() {
        stop()
        for action in HotkeyAction.allCases { HotkeyStore.shared.set(action.defaultHotkey, for: action) }
        messages = [:]
        HotKeyManager.shared.reload()
    }

    private func handle(_ event: NSEvent) {
        guard let action = recording else { return }
        let i18n = I18n.shared
        let hotkey = Hotkey(keyCode: Int(event.keyCode), modifiers: Self.carbonModifiers(event.modifierFlags))

        if hotkey.keyCode == kVK_Escape && hotkey.modifiers == 0 { stop(); return }
        guard hotkey.isAcceptable else {
            messages[action] = Message(text: i18n.t("hk_need_modifier"), isError: true)
            return
        }
        if let other = HotkeyStore.shared.owner(of: hotkey, except: action) {
            messages[action] = Message(text: String(format: i18n.t("hk_in_use_frameline"), i18n.t(other.titleKey)),
                                       isError: true)
            return
        }
        if hotkey != HotkeyStore.shared.hotkey(action) {
            switch HotKeyManager.shared.availability(hotkey) {
            case .otherApp:
                messages[action] = Message(text: i18n.t("hk_in_use_other"), isError: true)
                return
            case .system:
                // Accepté (⇧⌘4 par défaut l'est aussi) : macOS garde la priorité tant que son
                // raccourci reste coché dans Réglages Système → Clavier → Raccourcis clavier.
                messages[action] = Message(text: i18n.t("hk_in_use_system"), isError: false)
            case .free:
                messages[action] = nil
            }
            HotkeyStore.shared.set(hotkey, for: action)
        }
        stop()
    }

    private static func carbonModifiers(_ flags: NSEvent.ModifierFlags) -> UInt32 {
        var m: UInt32 = 0
        if flags.contains(.command) { m |= UInt32(cmdKey) }
        if flags.contains(.shift) { m |= UInt32(shiftKey) }
        if flags.contains(.option) { m |= UInt32(optionKey) }
        if flags.contains(.control) { m |= UInt32(controlKey) }
        return m
    }
}

struct ShortcutsPane: View {
    @EnvironmentObject private var i18n: I18n
    @ObservedObject private var store = HotkeyStore.shared
    @StateObject private var recorder = HotkeyRecorder()

    var body: some View {
        Form {
            Section {
                ForEach(HotkeyAction.allCases) { action in
                    row(action)
                }
            } header: {
                Text(i18n.t("tab_shortcuts"))
            } footer: {
                Text(i18n.t("hk_help")).font(.footnote).foregroundStyle(.secondary)
            }

            Section {
                Button(i18n.t("hk_reset")) { recorder.resetToDefaults() }
                    .disabled(HotkeyAction.allCases.allSatisfy { store.hotkey($0) == $0.defaultHotkey })
            }
        }
        .formStyle(.grouped)
        .onDisappear { recorder.stop() }
    }

    private func row(_ action: HotkeyAction) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            LabeledContent(i18n.t(action.titleKey)) {
                Button { recorder.toggle(action) } label: {
                    Text(recorder.recording == action ? i18n.t("hk_press")
                                                      : CaptureHotkey.label(for: store.hotkey(action)))
                        .font(.system(size: 13, weight: .medium))
                        .frame(minWidth: 96)
                }
                .buttonStyle(.bordered)
                .tint(recorder.recording == action ? .accentColor : nil)
            }
            if let message = recorder.messages[action] {
                Text(message.text)
                    .font(.caption)
                    .foregroundStyle(message.isError ? Color.red : Color.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}
