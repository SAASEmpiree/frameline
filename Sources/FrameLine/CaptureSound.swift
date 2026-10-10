import AppKit

/// Sons de l'app — DEUX indicatifs distincts, chacun choisissable dans les Réglages :
///  • le DÉCLIC de capture (fichier embarqué « CaptureSound.aif » par défaut, sinon un son système) ;
///  • l'indicatif de DÉMARRAGE d'enregistrement (« Tink » par défaut, sinon un son système COURT).
/// Les deux listes de choix sont VOLONTAIREMENT DISJOINTES (par identité ET par contenu audio) → on
/// n'entend jamais le même bruit entre « prendre une capture » et « démarrer un enregistrement », et
/// aucun son n'est proposé en double (vérifié par md5 ; cf. workflow de curation des sons).
enum CaptureSound {
    /// Ordre d'affichage des sons de CAPTURE (ids). "default" = embarqué (≡ Pop, donc Pop n'est pas reproposé).
    // Uniquement : son embarqué, sons « capture d'écran » (macScreenshot/grab) et tons d'alerte génériques
    // (Glass, Funk…). PAS de son macOS associé à une AUTRE fonction (Messages, montage de disque, Siri…).
    static let captureOrder = [
        "default", "macScreenshot", "grab",
        "Glass", "Funk", "Submarine", "Ping", "Bottle", "Hero", "Sosumi", "Blow",
    ]
    /// Ordre d'affichage des sons de DÉMARRAGE d'enregistrement (ids). Tous COURTS (≤ ~0,95 s) car le départ
    /// de l'enregistrement attend la fin du son. "default" = « Tink ». DISJOINTS des sons de capture.
    static let recordingOrder = [
        "default", "beginRecord", "jblBeginShort", "micUnmute", "acknowledgmentSent",
        "dictationGo", "siriListen", "powerChime",
        "Morse", "Frog", "Basso", "Purr",
    ]

    /// Sons chargés depuis un FICHIER (système / framework) : id → chemin absolu. Les ids ABSENTS de cette
    /// table sont des sons d'alerte « /System/Library/Sounds » chargés par nom (NSSound(named:)).
    private static let SS = "/System/Library/Components/CoreAudio.component/Contents/SharedSupport/SystemSounds/"
    private static let AS = "/System/Library/PrivateFrameworks/AssistantServices.framework/Versions/A/Resources/"
    static let soundFiles: [String: String] = [
        // — Capture (uniquement des sons « capture d'écran », pas d'autres fonctions) —
        "macScreenshot":      SS + "system/Screen Capture.aif",
        "grab":               SS + "system/Grab.aif",
        // — Enregistrement —
        "beginRecord":        SS + "system/begin_record.caf",
        "jblBeginShort":      SS + "siri/jbl_begin_short.caf",
        "micUnmute":          SS + "system/mic_unmute.caf",
        "acknowledgmentSent": SS + "system/acknowledgment_sent.caf",
        "dictationGo":        AS + "dt-begin.caf",
        "siriListen":         AS + "siri-begin-improved.caf",
        "powerChime":         "/System/Library/CoreServices/PowerChime.app/Contents/Resources/connect_power.aif",
    ]

    // MARK: - Déclic de capture

    private static func bundledShutter() -> NSSound? {
        if let url = Bundle.main.url(forResource: "CaptureSound", withExtension: "aif"),
           let s = NSSound(contentsOf: url, byReference: false) { return s }
        return NSSound(named: NSSound.Name("Pop"))      // repli : son système, toujours présent
    }
    /// Résout un choix de son de capture : "default" → fichier embarqué ; id à fichier → NSSound(contentsOfFile:) ;
    /// sinon un son d'alerte nommé. Repli systématique sur le son embarqué.
    private static func shutterSound(for name: String) -> NSSound? {
        if name == "default" { return bundledShutter() }
        if let p = soundFiles[name] { return NSSound(contentsOfFile: p, byReference: false) ?? bundledShutter() }
        return NSSound(named: NSSound.Name(name)) ?? bundledShutter()
    }
    // Cache (name, sound) : on ne recharge que quand la préférence change.
    private static var shutterCache: (name: String, sound: NSSound?)?
    private static var shutter: NSSound? {
        let name = Preferences.shared.captureSoundName
        if shutterCache?.name != name { shutterCache = (name, shutterSound(for: name)) }
        return shutterCache?.sound
    }

    /// Joue le déclic de capture (si activé dans les réglages). À appeler au moment de la capture.
    static func play() {
        guard Preferences.shared.playSound, let s = shutter else { return }
        if s.isPlaying { s.stop() }    // redéclenche proprement si deux captures rapprochées
        s.play()
    }

    // MARK: - Indicatif de démarrage d'enregistrement

    /// Résout un choix de son de démarrage : "default" → « Tink » ; id à fichier → NSSound(contentsOfFile:) ;
    /// sinon un son d'alerte nommé. Tous COURTS (l'appelant attend leur fin). Repli sur « Tink ».
    private static func startSound(for name: String) -> NSSound? {
        if name == "default" { return NSSound(named: NSSound.Name("Tink")) }
        if let p = soundFiles[name] { return NSSound(contentsOfFile: p, byReference: false) ?? NSSound(named: NSSound.Name("Tink")) }
        return NSSound(named: NSSound.Name(name)) ?? NSSound(named: NSSound.Name("Tink"))
    }
    private static var startCache: (name: String, sound: NSSound?)?
    private static var startCue: NSSound? {
        let name = Preferences.shared.recordSoundName
        if startCache?.name != name { startCache = (name, startSound(for: name)) }
        return startCache?.sound
    }

    /// Joue l'indicatif de démarrage (si le son est activé) et renvoie sa durée, pour permettre à
    /// l'appelant d'attendre la fin avant de lancer l'enregistrement.
    @discardableResult
    static func playRecordingStart() -> TimeInterval {
        guard Preferences.shared.playSound, let s = startCue else { return 0 }
        if s.isPlaying { s.stop() }
        s.play()
        return s.duration
    }

    // MARK: - Aperçu (Réglages) — joue un son à la demande, même si le son global est coupé.

    static func previewCapture(_ name: String) { let s = shutterSound(for: name); s?.stop(); s?.play() }
    static func previewRecording(_ name: String) { let s = startSound(for: name); s?.stop(); s?.play() }
}
