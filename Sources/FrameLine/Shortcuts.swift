import Foundation
import Combine
import Carbon.HIToolbox

/// Un raccourci global : touche PHYSIQUE (keyCode) + modificateurs au format Carbon
/// (`cmdKey | shiftKey | optionKey | controlKey`), exactement ce qu'attend RegisterEventHotKey.
struct Hotkey: Equatable {
    var keyCode: Int
    var modifiers: UInt32

    static let allModifiers = UInt32(cmdKey | shiftKey | optionKey | controlKey)

    /// Forme persistée dans UserDefaults : « keyCode:modifiers ».
    var storage: String { "\(keyCode):\(modifiers)" }

    init(keyCode: Int, modifiers: UInt32) {
        self.keyCode = keyCode
        self.modifiers = modifiers & Hotkey.allModifiers
    }

    init?(storage: String) {
        let parts = storage.split(separator: ":")
        guard parts.count == 2, let k = Int(parts[0]), let m = UInt32(parts[1]),
              (0...0x7F).contains(k), m & ~Hotkey.allModifiers == 0 else { return nil }
        self.init(keyCode: k, modifiers: m)
    }

    /// Les touches F se suffisent à elles-mêmes ; tout le reste exige ⌘, ⌃ ou ⌥ (⇧ seul taperait
    /// une majuscule partout dans le système).
    var isAcceptable: Bool {
        if Hotkey.functionKeys[keyCode] != nil { return true }
        return modifiers & UInt32(cmdKey | optionKey | controlKey) != 0
    }

    static let functionKeys: [Int: String] = [
        kVK_F1: "F1", kVK_F2: "F2", kVK_F3: "F3", kVK_F4: "F4", kVK_F5: "F5", kVK_F6: "F6",
        kVK_F7: "F7", kVK_F8: "F8", kVK_F9: "F9", kVK_F10: "F10", kVK_F11: "F11", kVK_F12: "F12",
        kVK_F13: "F13", kVK_F14: "F14", kVK_F15: "F15", kVK_F16: "F16", kVK_F17: "F17",
        kVK_F18: "F18", kVK_F19: "F19", kVK_F20: "F20",
    ]

    /// Touches sans caractère imprimable : nommées par leur glyphe macOS.
    static let namedKeys: [Int: String] = [
        kVK_Space: "Space", kVK_Return: "↩", kVK_Tab: "⇥", kVK_Delete: "⌫", kVK_ForwardDelete: "⌦",
        kVK_LeftArrow: "←", kVK_RightArrow: "→", kVK_UpArrow: "↑", kVK_DownArrow: "↓",
        kVK_Home: "↖", kVK_End: "↘", kVK_PageUp: "⇞", kVK_PageDown: "⇟", kVK_ANSI_KeypadEnter: "⌤",
    ]
}

/// Les quatre actions qui ont un raccourci global.
enum HotkeyAction: String, CaseIterable, Identifiable {
    case region, window, fullScreen, record

    var id: String { rawValue }

    /// Défauts alignés sur BridgeShot : ⇧⌘ + touche physique 4 / 8 / 9 / E
    /// (« ' », « ! », « ç » et « E » en AZERTY).
    var defaultHotkey: Hotkey {
        let mods = UInt32(cmdKey | shiftKey)
        switch self {
        case .region:     return Hotkey(keyCode: kVK_ANSI_4, modifiers: mods)
        case .window:     return Hotkey(keyCode: kVK_ANSI_8, modifiers: mods)
        case .fullScreen: return Hotkey(keyCode: kVK_ANSI_9, modifiers: mods)
        case .record:     return Hotkey(keyCode: kVK_ANSI_E, modifiers: mods)
        }
    }

    /// Identifiant Carbon (EventHotKeyID.id) : fixe, il ne dépend pas de la touche choisie.
    var carbonID: UInt32 {
        switch self {
        case .region: return 1
        case .window: return 2
        case .fullScreen: return 3
        case .record: return 4
        }
    }

    var preferenceKey: String { "hotkey." + rawValue }

    /// Libellé dans les Réglages (clés du menu de la barre des menus, déjà traduites).
    var titleKey: String {
        switch self {
        case .region: return "m_capture_area"
        case .window: return "m_capture_window"
        case .fullScreen: return "m_capture_fullscreen"
        case .record: return "hk_record"
        }
    }
}

/// Raccourcis courants, persistés dans UserDefaults. Publié : les vues SwiftUI qui affichent un
/// raccourci l'observent et se redessinent dès qu'il change dans les Réglages.
final class HotkeyStore: ObservableObject {
    static let shared = HotkeyStore()

    @Published private(set) var current: [HotkeyAction: Hotkey] = [:]

