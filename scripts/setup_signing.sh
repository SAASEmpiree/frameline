#!/bin/bash
# setup_signing.sh — crée (une seule fois) une identité de signature LOCALE et STABLE dans un
# keychain dédié, pour que l'autorisation « Enregistrement de l'écran » (TCC) survive aux rebuilds.
#
# Pourquoi : la signature ad-hoc (`codesign -s -`) change le cdhash à chaque build → macOS croit
# que c'est une « nouvelle » app et redemande l'autorisation. Une identité stable garde la même
# exigence désignée (Designated Requirement) → l'autorisation reste accordée.
#
# Le keychain dédié a un mot de passe CONNU, ce qui rend `set-key-partition-list` non interactif
# (sinon codesign échoue en errSecInternalComponent).
set -euo pipefail

KC="$HOME/Library/Keychains/easyscreen-signing.keychain-db"
KC_PW="easyscreen-local"
ID="SAASEmpire Local Signing"

if [ -f "$KC" ] && security find-certificate -c "$ID" "$KC" >/dev/null 2>&1; then
    security unlock-keychain -p "$KC_PW" "$KC" 2>/dev/null || true
    # Test FONCTIONNEL : codesign peut-il réellement signer ? (cert présent ≠ identité utilisable)
    PROBE="$(mktemp)"; printf '#!/bin/sh\n' > "$PROBE"; chmod +x "$PROBE"
    if codesign --force --sign "$ID" "$PROBE" >/dev/null 2>&1; then
        rm -f "$PROBE"
        echo "✓ Identité « $ID » déjà présente et fonctionnelle."
        exit 0
    fi
    rm -f "$PROBE"
    echo "▸ Identité présente mais inutilisable → recréation…"
fi

echo "▸ Création du keychain de signature dédié + certificat « $ID »…"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT

# 1) clé + certificat auto-signé avec EKU codeSigning
cat > "$TMP/cert.conf" <<EOF
[req]
distinguished_name = dn
x509_extensions = v3
prompt = no
[dn]
CN = $ID
[v3]
basicConstraints = critical,CA:false
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
EOF
openssl req -x509 -newkey rsa:2048 -days 3650 -nodes \
    -keyout "$TMP/key.pem" -out "$TMP/cert.pem" -config "$TMP/cert.conf" >/dev/null 2>&1
# -legacy + -macalg sha1 : indispensable pour que « security import » accepte le p12 (OpenSSL 3).
openssl pkcs12 -export -legacy -macalg sha1 \
    -inkey "$TMP/key.pem" -in "$TMP/cert.pem" -out "$TMP/cert.p12" \
    -passout pass:esign -name "$ID" >/dev/null 2>&1

# 2) keychain dédié avec mot de passe CONNU
security delete-keychain "$KC" 2>/dev/null || true
security create-keychain -p "$KC_PW" "$KC"
security set-keychain-settings "$KC"                 # pas d'expiration / pas d'auto-lock
security unlock-keychain -p "$KC_PW" "$KC"

# 3) ajoute à la liste de recherche utilisateur (sans écraser l'existant)
CURRENT=$(security list-keychains -d user | sed 's/"//g' | xargs)
security list-keychains -d user -s $CURRENT "$KC"

# 4) importe l'identité + autorise codesign à l'utiliser sans invite
security import "$TMP/cert.p12" -k "$KC" -P esign -T /usr/bin/codesign -A >/dev/null 2>&1
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$KC_PW" "$KC" >/dev/null 2>&1

# 5) vérifie réellement que codesign fonctionne avec l'identité
printf '#!/bin/sh\necho ok\n' > "$TMP/dummy"; chmod +x "$TMP/dummy"
if codesign --force --sign "$ID" "$TMP/dummy" >/dev/null 2>&1; then
    echo "✓ Identité « $ID » prête (codesign OK) — l'autorisation écran survivra aux rebuilds."
else
    echo "⚠ codesign ne peut pas utiliser l'identité ; l'app retombera sur la signature ad-hoc."
    exit 1
fi
