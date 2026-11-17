import SwiftUI
import AppKit
import AVFoundation

/// Réglages — calqués EXACTEMENT sur Flow : fenêtre à barre d'outils « préférences » (icône + libellé),
/// contenu en `Form` groupé natif (sections à en-tête + cartes arrondies, accent système bleu), et — comme
/// Flow — les réglages sont RÉPARTIS sur plusieurs onglets courts/aérés plutôt qu'entassés dans un seul
/// panneau qui défile. Chaque onglet tient sans scroll, comme « Général / Moteur / Langues » de Flow.

let appVersion = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0.0"

/// Onglets de la fenêtre Réglages (pilotés par la NSToolbar « préférences »).
/// Les libellés « Capture » / « Enregistrement » réutilisent les clés de section existantes
/// (déjà traduites dans les 10 langues) → aucun trou de localisation.
let ES_SETTINGS_TABS: [(id: String, key: String, symbol: String)] = [
    ("general",   "tab_general",    "gearshape"),
    ("capture",   "sec_capture",    "camera.viewfinder"),
    ("recording", "sec_recording",  "record.circle"),
    ("shortcuts", "tab_shortcuts",  "keyboard"),
    ("about",     "tab_about",      "info.circle"),
]

struct SettingsView: View {
    @AppStorage("settingsTab") private var tab = "general"
    @EnvironmentObject private var i18n: I18n

    // Contenu piloté par la barre d'outils (cf. SettingsToolbarController).
    var body: some View {
        Group {
            switch tab {
            case "capture":   CapturePane()
            case "recording": RecordingPane()
            case "shortcuts": ShortcutsPane()
            case "about":     AboutPane()
            default:          GeneralPane()
            }
        }
        .frame(width: 540, height: 580)
    }
}

// MARK: - Général  (Apparence : langue d'affichage + thème · Démarrage)

private struct GeneralPane: View {
    @EnvironmentObject private var i18n: I18n
    @AppStorage("theme") private var theme = "dark"
    @State private var launchAtLogin = Preferences.shared.launchAtLogin

    var body: some View {
        Form {
            Section(i18n.t("sec_appearance")) {
                // Langue d'affichage en tête, comme Flow (Apparence → Langue d'affichage).
                Picker(i18n.t("display_language"), selection: Binding(
                    get: { i18n.lang },
                    set: { i18n.set($0) }
                )) {
                    Text(i18n.t("system")).tag("system")
                    ForEach(ES_UI_LANGS, id: \.self) { code in
                        Text(ES_LANG_NAMES[code] ?? code).tag(code)
                    }
                }
                Picker(i18n.t("theme"), selection: $theme) {
                    Text(i18n.t("theme_system")).tag("system")
                    Text(i18n.t("theme_light")).tag("light")
                    Text(i18n.t("theme_dark")).tag("dark")
                }
            }

            Section(i18n.t("sec_startup")) {
                Toggle(i18n.t("launch_at_login"), isOn: $launchAtLogin)
            }

            MCPServerSection()
        }
        .formStyle(.grouped)
        .onChange(of: theme) { ThemeManager.apply() }
        .onChange(of: launchAtLogin) {
            Preferences.shared.launchAtLogin = launchAtLogin
            launchAtLogin = Preferences.shared.launchAtLogin       // reflète l'état réel (approbation possible)
        }
    }
}

// MARK: - Serveur MCP (local)

/// Interrupteur, état, copie de la config (avec jeton) et régénération du jeton du serveur MCP local.
private struct MCPServerSection: View {
    @EnvironmentObject private var i18n: I18n
    @ObservedObject private var mcp = MCPController.shared
    @AppStorage("mcpServerEnabled") private var enabled = true

    private var statusText: String {
        switch mcp.status {
        case .stopped: return i18n.t("mcp_status_stopped")
        case .starting: return i18n.t("mcp_status_starting")
        case .running(let port): return String(format: i18n.t("mcp_status_running"), "127.0.0.1:\(port)")
        case .failed(let reason): return String(format: i18n.t("mcp_status_failed"), reason)
        }
    }

    private var statusColor: Color {
        switch mcp.status {
        case .running: return .green
        case .failed: return .red
        default: return .secondary
        }
    }

    private var isRunning: Bool { if case .running = mcp.status { return true }; return false }

