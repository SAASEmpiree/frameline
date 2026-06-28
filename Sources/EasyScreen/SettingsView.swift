import SwiftUI

/// Réglages — même structure que la référence : Apparence (Thème), Après capture (bascules), Démarrage.
struct SettingsView: View {
    @AppStorage("theme") private var theme = "dark"
    @AppStorage("showPreview") private var showPreview = true
    @AppStorage("copyToClipboard") private var copyToClipboard = false
    @AppStorage("openEditor") private var openEditor = false
    @AppStorage("playSound") private var playSound = true
    @State private var launchAtLogin = Preferences.shared.launchAtLogin

    let onClose: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Spacer()
                Button(action: onClose) {
                    Text("»").font(.system(size: 17, weight: .semibold))
                }
                .buttonStyle(.plain).foregroundStyle(.secondary)
            }
            .padding(.horizontal, 18).padding(.top, 12)

            ScrollView {
                VStack(alignment: .leading, spacing: 26) {
                    section("Apparence") {
                        card {
                            HStack {
                                Text("Thème").font(.system(size: 13))
                                Spacer()
                                Picker("", selection: $theme) {
                                    Text("Système").tag("system")
                                    Text("Clair").tag("light")
                                    Text("Sombre").tag("dark")
                                }
                                .pickerStyle(.segmented).labelsHidden().frame(width: 260)
                            }
                            .padding(.horizontal, 16).padding(.vertical, 11)
                        }
                    }

                    section("Après capture") {
                        card {
                            VStack(spacing: 0) {
                                toggleRow("Afficher l'aperçu rapide après capture", $showPreview)
                                rowDivider
                                toggleRow("Copier dans le presse-papiers après capture", $copyToClipboard)
                                rowDivider
                                toggleRow("Ouvrir l'éditeur après capture", $openEditor)
                                rowDivider
                                toggleRow("Jouer le son de capture", $playSound)
                            }
                        }
                    }

                    section("Démarrage") {
                        card { toggleRow("Lancer au démarrage", $launchAtLogin) }
                    }
                }
                .padding(20)
            }
        }
        .frame(width: 560, height: 540)
        .onChange(of: theme) { ThemeManager.apply() }
        .onChange(of: launchAtLogin) {
            Preferences.shared.launchAtLogin = launchAtLogin
            launchAtLogin = Preferences.shared.launchAtLogin       // reflète l'état réel (approbation possible)
        }
    }

    // MARK: - Briques

    @ViewBuilder private func section<C: View>(_ title: String, @ViewBuilder _ content: () -> C) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title).font(.system(size: 15, weight: .bold))
            content()
        }
    }

    @ViewBuilder private func card<C: View>(@ViewBuilder _ content: () -> C) -> some View {
        content().background(RoundedRectangle(cornerRadius: 10).fill(Color.primary.opacity(0.06)))
    }

    private var rowDivider: some View { Divider().padding(.leading, 16) }

    private func toggleRow(_ title: String, _ binding: Binding<Bool>) -> some View {
        HStack {
            Text(title).font(.system(size: 13))
            Spacer()
            Toggle("", isOn: binding).labelsHidden().toggleStyle(.switch).controlSize(.small)
        }
        .padding(.horizontal, 16).padding(.vertical, 11)
    }
}
