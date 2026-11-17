# FrameLine for Windows

Windows port of FrameLine, in the `windows/` folder of the same repo as the macOS app (Swift, repo root). Every command below runs from `windows/`. C# .NET 8 + WPF.
Read `docs/ARCHITECTURE.md` first: stack, macOS→Windows mapping, module owners, build commands.

- Build from macOS: `dotnet build src/FrameLine -c Release -p:Platform=x64` (needs `EnableWindowsTargeting`, already set). The app cannot run on macOS.
- Tests (Core only, any OS): `dotnet test tests/FrameLine.Core.Tests`.
- Release: `scripts/publish.sh` → `dist/FrameLine/FrameLine.exe` (self-contained single file, `ScreenRecorderLib.dll` beside it) + `dist/FrameLine-<version>-win-x64.zip`.
