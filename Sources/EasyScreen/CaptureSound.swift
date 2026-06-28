import AppKit

/// Son de capture — un petit déclic simple et net (≠ du son de capture par défaut de macOS).
/// Fichier « CaptureSound.aif » embarqué dans le bundle ; repli sur le son système « Pop ».
enum CaptureSound {
    private static let shutter: NSSound? = {
        if let url = Bundle.main.url(forResource: "CaptureSound", withExtension: "aif"),
           let s = NSSound(contentsOf: url, byReference: false) { return s }
        return NSSound(named: NSSound.Name("Pop"))      // repli : son système, toujours présent
    }()

    /// Joue le déclic de capture (si activé dans les réglages). À appeler au moment de la capture.
    static func play() {
        guard Preferences.shared.playSound, let s = shutter else { return }
        if s.isPlaying { s.stop() }    // redéclenche proprement si deux captures rapprochées
        s.play()
    }
}
