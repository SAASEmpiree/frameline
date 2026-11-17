import SwiftUI
import AppKit

/// Guide de configuration de premier lancement — calqué PIXEL POUR PIXEL sur le « Setup Guide » de
/// BridgeShot : 1) Bienvenue · 2) Autoriser l'enregistrement d'écran · 3) Raccourcis · 4) Tout est prêt.
/// Libellés anglais, chrome identique (logo réel + « Skip Setup », carte centrée, « ‹ Back / dots / Continue ›»).
struct OnboardingView: View {
    let onFinish: () -> Void          // « Skip Setup » / « Open Library » / fermeture → ouvre la bibliothèque
    let onStartCapture: () -> Void    // « Take Your First Screenshot » → ferme + lance une capture zone

    @State private var step = 0
    @ObservedObject private var hotkeys = HotkeyStore.shared
    @State private var hasPermission = Permissions.hasScreenRecording()
    @State private var launchAtLogin = Preferences.shared.launchAtLogin
    private let permTimer = Timer.publish(every: 1, on: .main, in: .common).autoconnect()

    private let lastStep = 3
    /// Vert de succès « doux » de BridgeShot (badge Granted, coches) ≈ #6BD46A.
    private static let okGreen = Color(red: 0.42, green: 0.83, blue: 0.41)

    var body: some View {
        VStack(spacing: 0) {
            topChrome
            Spacer(minLength: 12)
            stepCard
                .padding(.horizontal, 26)
                .animation(.easeInOut(duration: 0.2), value: step)
            Spacer(minLength: 12)
            Spacer(minLength: 12)        // 2 spacers en bas vs 1 en haut → la carte remonte (~1/3 depuis le haut)
            bottomNav
        }
        .frame(width: 660, height: 640)
        .background(Color(white: 0.072))                     // ≈ #121212 near-black (pas le gris système)
        .preferredColorScheme(.dark)
        .onReceive(permTimer) { _ in hasPermission = Permissions.hasScreenRecording() }
    }

    // MARK: - Chrome haut / bas

    private var topChrome: some View {
        HStack(spacing: 9) {
            brandMark(20)
            Text("FrameLine").font(.system(size: 13, weight: .bold))
            Spacer()
            Button("Skip Setup") { onFinish() }
                .buttonStyle(.plain)
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 26).padding(.top, 20).padding(.bottom, 4)
    }

    private var bottomNav: some View {
        HStack {
            Button { withAnimation { step = max(0, step - 1) } } label: {
                Text("‹ Back").font(.system(size: 12, weight: .medium))
            }
            .buttonStyle(.plain).foregroundStyle(.secondary)
            .opacity(step > 0 ? 1 : 0).disabled(step == 0)

            Spacer()
            HStack(spacing: 7) {
                ForEach(0...lastStep, id: \.self) { i in
                    Capsule()
                        .fill(i == step ? Color.white : Color.white.opacity(0.15))
                        .frame(width: i == step ? 18 : 7, height: 6)
                        .animation(.easeOut(duration: 0.18), value: step)
                }
            }
            Spacer()

            // La dernière étape porte ses actions DANS la carte → pas de bouton « Continue » ici.
            if step < lastStep {
                PillButton(title: step == 0 ? "Get Started" : "Continue", filled: true) {
                    withAnimation { step += 1 }
                }
                .keyboardShortcut(.defaultAction)
            } else {
                Color.clear.frame(width: 96, height: 1)
            }
        }
        .padding(.horizontal, 26).padding(.vertical, 18)
    }

    // MARK: - Carte d'étape (HUG le contenu + centrée verticalement)

