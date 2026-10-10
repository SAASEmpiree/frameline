import AppKit

extension NSWindow {
    /// Centre la fenêtre sur l'écran qui contient le curseur (« notre » écran ACTIF),
    /// légèrement au-dessus du centre — exactement comme Flow et Units (`centerOnActiveScreen`).
    /// Repli : `center()` (écran principal) si l'écran actif est introuvable.
    func centerOnActiveScreen() {
        let mouse = NSEvent.mouseLocation
        // NSMouseInRect (et non `contains`) : curseur collé en haut de l'écran → y == frame.maxY, exclu
        // par `contains` → repli sur NSScreen.main (autre écran), ou l'écran empilé au-dessus.
        let screen = NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) } ?? NSScreen.main
        guard let visible = screen?.visibleFrame else { center(); return }
        let size = frame.size
        let x = visible.midX - size.width / 2
        let y = visible.midY - size.height / 2 + visible.height * 0.04
        setFrameOrigin(NSPoint(x: round(x), y: round(y)))
    }
}
