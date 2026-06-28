import SwiftUI
import UniformTypeIdentifiers

/// Import par glisser-déposer, appliqué à la sidebar et au panneau de détail (qui sont
/// désormais hébergés séparément dans un NSSplitViewController).
private struct DropImport: ViewModifier {
    @EnvironmentObject var library: CaptureLibrary
    @State private var targeted = false

    func body(content: Content) -> some View {
        content
            .onDrop(of: [.fileURL], isTargeted: $targeted) { providers in
                handle(providers); return true
            }
            .overlay {
                if targeted {
                    RoundedRectangle(cornerRadius: 8)
                        .strokeBorder(Color.accentColor, lineWidth: 3)
                        .padding(4)
                        .allowsHitTesting(false)
                }
            }
    }

    private func handle(_ providers: [NSItemProvider]) {
        let group = DispatchGroup()
        var urls: [URL] = []
        let lock = NSLock()
        for provider in providers {
            group.enter()
            provider.loadDataRepresentation(forTypeIdentifier: UTType.fileURL.identifier) { data, _ in
                if let data, let url = URL(dataRepresentation: data, relativeTo: nil) {
                    lock.lock(); urls.append(url); lock.unlock()
                }
                group.leave()
            }
        }
        group.notify(queue: .main) {
            if !urls.isEmpty { _ = library.importFiles(urls) }
        }
    }
}

extension View {
    func dropImport() -> some View { modifier(DropImport()) }
}