    private var stepCard: some View {
        VStack(alignment: .leading, spacing: 0) {
            switch step {
            case 0: welcome
            case 1: permission
            case 2: shortcuts
            default: ready
            }
        }
        .frame(maxWidth: .infinity, alignment: .topLeading)
        .padding(32)
        .background(RoundedRectangle(cornerRadius: 16, style: .continuous)
            .fill(Color(white: 0.125))                       // #202020 plein
            .shadow(color: .black.opacity(0.55), radius: 26, x: 0, y: 14))
        .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous)
            .strokeBorder(Color(white: 0.247), lineWidth: 1)) // hairline #3F3F3F
    }

    // MARK: - Étape 1 — Bienvenue

    private var welcome: some View {
        VStack(alignment: .leading, spacing: 18) {
            brandTile
            VStack(alignment: .leading, spacing: 8) {
                Text("Welcome to FrameLine").font(.system(size: 21, weight: .bold))
                Text("Capture, annotate and ship beautiful screenshots — right from your menu bar. Two quick steps and you're ready.")
                    .font(.system(size: 12)).foregroundStyle(Color.white.opacity(0.46))
                    .fixedSize(horizontal: false, vertical: true)
            }
            VStack(alignment: .leading, spacing: 18) {
                featureRow("viewfinder", "Pixel-perfect captures",
                           "Area, window and fullscreen — with a frozen-screen selector.")
                featureRow("pencil.tip", "Annotate anything",
                           "Arrows, shapes, text, counters and redaction, all non-destructive.")
                featureRow("sparkles", "Beautify and share",
                           "Backgrounds, padding and one-click copy for posts and docs.")
            }
            .padding(.top, 4)
        }
    }

    // MARK: - Étape 2 — Autorisation

    private var permission: some View {
        VStack(alignment: .leading, spacing: 18) {
            iconTile("lock.shield.fill")
            VStack(alignment: .leading, spacing: 8) {
                Text("Allow Screen Recording").font(.system(size: 21, weight: .bold))
                Text("FrameLine needs permission to capture your screen. Everything stays on your Mac.")
                    .font(.system(size: 12)).foregroundStyle(Color.white.opacity(0.46))
                    .fixedSize(horizontal: false, vertical: true)
            }

            // Ligne d'état (boîte SURÉLEVÉE, plus claire que la carte)
            HStack(spacing: 12) {
                Image(systemName: "rectangle.dashed.badge.record")
                    .font(.system(size: 16))
                    .foregroundStyle(hasPermission ? Self.okGreen : .white.opacity(0.9)).frame(width: 24)
                Text("Screen Recording").font(.system(size: 14, weight: .semibold))
                Spacer()
                if hasPermission {
                    HStack(spacing: 5) {
                        Circle().fill(Self.okGreen).frame(width: 6, height: 6)
                        Text("Granted").font(.system(size: 12, weight: .semibold)).foregroundStyle(Self.okGreen)
                    }
                    .padding(.horizontal, 10).padding(.vertical, 4)
                    .background(Capsule().fill(Color.white.opacity(0.05)))
                    .overlay(Capsule().strokeBorder(Self.okGreen.opacity(0.45), lineWidth: 1))
                } else {
                    PillButton(title: "Enable", filled: false) { Permissions.openScreenRecordingSettings() }
                }
            }
            .padding(.horizontal, 14).padding(.vertical, 12)
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color(white: 0.157)))
            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous)
                .strokeBorder(Color(white: 0.275), lineWidth: 1))

            if hasPermission {
                Text("You're all set — FrameLine can capture your screen.")
                    .font(.system(size: 13)).foregroundStyle(.secondary)
            } else {
                HStack(spacing: 10) {
                    Button { Permissions.openScreenRecordingSettings() } label: {
                        Label("Open System Settings", systemImage: "gearshape")
                    }
                    Button { Permissions.relaunch() } label: {
                        Label("I've enabled it — Relaunch", systemImage: "arrow.clockwise")
                    }
                }
                .controlSize(.regular).font(.system(size: 12))
            }
        }
        .onAppear {
            // 1re fois seulement : invite système native (légitime + inscrit l'app dans la liste TCC).
            if !Preferences.shared.didRequestScreenRecording {
                Preferences.shared.didRequestScreenRecording = true
                _ = Permissions.requestScreenRecording()
            }
        }
    }

    // MARK: - Étape 3 — Raccourcis

    private var shortcuts: some View {
        VStack(alignment: .leading, spacing: 18) {
            iconTile("keyboard")
            VStack(alignment: .leading, spacing: 8) {
                Text("Set up your shortcuts").font(.system(size: 21, weight: .bold))
                Text("FrameLine answers the classic screenshot keys out of the box. Change them anytime in Settings → Shortcuts.")
                    .font(.system(size: 12)).foregroundStyle(Color.white.opacity(0.46))
                    .fixedSize(horizontal: false, vertical: true)
            }
            VStack(spacing: 12) {
                shortcutRow("rectangle.dashed", "Capture Area", CaptureHotkey.region)
                shortcutRow("macwindow", "Capture Window", CaptureHotkey.window)
                shortcutRow("rectangle.inset.filled", "Capture Fullscreen", CaptureHotkey.fullScreen)
            }
            .padding(.top, 2)
            HStack(spacing: 9) {
                Image(systemName: "checkmark.circle.fill").font(.system(size: 14)).foregroundStyle(Self.okGreen)
                Text("Shortcuts ready — \(CaptureHotkey.label(for: CaptureHotkey.region)) now belongs to FrameLine.")
                    .font(.system(size: 12.5, weight: .semibold)).foregroundStyle(.primary)
            }
        }
    }

    // MARK: - Étape 4 — Tout est prêt

    private var ready: some View {
        VStack(alignment: .leading, spacing: 18) {
            iconTile("checkmark")
            VStack(alignment: .leading, spacing: 8) {
                Text("You're all set").font(.system(size: 21, weight: .bold))
                Text("Grab your first capture now — it lands in the library, ready to annotate.")
                    .font(.system(size: 12)).foregroundStyle(Color.white.opacity(0.46))
                    .fixedSize(horizontal: false, vertical: true)
            }
            VStack(alignment: .leading, spacing: 9) {
                checkLine(hasPermission, hasPermission ? "Screen Recording granted" : "Screen Recording pending")
                checkLine(true, "\(CaptureHotkey.label(for: CaptureHotkey.region)) captures an area")
            }

            VStack(alignment: .leading, spacing: 12) {
                // Boîte info « menu bar »
                HStack(spacing: 11) {
                    brandMark(22)
                    Text("This is FrameLine in your menu bar — click it anytime for captures, your library and settings.")
                        .font(.system(size: 12.5)).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(13)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color(white: 0.157)))
                .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .strokeBorder(Color(white: 0.275), lineWidth: 1))

                // Lancement au démarrage
                HStack(spacing: 12) {
                    Image(systemName: "power").font(.system(size: 16)).foregroundStyle(.white.opacity(0.9)).frame(width: 24)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Launch FrameLine at login").font(.system(size: 13, weight: .semibold))
                        Text("Your shortcuts are ready the moment your Mac starts.")
                            .font(.system(size: 11.5)).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Toggle("", isOn: $launchAtLogin).labelsHidden().toggleStyle(.switch)
                        .onChange(of: launchAtLogin) {
                            Preferences.shared.launchAtLogin = launchAtLogin
                            launchAtLogin = Preferences.shared.launchAtLogin     // reflète l'état réel
                        }
                }
                .padding(13)
                .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color(white: 0.157)))
                .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .strokeBorder(Color(white: 0.275), lineWidth: 1))
            }

            HStack(spacing: 20) {
                PillButton(title: "Take Your First Screenshot", filled: true, icon: "camera.viewfinder") {
                    onStartCapture()
                }
                Button("Open Library") { onFinish() }
                    .buttonStyle(.plain).font(.system(size: 13, weight: .medium)).foregroundStyle(.secondary)
            }
        }
    }

    // MARK: - Helpers de présentation

    /// Logo RÉEL de l'app (icône du bundle) — comme BridgeShot affiche son logo (header + boîte menu bar).
    private func brandMark(_ size: CGFloat) -> some View {
        Image(nsImage: NSApp.applicationIconImage)
            .resizable().interpolation(.high)
            .frame(width: size, height: size)
    }

    /// Grand logo de la carte « Bienvenue » — le logo réel posé directement sur la carte (pas de plaque).
    private var brandTile: some View {
        Image(nsImage: NSApp.applicationIconImage)
            .resizable().interpolation(.high)
            .frame(width: 58, height: 58)
    }

    /// Tuile blanche à glyphe sombre (façon icônes d'étape BridgeShot).
    private func iconTile(_ system: String) -> some View {
        RoundedRectangle(cornerRadius: 18, style: .continuous)
            .fill(Color.white)
            .frame(width: 64, height: 64)
            .overlay(Image(systemName: system).font(.system(size: 28, weight: .semibold))
                .foregroundStyle(Color(white: 0.12)))
    }

    private func featureRow(_ icon: String, _ title: String, _ subtitle: String) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: icon).font(.system(size: 16, weight: .medium))
                .foregroundStyle(.white.opacity(0.92)).frame(width: 20)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 13, weight: .semibold))
                Text(subtitle).font(.system(size: 12)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private func shortcutRow(_ icon: String, _ title: String, _ hotkey: Hotkey) -> some View {
        HStack(spacing: 12) {
            Image(systemName: icon).font(.system(size: 15)).foregroundStyle(.white.opacity(0.9)).frame(width: 24)
            Text(title).font(.system(size: 13, weight: .semibold))
            Spacer()
            HStack(spacing: 4) {
                ForEach(Array(CaptureHotkey.keys(for: hotkey).enumerated()), id: \.offset) { _, k in
                    Keycap(k)
                }
            }
        }
        .padding(.horizontal, 14).padding(.vertical, 5)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color(white: 0.157)))
        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous)
            .strokeBorder(Color(white: 0.275), lineWidth: 1))
    }

    private func checkLine(_ ok: Bool, _ text: String) -> some View {
        HStack(spacing: 9) {
            Image(systemName: ok ? "checkmark.circle.fill" : "circle.dashed")
                .font(.system(size: 14)).foregroundStyle(ok ? Self.okGreen : Color.orange)
            Text(text).font(.system(size: 13, weight: .semibold)).foregroundStyle(.primary)
        }
    }
}

