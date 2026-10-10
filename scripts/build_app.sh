#!/bin/bash
# build_app.sh — compile FrameLine (SPM), assemble le bundle .app, génère l'icône,
# signe en ad-hoc, et (option --install) installe dans /Applications/My App/.
#
# Usage:
#   ./scripts/build_app.sh            # build vers ./build/FrameLine.app
#   ./scripts/build_app.sh --install  # build puis installe dans /Applications/My App/
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

APP_NAME="FrameLine"
EXEC_NAME="FrameLine"
STAGE="$ROOT/build/$APP_NAME.app"
INSTALL_DIR="/Applications/My App"
INSTALL_APP="$INSTALL_DIR/$APP_NAME.app"

echo "▸ [1/6] Compilation (swift build -c release)…"
swift build -c release
BIN="$ROOT/.build/release/$EXEC_NAME"
[ -f "$BIN" ] || { echo "✗ binaire introuvable: $BIN"; exit 1; }

echo "▸ [2/6] Assemblage du bundle .app…"
rm -rf "$STAGE"
mkdir -p "$STAGE/Contents/MacOS" "$STAGE/Contents/Resources"
cp "$BIN" "$STAGE/Contents/MacOS/$EXEC_NAME"
cp "$ROOT/Resources/Info.plist" "$STAGE/Contents/Info.plist"
printf 'APPL????' > "$STAGE/Contents/PkgInfo"
# Ressources média embarquées (son de capture « ScreenCapture.aif », etc.)
cp "$ROOT/Resources/"*.aif "$STAGE/Contents/Resources/" 2>/dev/null || true

echo "▸ [3/6] Génération de l'icône (.icns)…"
ICON_SRC="$ROOT/Resources/AppIcon-1024.png"
if [ ! -f "$ICON_SRC" ]; then
    echo "  · (re)génération du PNG source"
    swift "$ROOT/scripts/make_icon.swift" "$ICON_SRC"
fi
ICONSET="$ROOT/build/AppIcon.iconset"
rm -rf "$ICONSET"; mkdir -p "$ICONSET"
for sz in 16 32 128 256 512; do
    sips -z $sz $sz       "$ICON_SRC" --out "$ICONSET/icon_${sz}x${sz}.png"       >/dev/null
    sips -z $((sz*2)) $((sz*2)) "$ICON_SRC" --out "$ICONSET/icon_${sz}x${sz}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$STAGE/Contents/Resources/AppIcon.icns"

echo "▸ [4/6] Signature…"
# Identité LOCALE STABLE si dispo (autorisation écran conservée entre rebuilds), sinon ad-hoc.
SIGN_ID="-"
SIGN_KC="$HOME/Library/Keychains/easyscreen-signing.keychain-db"
SIGN_NAME="SAASEmpire Local Signing"
if [ -f "$SIGN_KC" ]; then
    security unlock-keychain -p "easyscreen-local" "$SIGN_KC" 2>/dev/null || true
    # codesign résout l'identité via la search-list utilisateur (pas d'option --keychain) → on garantit sa présence.
    if ! security list-keychains -d user | sed 's/"//g' | grep -qF "$SIGN_KC"; then
        security list-keychains -d user -s $(security list-keychains -d user | sed 's/"//g' | xargs) "$SIGN_KC"
    fi
    # Sonde FONCTIONNELLE : codesign peut-il VRAIMENT signer avec l'identité ? (sinon repli ad-hoc honnête)
    PROBE="$(mktemp)"; printf '#!/bin/sh\n' > "$PROBE"; chmod +x "$PROBE"
    if codesign --force --sign "$SIGN_NAME" "$PROBE" >/dev/null 2>&1; then
        SIGN_ID="$SIGN_NAME"
        echo "  · identité stable : $SIGN_NAME"
    fi
    rm -f "$PROBE"
fi
[ "$SIGN_ID" = "-" ] && echo "  · signature ad-hoc (lance scripts/setup_signing.sh pour des autorisations stables)"
# Garde-fou OPT-IN : pour les tests TCC, exiger l'identité stable (l'ad-hoc reperd l'autorisation
# « Enregistrement de l'écran » à chaque rebuild). N'échoue QUE si REQUIRE_STABLE_SIGNING=1 → les
# builds frais / la CI restent non bloqués.  Usage : REQUIRE_STABLE_SIGNING=1 ./scripts/build_app.sh --install
if [ "$SIGN_ID" = "-" ] && [ "${REQUIRE_STABLE_SIGNING:-0}" = "1" ]; then
    echo "✗ Identité stable requise (REQUIRE_STABLE_SIGNING=1) mais indisponible — lance d'abord scripts/setup_signing.sh" >&2
    exit 1
fi
# Single binary, aucun framework embarqué → pas de --deep.
# [SEC] --options runtime : active le HARDENED RUNTIME. AMFI ignore alors DYLD_INSERT_LIBRARIES
# et la validation de bibliothèque rejette toute dylib étrangère → ferme l'injection de code
# dans ce process (qui détient les permissions TCC Écran/Micro) et donc le contournement TCC.
# Fonctionne même en signature ad-hoc/locale (binaire unique, aucune dylib tierce à charger).
codesign --force --options runtime --sign "$SIGN_ID" "$STAGE"
codesign --verify --strict "$STAGE" && echo "  · signature OK (hardened runtime)"

if [ "${1:-}" == "--install" ]; then
    echo "▸ [5/6] Installation dans ${INSTALL_DIR} …"
    mkdir -p "$INSTALL_DIR"
    # Quitter une instance en cours pour éviter "Resource busy"
    pkill -x "$EXEC_NAME" 2>/dev/null || true
    sleep 0.5
    rm -rf "$INSTALL_APP"
    cp -R "$STAGE" "$INSTALL_APP"
    # Re-signer après copie (le chemin change) avec la MÊME identité + hardened runtime.
    codesign --force --options runtime --sign "$SIGN_ID" "$INSTALL_APP" >/dev/null 2>&1 || true
    # Rafraîchir le cache Launch Services
    /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister \
        -f "$INSTALL_APP" 2>/dev/null || true
    echo "  · installé: $INSTALL_APP"
    echo "▸ [6/6] Terminé. Lance l'app: open \"$INSTALL_APP\""
else
    echo "▸ [5/6] (pas d'install — utilise --install pour déployer)"
    echo "▸ [6/6] Terminé. Bundle de test: $STAGE"
fi
