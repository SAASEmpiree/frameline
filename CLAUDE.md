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

## Local MCP server

`Sources/FrameLine/MCP/`: Streamable HTTP MCP on `127.0.0.1:<free port>/mcp`, Bearer token (256-bit,
new at every launch), Host/Origin limited to loopback, no third-party dependency (Network.framework).
Discovery file `~/Library/Application Support/FrameLine/mcp.json` (0600, dir 0700, removed on quit),
read by Coucou. Toggle in Settings > General > "Serveur MCP (local)" (default on).

- Core (Foundation/Network only): `MCPJSONRPC`, `MCPHTTP`, `MCPServer`, `MCPDiscovery`, `FrameLineMCPCatalog`
  (tool names, schemas, annotations). App glue: `FrameLineMCPTools` (handlers), `MCPController` (lifecycle).
- Tests: `./scripts/test-mcp.sh` (unit + real loopback listener). Against the running app:
  `./scripts/mcp-smoke.sh` (read-only).
- Build gotcha (Command Line Tools only): the default macOS 27 SDK lacks the SwiftUI macro plugin
  (`@State` fails to expand). Build with `SDKROOT=/Library/Developer/CommandLineTools/SDKs/MacOSX26.5.sdk ./scripts/build_app.sh`.