    private init() {
        for action in HotkeyAction.allCases {
            let saved = UserDefaults.standard.string(forKey: action.preferenceKey).flatMap(Hotkey.init(storage:))
            current[action] = (saved?.isAcceptable == true) ? saved : action.defaultHotkey
        }
    }

    func hotkey(_ action: HotkeyAction) -> Hotkey { current[action] ?? action.defaultHotkey }

    func set(_ hotkey: Hotkey, for action: HotkeyAction) {
        current[action] = hotkey
        if hotkey == action.defaultHotkey {
            UserDefaults.standard.removeObject(forKey: action.preferenceKey)
        } else {
            UserDefaults.standard.set(hotkey.storage, forKey: action.preferenceKey)
        }
        NotificationCenter.default.post(name: .hotkeysChanged, object: nil)
    }

    /// L'action FrameLine qui utilise déjà ce raccourci (hors `action`), s'il y en a une.
    func owner(of hotkey: Hotkey, except action: HotkeyAction) -> HotkeyAction? {
        HotkeyAction.allCases.first { $0 != action && self.hotkey($0) == hotkey }
    }
}

extension Notification.Name {
    static let hotkeysChanged = Notification.Name("FrameLine.hotkeysChanged")
}

/// Source UNIQUE des raccourcis de capture + rendu du libellé selon la disposition clavier
/// ACTIVE — exactement comme BridgeShot : ⇧⌘[touche physique 4] s'affiche « ' » en AZERTY,
/// « 4 » en QWERTY.
enum CaptureHotkey {
    static var region: Hotkey     { HotkeyStore.shared.hotkey(.region) }
    static var window: Hotkey     { HotkeyStore.shared.hotkey(.window) }
    static var fullScreen: Hotkey { HotkeyStore.shared.hotkey(.fullScreen) }
    static var record: Hotkey     { HotkeyStore.shared.hotkey(.record) }

    /// Caractère imprimé sur la touche physique pour la disposition courante (sans modificateur).
    static func character(for keyCode: Int) -> String {
        guard let source = TISCopyCurrentKeyboardLayoutInputSource()?.takeRetainedValue(),
              let layoutPtr = TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData)
        else { return "" }
        let layoutData = unsafeBitCast(layoutPtr, to: CFData.self)
        let keyLayout = unsafeBitCast(CFDataGetBytePtr(layoutData), to: UnsafePointer<UCKeyboardLayout>.self)

        var deadKeyState: UInt32 = 0
        var length = 0
        var chars = [UniChar](repeating: 0, count: 4)
        let status = UCKeyTranslate(keyLayout,
                                    UInt16(keyCode),
                                    UInt16(kUCKeyActionDisplay),
                                    0,                                   // pas de modificateur → caractère de base
                                    UInt32(LMGetKbdType()),
                                    OptionBits(kUCKeyTranslateNoDeadKeysMask),   // le MASQUE (1 << bit), pas le n° de bit (= 0, aucune option)
                                    &deadKeyState,
                                    chars.count, &length, &chars)
        guard status == noErr, length > 0 else { return "" }
        return String(utf16CodeUnits: chars, count: length)
    }

    /// Glyphe affiché : une lettre ASCII (a–z) se montre en MAJUSCULE (convention macOS, ex.
    /// ⇧⌘E) ; la ponctuation et les lettres accentuées gardent leur glyphe de base (⇧⌘' / ⇧⌘! / ⇧⌘ç,
    /// comme BridgeShot — on NE veut PAS ç → Ç). F-keys, flèches, espace… par leur nom.
    static func display(for keyCode: Int) -> String {
        if let name = Hotkey.functionKeys[keyCode] ?? Hotkey.namedKeys[keyCode] { return name }
        let c = character(for: keyCode)
        guard let ch = c.first, c.count == 1, ch.isASCII, ch.isLetter else { return c }
        return c.uppercased()
    }

    /// Glyphes des modificateurs dans l'ordre macOS canonique : ⌃ ⌥ ⇧ ⌘.
    static func modifierGlyphs(_ mods: UInt32) -> [String] {
        var out: [String] = []
        if mods & UInt32(controlKey) != 0 { out.append("⌃") }
        if mods & UInt32(optionKey) != 0 { out.append("⌥") }
        if mods & UInt32(shiftKey) != 0 { out.append("⇧") }
        if mods & UInt32(cmdKey) != 0 { out.append("⌘") }
        return out
    }

    /// Libellé compact « ⇧⌘' ».
    static func label(for hotkey: Hotkey) -> String { keys(for: hotkey).joined() }

    /// Composants ["⇧", "⌘", "'"] pour les pastilles d'action.
    static func keys(for hotkey: Hotkey) -> [String] {
        modifierGlyphs(hotkey.modifiers) + [display(for: hotkey.keyCode)]
    }
}