// MARK: - Composants

/// Touche stylisée (⇧ / ⌘ / ' …) — façon keycap BridgeShot.
private struct Keycap: View {
    let label: String
    init(_ label: String) { self.label = label }
    var body: some View {
        Text(label)
            .font(.system(size: 12, weight: .semibold, design: .rounded))
            .frame(minWidth: 20, minHeight: 18)
            .padding(.horizontal, 4)
            .background(RoundedRectangle(cornerRadius: 5, style: .continuous).fill(Color.white.opacity(0.05)))
            .overlay(RoundedRectangle(cornerRadius: 5, style: .continuous)
                .strokeBorder(Color.white.opacity(0.06), lineWidth: 1))
    }
}

/// Bouton pilule — rempli (blanc, texte sombre) ou contour, avec flèche optionnelle.
private struct PillButton: View {
    let title: String
    let filled: Bool
    var icon: String? = nil
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 7) {
                Image(systemName: icon ?? "arrow.right").font(.system(size: 11, weight: .bold))
                Text(title).font(.system(size: 12, weight: .semibold))
            }
            .padding(.horizontal, 16).padding(.vertical, 6)
            .background(
                Capsule().fill(filled
                    ? Color.white.opacity(hovering ? 1 : 0.97)
                    : Color.white.opacity(hovering ? 0.16 : 0.10)))
            .overlay(filled ? nil : Capsule().strokeBorder(Color.white.opacity(0.18), lineWidth: 1))
            .foregroundStyle(filled ? Color(white: 0.06) : .white)
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.12), value: hovering)
    }
}
