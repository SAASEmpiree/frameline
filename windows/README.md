<div align="center">
  <img src="docs/icon.png" width="116" alt="FrameLine" />

  # FrameLine for Windows 🎯

  **Screenshots and screen recordings from your system tray. Fast, simple, 100% local.**

  [![Windows 10 2004+ / 11](https://img.shields.io/badge/Windows-10%202004%2B%20%C2%B7%2011-0a0a0c?style=flat-square&logo=windows&logoColor=white)](#-install)
  [![Version](https://img.shields.io/badge/version-1.0.1-ff3b30?style=flat-square)](../../releases/latest)
  [![C#](https://img.shields.io/badge/-C%23%20%C2%B7%20.NET%208%20%C2%B7%20WPF-512BD4?style=flat-square&logo=dotnet&logoColor=white)](#-build-from-source)
  [![Local](https://img.shields.io/badge/local-100%25-2fd389?style=flat-square)](#-what-it-does)

  <br />

  <a href="../../releases/latest"><img src="https://img.shields.io/badge/Download%20for%20Windows-FrameLine-ff3b30?style=for-the-badge&logo=windows&logoColor=white" alt="Download FrameLine for Windows" /></a>

  <sub>Windows 10 (2004+) / 11 · x64 · the same app as <a href="https://github.com/SAASEmpiree/frameline">FrameLine for macOS</a></sub>
</div>

---

## 💡 What it does

FrameLine takes screenshots and records your screen from the system tray. Press a shortcut, drag over an area (or click a window), and the capture pops up as a small preview you can copy, annotate, pin on screen or drag straight into another app. Recordings capture the system sound and your microphone, and can be trimmed right after. Everything stays on your PC: no account, no cloud, no upload.

It is a port of the macOS app, feature for feature and string for string: same menu, same settings, same editor, same 10 languages.

## ✨ Features

- 🖼️ **Area, window or full screen** — with a loupe and pixel colour while you select, plus a 3 / 5 / 10 s timer for full screen. Multi-monitor and per-monitor DPI aware.
- 🎬 **Screen recording** — MP4 (H.264 + AAC) of the primary monitor with system audio and microphone, start and stop from the tray or **Ctrl+Shift+E**.
- 🪟 **Quick preview** — floating thumbnail after each capture, in the corner you choose; drag and drop it anywhere.
- ✏️ **Annotation editor** — rectangle, arrow, pen, text, pixelate and crop.
- ✂️ **Video trimmer** — trim a recording in place, the twin of the image editor.
- 🔤 **Copy text (OCR)** — Windows OCR reads the text in a capture and puts it on the clipboard.
- 📌 **Pin to screen** — keep a capture floating above your windows, or pin the image on your clipboard.
- 🗂️ **History & library** — recent captures in a floating panel, every capture in one window.
- ⌨️ **Global shortcuts** — **Ctrl+Shift+4** area, **Ctrl+Shift+8** window, **Ctrl+Shift+9** full screen, **Ctrl+Shift+E** record by default (virtual keys, so they work on AZERTY too); change any of them in **Settings → Shortcuts**.
- 🗣️ **UI in 10 languages** — switch the display language live; light, dark or system theme.
- 🤖 **Local MCP server** — Claude Code, Cursor or Coucou can take screenshots, record the screen and read your captures (127.0.0.1 only, secret token in `%APPDATA%\FrameLine\mcp.json`, can be turned off in Settings).
- 🔔 **Capture sounds**, PNG or JPG output, copy-to-clipboard and open-in-editor after capture, launch at login, `frameline://` links.

## 📦 Install

**Direct download (easiest):**

1. Download **FrameLine-&lt;version&gt;-win-x64.zip** from the [latest release](../../releases/latest).
2. Unzip it anywhere you like, for example `%LOCALAPPDATA%\Programs\FrameLine`. Keep `ScreenRecorderLib.dll` and the `Assets` folder next to `FrameLine.exe`.
3. Run **FrameLine.exe**. It is not signed yet, so the first time SmartScreen may say *Windows protected your PC*: click **More info → Run anyway** (only once).

No .NET install is needed: the runtime is inside the exe. Screen **recording** needs the [Visual C++ Redistributable x64](https://aka.ms/vs/17/release/vc_redist.x64.exe) (most PCs already have it) and Media Foundation (on Windows N editions, install the *Media Feature Pack*).

**With Scoop:** coming later.

A **FrameLine icon** appears in the system tray (click the `^` arrow if Windows hides it) and the setup guide walks you through the shortcuts. Windows has no screen-capture permission; the **Microphone** step is only needed if you record your voice (*Settings → Privacy & security → Microphone → Let desktop apps access your microphone*).

## 🔄 Updates

FrameLine has no built-in updater. Download the new zip from the [latest release](../../releases/latest), quit FrameLine from the tray menu, and replace the files. Your settings live in `%APPDATA%\FrameLine` and your captures in `Pictures\FrameLine`, so nothing is lost.

## 🛠️ Build from source

.NET 8 SDK, from **Windows or macOS** (cross-compiling a WPF app is supported with `EnableWindowsTargeting`, already set; the app itself only runs on Windows).

```sh
dotnet build src/FrameLine -c Release -p:Platform=x64   # build (Platform must be x64: ScreenRecorderLib is native)
dotnet test tests/FrameLine.Core.Tests                  # settings, i18n, library, MCP: runs on any OS
./scripts/publish.sh                                    # dist/FrameLine/FrameLine.exe + dist/FrameLine-<version>-win-x64.zip
```

On Windows, run `publish.sh` from Git Bash or WSL, or call the `dotnet publish` line inside it from PowerShell. The version is `<Version>` in `src/FrameLine/FrameLine.csproj`.

## 🗂️ Project layout

```
src/FrameLine.Core/       # net8.0, no Windows API: settings, i18n, capture library, MCP server
  Strings.cs / I18n.cs    #   UI strings × 10 languages
  Mcp/                    #   local MCP server (127.0.0.1, Bearer token)
src/FrameLine/            # net8.0-windows, WPF
  Shell/                  #   tray, hotkeys, coordinator, settings, onboarding, MCP host
  Capture/                #   GDI capture, region / window overlays, loupe, sounds
  Recording/              #   ScreenRecorderLib MP4, trimmer (MediaComposition)
  Editor/                 #   annotation editor, OCR (Windows.Media.Ocr)
  Library/                #   quick preview, history panel, pinned shots, library window
  Platform/               #   theme, Mica / dark title bar, shared toast
tests/FrameLine.Core.Tests/  # xunit, runs on macOS too
docs/ARCHITECTURE.md      # macOS → Windows mapping and module map
scripts/publish.sh        # release build + zip
```

## 📄 License

All rights reserved. © SAAS Empire

<div align="center"><sub>Frame it, ship it, nothing leaves your PC.</sub></div>
