import AppKit
import ImageIO
import Vision

/// OCR « Copy Text » (comme BridgeShot) : reconnaît le texte d'une capture via Vision
/// et le place dans le presse-papiers. On reste hors @MainActor pour l'OCR lui-même
/// (calcul potentiellement long) et on ne repasse en main que pour toucher au pasteboard.
enum TextRecognizer {

    // MARK: - Points d'entrée

    /// Reconnaît le texte d'une image déjà décodée (ex. canvas de l'éditeur en mémoire).
    /// - Returns: le texte concaténé (une ligne par observation), « » si rien n'est trouvé.
    static func recognizeText(in image: CGImage) async -> String {
        // Vision est synchrone/bloquant → on l'exécute hors du thread appelant.
        await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                continuation.resume(returning: performOCR(on: image))
            }
        }
    }

    /// Reconnaît le texte du fichier `url`. Le décodage ImageIO ET l'OCR se font hors
    /// du main (un screenshot 6K coûte quelques dizaines de ms à décoder).
    static func recognizeText(from url: URL) async -> String {
        await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                guard let image = loadCGImage(from: url) else {
                    continuation.resume(returning: "")   // fichier illisible → rien à coller
                    return
                }
                continuation.resume(returning: performOCR(on: image))
            }
        }
    }

    /// Lit l'image du fichier, en extrait le texte et le copie dans le presse-papiers.
    /// - Returns: `true` si du texte a été copié (permet à l'appelant d'afficher un retour).
    ///   No-op silencieux (`false`) si l'image est illisible ou ne contient aucun texte.
    @MainActor
    @discardableResult
    static func copyText(from url: URL) async -> Bool {
        // Décodage + OCR entièrement hors main ; on ne revient ici (main) que pour le pasteboard.
        let text = await recognizeText(from: url)
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return false   // aucun texte détecté → on ne vide pas le pasteboard pour rien
        }
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
        return true
    }

    // MARK: - Privé

    /// Cœur OCR synchrone (à n'appeler que hors du main). Construit une requête neuve à
    /// chaque fois → aucun état partagé, donc utilisable depuis n'importe quelle file.
    private static func performOCR(on image: CGImage) -> String {
        let request = VNRecognizeTextRequest()
        // .accurate = qualité maximale (on privilégie la justesse à la vitesse pour du copier-coller).
        request.recognitionLevel = .accurate
        // Corrige les mots via le lexique de la langue (meilleur résultat sur du texte naturel).
        request.usesLanguageCorrection = true
        request.recognitionLanguages = preferredLanguages()

        let handler = VNImageRequestHandler(cgImage: image, options: [:])
        do {
            try handler.perform([request])
        } catch {
            // Échec de Vision (image invalide, OCR indispo) → chaîne vide plutôt qu'un crash.
            return ""
        }

        let observations = request.results ?? []
        // On garde le meilleur candidat de chaque ligne, dans l'ordre de lecture renvoyé par Vision.
        return observations
            .compactMap { $0.topCandidates(1).first?.string }
            .joined(separator: "\n")
    }

    /// Langues OCR à utiliser, ordonnées par priorité (fr, en, puis langues système).
    /// On ne renvoie QUE des codes réellement supportés par Vision : passer un code absent
    /// (ex. un « fr » nu issu de `Locale.preferredLanguages`) ferait lever `perform()`.
    private static func preferredLanguages() -> [String] {
        let supported = supportedRecognitionLanguages()
        // Souhait : français puis anglais, complétés par les préférences linguistiques de l'utilisateur.
        var wanted = ["fr-FR", "en-US"]
        for lang in Locale.preferredLanguages where !wanted.contains(lang) { wanted.append(lang) }

        var result: [String] = []
        for code in wanted {
            // Match exact d'abord ; à défaut le 1er code supporté de même base ("fr" → "fr-FR").
            let base = String(code.prefix(2))
            guard let match = supported.first(where: { $0 == code })
                ?? supported.first(where: { $0.hasPrefix(base) }),
                  !result.contains(match)
            else { continue }
            result.append(match)
        }
        // Repli : l'anglais est toujours supporté par Vision.
        return result.isEmpty ? ["en-US"] : result
    }

    /// Liste des langues OCR disponibles pour le niveau .accurate (varie selon la version de macOS).
    private static func supportedRecognitionLanguages() -> [String] {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        return (try? request.supportedRecognitionLanguages()) ?? []
    }

    /// Charge un CGImage depuis un fichier via ImageIO (thread-safe, contrairement à NSImage).
    private static func loadCGImage(from url: URL) -> CGImage? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        return CGImageSourceCreateImageAtIndex(source, 0, nil)
    }
}
