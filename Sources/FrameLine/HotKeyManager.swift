import AppKit
import Carbon.HIToolbox

/// Raccourcis globaux système via Carbon RegisterEventHotKey.
/// Aucun droit ni permission Accessibilité requis (contrairement à CGEventTap).
/// Les touches viennent de `HotkeyStore` (réglables dans Réglages → Raccourcis) et se
/// ré-enregistrent à chaud, sans relancer l'app.
final class HotKeyManager {
    static let shared = HotKeyManager()

    private var handlers: [UInt32: () -> Void] = [:]
    private var refs: [HotkeyAction: EventHotKeyRef] = [:]
    private var handlerInstalled = false
    private var suspended = false

    /// Enregistre les 4 raccourcis courants (défauts BridgeShot ⇧⌘4/8/9/E tant qu'ils n'ont pas été changés).
    func register(region: @escaping () -> Void,
                  window: @escaping () -> Void,
                  full: @escaping () -> Void,
                  record: @escaping () -> Void) {
        installHandlerIfNeeded()
        handlers[HotkeyAction.region.carbonID] = region
        handlers[HotkeyAction.window.carbonID] = window
        handlers[HotkeyAction.fullScreen.carbonID] = full
        handlers[HotkeyAction.record.carbonID] = record
        for action in HotkeyAction.allCases {
            _ = register(action, HotkeyStore.shared.hotkey(action))
        }
    }

    enum Availability { case free, system, otherApp }

    /// Le raccourci est-il libre ? `system` = raccourci macOS actif (Réglages Système → Clavier →
    /// Raccourcis : captures d'écran, Spotlight…), `otherApp` = une autre app l'a déjà enregistré.
    /// Essai à blanc : rien ne reste enregistré.
    func availability(_ hotkey: Hotkey) -> Availability {
        if Self.isSystemShortcut(hotkey) { return .system }
        var ref: EventHotKeyRef?
        let hkID = EventHotKeyID(signature: OSType(0x45535354 /* 'ESST' */), id: 99)
        let status = RegisterEventHotKey(UInt32(hotkey.keyCode), hotkey.modifiers, hkID,
                                         GetApplicationEventTarget(), 0, &ref)
        if let ref { UnregisterEventHotKey(ref) }
        return status == noErr ? .free : .otherApp
    }

    private static func isSystemShortcut(_ hotkey: Hotkey) -> Bool {
        var unmanaged: Unmanaged<CFArray>?
        guard CopySymbolicHotKeys(&unmanaged) == noErr,
              let list = unmanaged?.takeRetainedValue() as? [[String: Any]] else { return false }
        return list.contains { entry in
            guard (entry[kHISymbolicHotKeyEnabled as String] as? Bool) == true,
                  let code = entry[kHISymbolicHotKeyCode as String] as? Int,
                  let mods = entry[kHISymbolicHotKeyModifiers as String] as? Int else { return false }
            return code == hotkey.keyCode && UInt32(mods) & Hotkey.allModifiers == hotkey.modifiers
        }
    }

    /// Ré-enregistre les 4 raccourcis depuis `HotkeyStore` (après un changement dans les Réglages).
    func reload() {
        for action in HotkeyAction.allCases { unregister(action) }
        guard !suspended else { return }
        for action in HotkeyAction.allCases { _ = register(action, HotkeyStore.shared.hotkey(action)) }
    }

    /// Libère toutes les touches le temps qu'un champ des Réglages en écoute une nouvelle :
    /// sinon Carbon avalerait la frappe (et déclencherait une capture) avant la fenêtre.
    func suspend() {
        guard !suspended else { return }
        suspended = true
        for action in HotkeyAction.allCases { unregister(action) }
    }

    func resume() {
        guard suspended else { return }
        suspended = false
        reload()
    }

    @discardableResult
    private func register(_ action: HotkeyAction, _ hotkey: Hotkey) -> Bool {
        var ref: EventHotKeyRef?
        let hkID = EventHotKeyID(signature: OSType(0x45535343 /* 'ESSC' */), id: action.carbonID)
        let status = RegisterEventHotKey(UInt32(hotkey.keyCode), hotkey.modifiers, hkID,
                                         GetApplicationEventTarget(), 0, &ref)
        if status == noErr, let ref {
            refs[action] = ref
            ESLog.log("RegisterEventHotKey \(action.rawValue) keyCode=\(hotkey.keyCode) mods=\(hotkey.modifiers) → OK")
            return true
        }
        ESLog.log("RegisterEventHotKey \(action.rawValue) keyCode=\(hotkey.keyCode) → ÉCHEC status=\(status)")
        NSLog("FrameLine — RegisterEventHotKey \(action.rawValue) a échoué (status=\(status))")
        return false
    }

    private func unregister(_ action: HotkeyAction) {
        if let ref = refs.removeValue(forKey: action) { UnregisterEventHotKey(ref) }
    }

    private func installHandlerIfNeeded() {
        guard !handlerInstalled else { return }
        handlerInstalled = true
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard),
                                 eventKind: UInt32(kEventHotKeyPressed))
        let this = Unmanaged.passUnretained(self).toOpaque()
        InstallEventHandler(GetApplicationEventTarget(), { (_, event, userData) -> OSStatus in
            guard let event, let userData else { return OSStatus(eventNotHandledErr) }
            var hkID = EventHotKeyID()
            GetEventParameter(event,
                              EventParamName(kEventParamDirectObject),
                              EventParamType(typeEventHotKeyID),
                              nil, MemoryLayout<EventHotKeyID>.size, nil, &hkID)
            let mgr = Unmanaged<HotKeyManager>.fromOpaque(userData).takeUnretainedValue()
            ESLog.log("HotKey PRESSED id=\(hkID.id)")
            if let action = mgr.handlers[hkID.id] {
                DispatchQueue.main.async { action() }    // Carbon livre sur le main, on reste explicite
            }
            return noErr
        }, 1, &spec, this, nil)
    }
}
