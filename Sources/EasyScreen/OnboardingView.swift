import SwiftUI

/// Tutoriel de premier lancement : bienvenue → modes de capture → autorisation écran → prêt.
struct OnboardingView: View {
    let onFinish: () -> Void

    @State private var step = 0
    @State private var hasPermission = Permissions.hasScreenRecording()
    private let permTimer = Timer.publish(every: 1, on: .main, in: .common).autoconnect()

    private var primaryTitle: String {
        switch step { case 0: return "Commencer"; case 3: return "Terminer"; default: return "Suivant" }
    }

    var body: some View {
        VStack(spacing: 0) {
            ZStack {
                switch step {
                case 0: welcome
                case 1: modes
                case 2: permission
                default: ready
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .padding(.horizontal, 40)
            .animation(.easeInOut(duration: 0.2), value: step)

            HStack {
                Button("Retour") { withAnimation { step = max(0, step - 1) } }
                    .buttonStyle(.plain).foregroundStyle(.secondary)
                    .opacity(step > 0 ? 1 : 0)
                    .disabled(step == 0)            // opacity(0) ne coupe pas le clic en SwiftUI
                Spacer()
                HStack(spacing: 7) {
                    ForEach(0..<4, id: \.self) { i in
                        Circle()
                            .fill(i == step ? Color.accentColor : Color.white.opacity(0.22))
                            .frame(width: 7, height: 7)
                    }
                }
                Spacer()
                Button(primaryTitle) {
                    if step < 3 { withAnimation { step += 1 } } else { onFinish() }
                }
                .keyboardShortcut(.defaultAction)
                .controlSize(.large)
            }
            .padding(20)
        }
        .frame(width: 640, height: 500)
        .background(Color(nsColor: .windowBackgroundColor))
        .preferredColorScheme(.dark)
        .onReceive(permTimer) { _ in hasPermission = Permissions.hasScreenRecording() }
    }

    // MARK: - Étapes

    private var welcome: some View {
        VStack(spacing: 20) {
            appTile
            Text("Bienvenue dans Easy Screen").font(.system(size: 27, weight: .bold))
            Text("Capturez une zone, une fenêtre ou tout l'écran — annotez et partagez en un éclair.\n100 % gratuit, 100 % local, sans compte.")
                .font(.system(size: 14)).foregroundStyle(.secondary)
                .multilineTextAlignment(.center).frame(maxWidth: 440)
        }
    }

    private var modes: some View {
        VStack(spacing: 24) {
            Text("Trois façons de capturer").font(.system(size: 22, weight: .bold))
            VStack(spacing: 12) {
                modeRow("viewfinder", "Capturer une zone", "Sélectionnez n'importe quelle portion de l'écran", CaptureHotkey.label(for: CaptureHotkey.region))
                modeRow("macwindow", "Capturer une fenêtre", "Cliquez sur une fenêtre pour la saisir", CaptureHotkey.label(for: CaptureHotkey.window))
                modeRow("display", "Tout l'écran", "Capturez l'écran entier d'un coup", CaptureHotkey.label(for: CaptureHotkey.fullScreen))
                modeRow("record.circle", "Enregistrer", "Filmez votre écran en vidéo", CaptureHotkey.label(for: CaptureHotkey.record))
            }
            .frame(maxWidth: 460)
        }
    }

    private var permission: some View {
        VStack(spacing: 16) {
            Image(systemName: hasPermission ? "checkmark.shield.fill" : "lock.shield")
                .font(.system(size: 52)).foregroundStyle(hasPermission ? .green : .orange)
            Text("Autorisez l'enregistrement de l'écran").font(.system(size: 21, weight: .bold))
            Text("macOS exige cette autorisation pour capturer l'écran. Activez Easy Screen dans Réglages Système › Confidentialité, puis relancez Easy Screen pour finaliser.")
                .font(.system(size: 14)).foregroundStyle(.secondary)
                .multilineTextAlignment(.center).frame(maxWidth: 460)
            if hasPermission {
                Label("Autorisation accordée", systemImage: "checkmark.circle.fill")
                    .foregroundStyle(.green).font(.system(size: 14, weight: .medium)).padding(.top, 4)
            } else {
                // Réglages = UNIQUEMENT le volet (plus de prompt empilé). Relance explicite : seule façon
                // fiable de rafraîchir l'état TCC en session (CGPreflight est caché par process).
                HStack(spacing: 10) {
                    Button { Permissions.openScreenRecordingSettings() } label: {
                        Label("Ouvrir les Réglages Système", systemImage: "gearshape")
                    }
                    Button { Permissions.relaunch() } label: {
                        Label("J'ai activé — Relancer", systemImage: "arrow.clockwise")
                    }
                }
                .controlSize(.large).padding(.top, 4)
            }
        }
        .onAppear {
            // 1re fois seulement : prompt système natif (invite légitime + inscrit l'app dans la liste TCC).
            if !Preferences.shared.didRequestScreenRecording {
                Preferences.shared.didRequestScreenRecording = true
                Permissions.requestScreenRecording()
            }
        }
    }

    private var ready: some View {
        VStack(spacing: 18) {
            Image(systemName: "checkmark.circle.fill").font(.system(size: 56)).foregroundStyle(.green)
            Text("Tout est prêt !").font(.system(size: 26, weight: .bold))
            Text("Utilisez \(CaptureHotkey.label(for: CaptureHotkey.region)) à tout moment, ou cliquez sur l'icône 📷 dans la barre des menus.\nVos captures apparaissent automatiquement dans la galerie.")
                .font(.system(size: 14)).foregroundStyle(.secondary)
                .multilineTextAlignment(.center).frame(maxWidth: 460)
        }
    }

    // MARK: - Helpers

    private var appTile: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 24)
                .fill(LinearGradient(colors: [Color(red: 0.18, green: 0.49, blue: 0.96),
                                              Color(red: 0.48, green: 0.25, blue: 0.95)],
                                     startPoint: .topLeading, endPoint: .bottomTrailing))
                .frame(width: 104, height: 104)
            Image(systemName: "camera.viewfinder").font(.system(size: 46)).foregroundStyle(.white)
        }
    }

    private func modeRow(_ icon: String, _ title: String, _ subtitle: String, _ keys: String) -> some View {
        HStack(spacing: 14) {
            Image(systemName: icon).font(.system(size: 18)).frame(width: 30)
                .foregroundStyle(.white.opacity(0.9))
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 14, weight: .semibold))
                Text(subtitle).font(.system(size: 12)).foregroundStyle(.secondary)
            }
            Spacer()
            Text(keys).font(.system(size: 12, weight: .semibold, design: .rounded))
                .padding(.horizontal, 8).padding(.vertical, 4)
                .background(RoundedRectangle(cornerRadius: 6).fill(Color.white.opacity(0.1)))
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.white.opacity(0.05)))
    }
}
