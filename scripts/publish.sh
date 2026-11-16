#!/usr/bin/env bash
# Builds the Windows release from macOS or Windows (Git Bash / WSL):
#   dist/FrameLine/FrameLine.exe            self-contained single file (no .NET install needed)
#   dist/FrameLine/ScreenRecorderLib.dll    mixed-mode C++/CLI, must stay next to the exe
#   dist/FrameLine-<version>-win-x64.zip    the folder above, for GitHub releases / Scoop
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/FrameLine/FrameLine.csproj"
OUT="$ROOT/dist/FrameLine"
ARTIFACTS="${ARTIFACTS:-${TMPDIR:-/tmp}/frameline-publish}"

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | head -n1)"
[ -n "$VERSION" ] || { echo "No <Version> in $PROJECT" >&2; exit 1; }
ZIP="$ROOT/dist/FrameLine-$VERSION-win-x64.zip"

rm -rf "$OUT" "$ZIP"
mkdir -p "$OUT"

dotnet publish "$PROJECT" \
  -c Release -p:Platform=x64 -r win-x64 --self-contained \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none \
  --artifacts-path "$ARTIFACTS" \
  -o "$OUT"

# Debug symbols and XML docs that ride along with package references are not needed to run.
find "$OUT" \( -name '*.pdb' -o -name '*.xml' \) -delete

for f in FrameLine.exe ScreenRecorderLib.dll; do
  [ -f "$OUT/$f" ] || { echo "Missing $OUT/$f" >&2; exit 1; }
done

( cd "$OUT" && zip -qr -X "$ZIP" . )

echo "FrameLine $VERSION"
ls -l "$OUT"
echo "zip: $ZIP ($(du -h "$ZIP" | cut -f1))"
