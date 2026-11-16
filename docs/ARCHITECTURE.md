# FrameLine for Windows: architecture and module ownership

Port of the macOS app (Swift, `~/Documents/SAASEmpiree/FrameLine`, commit 4899e86, the REFERENCE:
read it, never edit it). Goal: the same app, feature for feature, string for string, on Windows 10 (19041+) / 11.

## Stack

- C# / .NET 8, WPF for every window, WinForms only for `NotifyIcon` (tray). No other UI framework.
- `src/FrameLine.Core` (`net8.0`, no Windows API): settings, i18n, capture library, MCP server. Unit-tested on any OS.
- `src/FrameLine` (`net8.0-windows10.0.19041.0`, x64, self-contained): everything that touches Windows.
- `tests/FrameLine.Core.Tests` (xunit, `net8.0`): runs on macOS too.
- Packages allowed: `ScreenRecorderLib` (recording: Media Foundation H.264 MP4, WASAPI loopback + mic,
  cursor), `NAudio` (audio helpers if needed). Anything else: justify in the module report.
- WinRT APIs available through the TFM: `Windows.Media.Ocr`, `Windows.Media.Editing` (trim),
  `Windows.Graphics.Imaging`, `Windows.Storage`.

## Build (from macOS, cannot run the app here)

Concurrent agents must never share `obj/`: always pass your own artifacts path.

    dotnet build src/FrameLine -c Release -p:Platform=x64 --artifacts-path /tmp/fl-art-<you>
    dotnet test tests/FrameLine.Core.Tests --artifacts-path /tmp/fl-art-<you>-t

Release: `scripts/publish.sh` (single-file self-contained `dist/FrameLine.exe` + zip for Scoop).

## macOS → Windows mapping (decided, do not re-litigate)

| macOS | Windows |
|---|---|
| menu bar item (`StatusBarController`) | tray icon (`NotifyIcon`) + WPF-styled context menu; left click = same as mac click |
| ⇧⌘4 / ⇧⌘8 / ⇧⌘9 / ⇧⌘E | **Ctrl+Shift+4 / 8 / 9 / E** via `RegisterHotKey` on VK codes (layout-independent, works on AZERTY) |
| ScreenCaptureKit screenshot | GDI `BitBlt` from the desktop DC over the virtual screen, per-monitor DPI v2 aware |
| `screencapture -i -w` window pick | own overlay: hover highlights the top-level window under the cursor (`DwmGetWindowAttribute` EXTENDED_FRAME_BOUNDS), click captures it (`PrintWindow` PW_RENDERFULLCONTENT, fallback BitBlt); `WindowShadow` adds a soft drop shadow like macOS |
| `RegionSelectOverlay` + loupe | one borderless topmost WPF window per monitor (or one over the virtual screen), frozen screenshot as background, loupe + pixel colour, Esc cancels |
| ScreenCaptureKit recording + audio | `ScreenRecorderLib` (system audio = loopback, mic optional) to MP4 |
| AVFoundation trimmer | `MediaElement` playback + `Windows.Media.Editing.MediaComposition` trim/render |
| Vision OCR | `Windows.Media.Ocr` |
| NSPasteboard | WPF `Clipboard` (image + file drop list) |
| NSSound / system sounds | bundled `CaptureSound.wav` + `C:\Windows\Media\*.wav`; same rules: capture and recording lists disjoint, recording cues ≤ 1 s, no sound tied to another Windows function |
| UserDefaults | `%APPDATA%\FrameLine\settings.json` |
| `~/Pictures/FrameLine` | `%USERPROFILE%\Pictures\FrameLine` |
| SMAppService login item | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `FrameLine` |
| Screen Recording permission | none on Windows; onboarding keeps its steps but the permission step becomes "Microphone (optional)" via `ms-settings:privacy-microphone` |
| Finder reveal / Trash | `explorer /select,` and `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., SendToRecycleBin)` |
| `~/Library/Application Support/FrameLine/mcp.json` | `%APPDATA%\FrameLine\mcp.json` (ACL: current user only) |
| Liquid Glass / vibrancy | dark translucent surfaces; Windows 11 Mica/Acrylic via `DwmSetWindowAttribute` (DWMWA_SYSTEMBACKDROP_TYPE) when available, solid fallback on Windows 10 |
| ⌘ in labels/strings | Ctrl (strings that show shortcuts must show Ctrl+Shift+…) |

## Modules and owners (each agent writes ONLY its own folder)

| Owner | Folder | Swift sources to port |
|---|---|---|
| core | `src/FrameLine.Core/*.cs`, `tests/**` except `Mcp*` tests | Preferences, I18n, Strings, CaptureLibrary (file part) |
| mcp | `src/FrameLine.Core/Mcp/`, `tests/FrameLine.Core.Tests/Mcp*` | MCP/* (JSONRPC, HTTP, Server, Discovery, Catalog, Tools logic), tests/MCP* |
| capture | `src/FrameLine/Capture/` | ScreenCapturer, CaptureManager, RegionSelectOverlay, OverlayText, CaptureSound (window pick replaces screencapture -w) |
| recording | `src/FrameLine/Recording/` | RecordingManager (+ GIF export), VideoTrimmer |
| editor | `src/FrameLine/Editor/` | PreviewWindowController (annotation editor), TextRecognizer |
| library | `src/FrameLine/Library/` | CaptureThumbnail, CaptureHistoryPanel, PinnedShotWindow, MainWindowController, SidebarView, DetailView, ContentView |
| shell | `src/FrameLine/Shell/`, `src/FrameLine/Platform/`, `App.xaml(.cs)` | AppDelegate, AppCoordinator, StatusBarController, HotKeyManager, Shortcuts, SettingsView, PreferencesWindowController, OnboardingView/Controller, ThemeManager, GlassUI (shared styles in `Platform/`), Notifications, DebugLog, WindowCentering, AlwaysActiveWindow, MCPController glue (`Shell/McpHost.cs`) |

Cross-module calls go through the public types in the `*Api.cs` stub files and in Core. Those
signatures are the contract: an owner fills the bodies (may split into more files in its folder,
may ADD members), never removes or renames a public member. Need something from another module that
the contract lacks? Write it in your final report; the integration step adds it.

Shared look (owner shell, `Platform/Theme.xaml` + `Platform/Glass.cs`): other modules use plain WPF
with `DynamicResource` keys documented there; until it exists, use local styles in your own folder.

## Rules

- Deterministic UI thread use: WPF objects only on the dispatcher; capture/encode work off-thread.
- Never create fonts/brushes/typefaces per frame in a render or mouse-move path: cache them static (the
  macOS app crashed for exactly this, see `OverlayText.swift`).
- Thumbnail generation: bounded concurrency (max 2), decode with `DecodePixelWidth`, `Freeze()` bitmaps.
- Every user-visible string goes through `Loc.L("key")` with the macOS keys; add Windows-only keys to
  the Strings table in all 10 languages (owner core; others list them in their report).
- No telemetry, no network except the loopback MCP server.