    var body: some View {
        Section {
            Toggle(i18n.t("mcp_enable"), isOn: $enabled)
                .onChange(of: enabled) { MCPController.shared.setEnabled(enabled) }
            LabeledContent(i18n.t("mcp_status")) {
                HStack(spacing: 6) {
                    Circle().fill(statusColor).frame(width: 7, height: 7)
                    Text(statusText).foregroundStyle(.secondary).lineLimit(2)
                }
            }
            HStack {
                Button(i18n.t("mcp_copy_config")) { MCPController.shared.copyClientConfig() }
                    .disabled(!isRunning)
                Button(i18n.t("mcp_regenerate")) { MCPController.shared.regenerateToken() }
                    .disabled(!isRunning)
            }
        } header: {
            Text(i18n.t("sec_mcp"))
        } footer: {
            Text(i18n.t("mcp_footer")).font(.footnote).foregroundStyle(.secondary)
        }
    }
}

/// Libellé d'un son dans les menus : "default"/"macScreenshot" → clés i18n dédiées ; sons système à FICHIER
/// → clé "snd_<id>" (fr/en, repli en pour les autres langues) ; sons d'alerte (Glass, Funk…) → leur nom
/// littéral (pas de clé → on affiche l'id, comme les noms de sons macOS universels).
@MainActor private func soundMenuLabel(_ id: String, _ i18n: I18n) -> String {
    switch id {
    case "default":       return i18n.t("sound_default")
    case "macScreenshot": return i18n.t("sound_mac")
    default:
        let key = "snd_" + id
        let t = i18n.t(key)
        return t == key ? id : t
    }
}

// MARK: - Capture  (Après capture · Format d'image)

private struct CapturePane: View {
    @EnvironmentObject private var i18n: I18n
    @AppStorage("showPreview") private var showPreview = true
    @AppStorage("copyToClipboard") private var copyToClipboard = false
    @AppStorage("openEditor") private var openEditor = false
    @AppStorage("playSound") private var playSound = true
    @AppStorage("imageFormat") private var imageFormat = "png"
    @AppStorage("previewFollowsCursor") private var previewFollowsCursor = true
    @AppStorage("previewCorner") private var previewCorner = "bottomLeft"
    @AppStorage("closePreviewAfterDrag") private var closePreviewAfterDrag = false
    @AppStorage("captureSoundName") private var captureSound = "default"

    var body: some View {
        Form {
            Section(i18n.t("sec_after_capture")) {
                Toggle(i18n.t("show_preview"), isOn: $showPreview)
                Picker(i18n.t("preview_corner"), selection: $previewCorner) {
                    Text(i18n.t("corner_bottom_left")).tag("bottomLeft")
                    Text(i18n.t("corner_top_left")).tag("topLeft")
                    Text(i18n.t("corner_bottom_right")).tag("bottomRight")
                    Text(i18n.t("corner_top_right")).tag("topRight")
                }
                .disabled(!showPreview)
                .onChange(of: previewCorner) {
                    CaptureThumbnailController.shared.previewCornerChanged()
                }
                Toggle(i18n.t("preview_follow_cursor"), isOn: $previewFollowsCursor)
                    .disabled(!showPreview)
                    .onChange(of: previewFollowsCursor) {
                        CaptureThumbnailController.shared.followingPreferenceChanged()
                    }
                Toggle(i18n.t("close_preview_after_drag"), isOn: $closePreviewAfterDrag)
                    .disabled(!showPreview)
                Toggle(i18n.t("copy_clipboard"), isOn: $copyToClipboard)
                Toggle(i18n.t("open_editor"), isOn: $openEditor)
                Toggle(i18n.t("play_sound"), isOn: $playSound)
                Picker(i18n.t("sound_capture"), selection: $captureSound) {
                    ForEach(CaptureSound.captureOrder, id: \.self) { id in
                        Text(soundMenuLabel(id, i18n)).tag(id)
                    }
                }
                .disabled(!playSound)
                .onChange(of: captureSound) { CaptureSound.previewCapture(captureSound) }   // aperçu : on l'entend
            }

            Section(i18n.t("sec_capture")) {
                Picker(i18n.t("image_format"), selection: $imageFormat) {
                    Text("PNG").tag("png")
                    Text("JPEG").tag("jpg")
                }
            }
        }
        .formStyle(.grouped)
    }
}

// MARK: - Enregistrement

