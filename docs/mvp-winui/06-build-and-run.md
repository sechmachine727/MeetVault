# MeetVault WinUI 3 MVP — Build and Run (Windows)

Audience: Windows implementer.

## 1. Prerequisites (Windows machine)

- Windows 10 2004+ or 11, Visual Studio with .NET and WinApp SDK workloads, `winapp` CLI 0.7+.
- No Python required. Models bundle with the installer.

## 2. Configure

- Restore the solution; verify APIs with `winapp find-api` from the app project directory.
- Discover control patterns with `winapp find-ui` before writing XAML.
- Store the Gemini key via the Settings screen (Credential Locker); never in files.

## 3. Build, package, verify

```powershell
winapp package .\MeetVault.WinUI.csproj --arch x64 --cert .\devcert.pfx
```

- Unsigned multi-arch variant: repeat `--arch` with `--no-sign`; timestamp production signing via `winapp sign`.
- Run the unit suite, scripted PCM integration, synthetic long-session test, then the manual acceptance pass in `05-test-plan.md`.
- Reference behavior lives frozen at `Wireframes/Desktop/MeetVault-Desktop-Demo/`; port behavior, not code.
