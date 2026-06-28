import AppKit
import Carbon.HIToolbox

/// Raccourcis globaux système via Carbon RegisterEventHotKey.
/// Aucun droit ni permission Accessibilité requis (contrairement à CGEventTap).
final class HotKeyManager {
    static let shared = HotKeyManager()

    private var handlers: [UInt32: () -> Void] = [:]
    private var refs: [EventHotKeyRef?] = []
    private var handlerInstalled = false

    /// Enregistre les 4 raccourcis par défaut, alignés sur BridgeShot.
    /// keyCodes physiques (cf. `CaptureHotkey`) : zone=4 (⇧⌘'), fenêtre=8 (⇧⌘!),
    /// plein écran=9 (⇧⌘ç), enregistrement=0 (⇧⌘à).
    func registerDefaults(region: @escaping () -> Void,
                          window: @escaping () -> Void,
                          full: @escaping () -> Void,
                          record: @escaping () -> Void) {
        installHandlerIfNeeded()
        let mods = UInt32(cmdKey | shiftKey)
        register(id: 1, keyCode: UInt32(CaptureHotkey.region),     mods: mods, action: region)
        register(id: 2, keyCode: UInt32(CaptureHotkey.window),     mods: mods, action: window)
        register(id: 3, keyCode: UInt32(CaptureHotkey.fullScreen), mods: mods, action: full)
        register(id: 4, keyCode: UInt32(CaptureHotkey.record),     mods: mods, action: record)
    }

    private func register(id: UInt32, keyCode: UInt32, mods: UInt32, action: @escaping () -> Void) {
        handlers[id] = action
        var ref: EventHotKeyRef?
        let hkID = EventHotKeyID(signature: OSType(0x45535343 /* 'ESSC' */), id: id)
        let status = RegisterEventHotKey(keyCode, mods, hkID, GetApplicationEventTarget(), 0, &ref)
        if status == noErr {
            refs.append(ref)
            ESLog.log("RegisterEventHotKey id=\(id) keyCode=\(keyCode) → OK")
        } else {
            ESLog.log("RegisterEventHotKey id=\(id) keyCode=\(keyCode) → ÉCHEC status=\(status)")
            NSLog("Easy Screen — RegisterEventHotKey id=\(id) a échoué (status=\(status))")
        }
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
