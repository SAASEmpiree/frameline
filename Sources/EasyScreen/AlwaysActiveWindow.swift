import AppKit

/// Fenêtre qui se dessine TOUJOURS comme « active » : les 3 feux tricolores
/// (fermer/réduire/zoom) restent rouge/jaune/vert même quand la fenêtre n'est
/// pas key et même quand l'app entière est désactivée — c.-à-d. exactement
/// pendant une capture, où l'overlay système `/usr/sbin/screencapture -i` (un
/// process SÉPARÉ) prend le focus et désactive Easy Screen. Sur la barre de
/// titre sombre (ThemeManager → .darkAqua), le gris « inactif » d'AppKit était
/// quasi invisible : c'est la plainte corrigée ici.
///
/// COMMENT ÇA MARCHE — `NSThemeFrame` (la vue racine de la barre de titre)
/// n'interroge qu'UNE propriété pour choisir le rendu actif (coloré) vs inactif
/// (gris) de la barre ET des feux : `hasKeyAppearance`. C'est le levier exact
/// surchargé par Chromium / Electron / Firefox (commentaire Chromium textuel :
/// « Lets the traffic light buttons keep their active state »). Forcer `true`
/// fait dessiner la fenêtre comme toujours key → feux colorés en permanence,
/// SANS afficher les glyphes ×/−/+ (contrairement au vieux hack `_mouseInGroup`,
/// d'ailleurs supprimé sur macOS 26). Vérifié empiriquement sur macOS 26 : la
/// cascade hasKeyAppearance → _hasActiveAppearanceForStandardWindowButton:
/// bascule à true alors même que isKeyWindow == false et que l'app est inactive.
///
/// POURQUOI EN @objc(...) ET PAS `override` — `hasKeyAppearance` n'est PAS dans
/// les en-têtes publics du SDK, donc le compilateur Swift ne connaît pas le
/// symbole : écrire `override var hasKeyAppearance` ne compile pas. On déclare
/// une propriété au nom Swift quelconque mais dont le SÉLECTEUR ObjC est
/// `hasKeyAppearance` ; le runtime ObjC installe alors notre IMP pour ce
/// sélecteur, ce qui masque (override) l'implémentation héritée appelée par
/// AppKit via objc_msgSend. Si un futur macOS retirait la méthode, AppKit cesse
/// simplement de l'appeler → retour silencieux au comportement système, AUCUN
/// crash (dégradation sûre).
///
/// ⚠️ API privée (`hasKeyAppearance`). OK pour Easy Screen, distribué hors Mac
/// App Store (Developer ID + notarisation, qui ne scanne pas les API privées).
/// À NE PAS soumettre tel quel au Mac App Store : retirer cette classe d'abord.
/// À re-tester à chaque macOS majeur.
final class AlwaysActiveWindow: NSWindow {

    /// Mettre à `false` pour revenir au comportement système (debug / A-B test).
    /// Quand désactivé, on approxime le défaut par `isKeyWindow` (le vrai
    /// `super.hasKeyAppearance` n'est pas appelable depuis Swift).
    var forcesActiveAppearance = true

    // Sélecteur ObjC visé : `-[NSWindow hasKeyAppearance]` (BOOL). Seul `@objc(...)`
    // compte ; le nom Swift `es_hasKeyAppearance` est arbitraire et ne sert qu'à
    // éviter toute collision côté Swift.
    @objc(hasKeyAppearance) var es_hasKeyAppearance: Bool {
        forcesActiveAppearance ? true : isKeyWindow
    }

    // MARK: - Double-clic sur la barre du haut → agrandir

    /// Double-clic dans la BARRE DU HAUT (zone titre/toolbar) → agrandit/restaure la fenêtre,
    /// quel que soit le réglage système « double-clic sur la barre de titre ». Restreint à cette
    /// barre uniquement (pas le contenu) ; les boutons de la toolbar (icônes de capture, bouton
    /// sidebar) gardent leur action normale — on les exclut via un hit-test.
    override func sendEvent(_ event: NSEvent) {
        if event.type == .leftMouseDown, event.clickCount == 2,
           event.locationInWindow.y >= contentLayoutRect.maxY,                 // au-dessus du contenu = barre du haut
           !Self.hitIsButton(contentView?.superview?.hitTest(event.locationInWindow)) {
            performZoom(nil)
            return
        }
        super.sendEvent(event)
    }

    /// La vue touchée (ou un de ses ancêtres) est-elle un bouton ? (→ on laisse passer le clic)
    private static func hitIsButton(_ view: NSView?) -> Bool {
        var v = view
        while let cur = v { if cur is NSButton { return true }; v = cur.superview }
        return false
    }
}
