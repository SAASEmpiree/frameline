import Foundation
import Combine

/// Langue d'affichage (i18n maison, sans .lproj) — calqué sur Flow : changement à chaud,
/// sans redémarrage. L'UI des Réglages ET le menu de la barre des menus suivent ce réglage.

/// Langues proposées comme « langue d'affichage ». "en" est la source de vérité.
let ES_UI_LANGS: [String] = ["en", "fr", "es", "de", "it", "pt-BR", "nl", "ru", "ja", "zh-Hans"]

/// Nom natif de chaque langue (façon DeepL : chaque langue affichée dans sa propre langue).
let ES_LANG_NAMES: [String: String] = [
    "en": "English", "fr": "Français", "es": "Español", "de": "Deutsch",
    "it": "Italiano", "pt-BR": "Português (Brasil)", "nl": "Nederlands",
    "ru": "Русский", "ja": "日本語", "zh-Hans": "简体中文",
]

extension Notification.Name {
    static let displayLanguageChanged = Notification.Name("FrameLine.displayLanguageChanged")
}

@MainActor
final class I18n: ObservableObject {
    static let shared = I18n()
    @Published var lang: String

    init() { lang = UserDefaults.standard.string(forKey: "displayLang") ?? "system" }

    /// Ordre des langues RÉELLEMENT préféré au niveau système (domaine global), indépendant des
    /// localisations déclarées par l'app. `Locale.preferredLanguages` est filtré par les .lproj de
    /// l'app (ici anglais seul) → renverrait "en" sur un Mac français. On lit donc AppleLanguages
    /// dans le domaine global via CFPreferences (repli : Locale.preferredLanguages).
    private func systemLanguages() -> [String] {
        if let v = CFPreferencesCopyValue("AppleLanguages" as CFString, kCFPreferencesAnyApplication,
                                          kCFPreferencesCurrentUser, kCFPreferencesAnyHost) as? [String],
           !v.isEmpty {
            return v
        }
        return Locale.preferredLanguages
    }

    /// Langue effective : "system" → 1re préférence du système couverte, sinon "en".
    var effLang: String {
        if lang != "system" { return lang }
        for pref in systemLanguages() {
            let l = Locale.Language(identifier: pref)
            let code = l.languageCode?.identifier ?? ""
            let script = l.script?.identifier
            if let script,
               let m = ES_UI_LANGS.first(where: {
                   let c = Locale.Language(identifier: $0)
                   return c.languageCode?.identifier == code && c.script?.identifier == script
               }) { return m }
            if let m = ES_UI_LANGS.first(where: { $0 == code || $0.hasPrefix(code + "-") }) { return m }
        }
        return "en"
    }

    /// Chaîne traduite (repli : langue d'affichage → anglais → clé brute).
    func t(_ key: String) -> String {
        ES_STRINGS[effLang]?[key] ?? ES_STRINGS["en"]?[key] ?? key
    }

    func set(_ code: String) {
        guard code != lang else { return }
        lang = code
        UserDefaults.standard.set(code, forKey: "displayLang")
        NotificationCenter.default.post(name: .displayLanguageChanged, object: nil)
    }
}

/// Raccourci hors SwiftUI (menu AppKit, alertes) : `L("key")`.
@MainActor func L(_ key: String) -> String { I18n.shared.t(key) }
