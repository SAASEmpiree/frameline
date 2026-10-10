import Foundation
import Carbon.HIToolbox

/// Source UNIQUE des raccourcis de capture (keyCode physique) + rendu du libellé
/// selon la disposition clavier ACTIVE — exactement comme BridgeShot :
/// ⇧⌘[touche physique 4] s'affiche « ' » en AZERTY, « 4 » en QWERTY.
///
/// Réglés pour correspondre aux défauts de BridgeShot :
///   Capture Area = ⇧⌘' (touche 4) · Window = ⇧⌘! (touche 8) · Fullscreen = ⇧⌘ç (touche 9).
enum CaptureHotkey {
    static let region     = kVK_ANSI_4   // « Capture Area » BridgeShot (⇧⌘' en AZERTY)
    static let window     = kVK_ANSI_8   // « Window »       BridgeShot (⇧⌘! en AZERTY)
    static let fullScreen = kVK_ANSI_9   // « Fullscreen »   BridgeShot (⇧⌘ç en AZERTY)
    static let record     = kVK_ANSI_E   // Enregistrement — ⇧⌘E

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

    /// Glyphe affiché : une lettre ASCII (a–z) sous ⇧ se montre en MAJUSCULE (convention macOS, ex.
    /// ⇧⌘E) ; la ponctuation et les lettres accentuées gardent leur glyphe de base (⇧⌘' / ⇧⌘! / ⇧⌘ç,
    /// comme BridgeShot — on NE veut PAS ç → Ç).
    static func display(for keyCode: Int) -> String {
        let c = character(for: keyCode)
        guard let ch = c.first, c.count == 1, ch.isASCII, ch.isLetter else { return c }
        return c.uppercased()
    }

    /// Libellé compact « ⇧⌘' » (ordre macOS canonique : Maj avant Cmd, comme BridgeShot).
    static func label(for keyCode: Int) -> String { "⇧⌘" + display(for: keyCode) }

    /// Composants ["⇧", "⌘", "'"] pour les pastilles d'action.
    static func keys(for keyCode: Int) -> [String] { ["⇧", "⌘", display(for: keyCode)] }
}
