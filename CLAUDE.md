# FrameLine

Native macOS menu-bar screenshot and screen-recording app (Swift Package, AppKit + SwiftUI), formerly
"EasyScreen", modelled on BridgeShot (backlog in `BRIDGESHOT_PARITY.md`). Area / window / full-screen
capture, ScreenCaptureKit recording with system audio + mic (macOS 15+), quick preview, annotation
editor, video trimmer, OCR, pinned shots, history. 100% local. UI in 10 languages (`Strings.swift`).

## Build

- `./scripts/build_app.sh` → `build/FrameLine.app` (`swift build -c release`, hardened runtime).
- Sign with the stable `SAASEmpire Local Signing` identity (`./scripts/setup_signing.sh` once) or macOS
  drops the Screen Recording permission on every rebuild (ad-hoc fallback otherwise).
- The version is `CFBundleShortVersionString` in `Resources/Info.plist`.

## Install

`./scripts/build_app.sh --install` builds, quits FrameLine, copies to `/Applications/My App/FrameLine.app`,
re-signs and refreshes Launch Services.

## Release & sync

Installing runs `~/Documents/SAASEmpiree/homebrew-tap/tools/sync-after-install`, which pushes main + tag and publishes the release; see the tap's tools/README.

- `app.json` is the manifest that tool reads (build command, artifact, update channel `brew`).
- The version is always read from the built `.app` (`CFBundleShortVersionString`), never from `app.json`.
