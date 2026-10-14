# Contributing to Window Rotator

First of all: thank you! 🙌 Every bug report from a real wall display makes this tool better.

## Dev setup

```powershell
git clone https://github.com/samyness/window-rotator.git
cd window-rotator
dotnet build Fensterwechler.slnx
dotnet run --project Fensterwechler.Tests      # self-check, must print "Alle Prüfungen bestanden."
dotnet run --project Fensterwechler            # start the app
```

Requirements: Windows 10/11 and the .NET 10 SDK. Visual Studio 2026 or Rider work out of the box.

## Ground rules

- **Zero NuGet dependencies** in the app. The platform already has what we need.
- **Engine stays pure.** `RotationEngine` knows nothing about WPF or Win32 – that's why it is testable. Keep it that way.
- **New logic → new check** in `Fensterwechler.Tests/Program.cs` (plain asserts, no test framework).
- **Never block the UI thread on a foreign window.** Use the async Win32 variants (`ShowWindowAsync`, `SWP_ASYNCWINDOWPOS`) and check `IsHungAppWindow`.
- **Wrong window < no window.** Re-matching must never guess.
- Build must stay at **0 warnings**.

## Pull requests

1. Fork, branch from `main` (`feature/…` or `fix/…`).
2. Keep PRs small and focused; describe what you tested on a real desktop.
3. CI (build + self-check) must be green.

## Releasing (maintainers)

Update `CHANGELOG.md` and `<Version>` in `Fensterwechler/Fensterwechler.csproj`, then push a tag `vX.Y.Z` – the release workflow builds, tests and publishes the ZIP.