private struct RecordingPane: View {
    @EnvironmentObject private var i18n: I18n
    @AppStorage("recordSystemAudio") private var recordSystemAudio = true
    @AppStorage("recordMicrophone") private var recordMicrophone = false
    @AppStorage("playSound") private var playSound = true
    @AppStorage("recordSoundName") private var recordSound = "default"

    var body: some View {
        Form {
            Section(i18n.t("sec_recording")) {
                Toggle(i18n.t("record_system_audio"), isOn: $recordSystemAudio)
                Toggle(i18n.t("record_microphone"), isOn: $recordMicrophone)
                    .onChange(of: recordMicrophone) { _, on in
                        // Demande l'autorisation micro dès l'activation (sinon : pas de piste micro).
                        if on, AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined {
                            AVCaptureDevice.requestAccess(for: .audio) { _ in }
                        }
                    }
                // Son joué AU DÉMARRAGE de l'enregistrement — liste DISJOINTE des sons de capture.
                Picker(i18n.t("sound_recording_start"), selection: $recordSound) {
                    ForEach(CaptureSound.recordingOrder, id: \.self) { id in
                        Text(soundMenuLabel(id, i18n)).tag(id)
                    }
                }
                .disabled(!playSound)
                .onChange(of: recordSound) { CaptureSound.previewRecording(recordSound) }   // aperçu : on l'entend
            }
        }
        .formStyle(.grouped)
    }
}

// MARK: - À propos  (même disposition que Flow : logo + version + tagline + bouton MAJ)

private struct AboutPane: View {
    @EnvironmentObject private var i18n: I18n

    var body: some View {
        VStack(spacing: 9) {
            Image(nsImage: NSApp.applicationIconImage)
                .resizable().interpolation(.high)
                .frame(width: 64, height: 64)
                .padding(.top, 14)
            Text("FrameLine").font(.title2).bold()
            Text("\(i18n.t("version")) \(appVersion)").foregroundStyle(.secondary)
            Text(i18n.t("about_tagline"))
                .font(.footnote)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            Button(i18n.t("check_updates")) { esShowUpToDate() }
                .controlSize(.small)
                .padding(.top, 8)
            Spacer()
            Text("© 2026 SAAS Empire")
                .font(.caption)
                .foregroundStyle(.tertiary)
                .padding(.bottom, 12)
        }
        .frame(maxWidth: .infinity)
        .padding(.horizontal)
    }
}

// MARK: - « Check for Updates » (placeholder honnête, localisé)

@MainActor func esShowUpToDate() {
    NSApp.activate(ignoringOtherApps: true)
    let a = NSAlert()
    a.messageText = I18n.shared.t("uptodate_title")
    a.informativeText = String(format: I18n.shared.t("uptodate_msg"), appVersion)
    a.addButton(withTitle: I18n.shared.t("ok"))
    a.runModal()
}

// MARK: - Barre d'outils « préférences » (look Flow / CodexBar)

/// Un item sélectionnable par onglet (icône + libellé). Clic → écrit l'onglet courant
/// (UserDefaults "settingsTab"), que SettingsView observe via @AppStorage.
@MainActor
final class SettingsToolbarController: NSObject, NSToolbarDelegate {
    private var ids: [NSToolbarItem.Identifier] { ES_SETTINGS_TABS.map { .init($0.id) } }
    func toolbarDefaultItemIdentifiers(_ t: NSToolbar) -> [NSToolbarItem.Identifier] { ids }
    func toolbarAllowedItemIdentifiers(_ t: NSToolbar) -> [NSToolbarItem.Identifier] { ids }
    func toolbarSelectableItemIdentifiers(_ t: NSToolbar) -> [NSToolbarItem.Identifier] { ids }
    func toolbar(_ t: NSToolbar, itemForItemIdentifier id: NSToolbarItem.Identifier,
                 willBeInsertedIntoToolbar flag: Bool) -> NSToolbarItem? {
        guard let entry = ES_SETTINGS_TABS.first(where: { $0.id == id.rawValue }) else { return nil }
        let item = NSToolbarItem(itemIdentifier: id)
        item.label = I18n.shared.t(entry.key)
        item.image = NSImage(systemSymbolName: entry.symbol, accessibilityDescription: item.label)
        item.target = self
        item.action = #selector(didSelect(_:))
        return item
    }
    @objc func didSelect(_ sender: NSToolbarItem) {
        UserDefaults.standard.set(sender.itemIdentifier.rawValue, forKey: "settingsTab")
    }
}
