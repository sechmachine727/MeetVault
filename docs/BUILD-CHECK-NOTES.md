# BUILD-CHECK-NOTES (Task B, 2026-10-06)
Scope: verify only, no src edits, no commits, no cert work.

## Observed
- Cmd dotnet --info: SDK 8.0.425, Runtime 8.0.31, RID win-x64, no workloads, no global.json.
- Cmd dotnet test MeetVault.sln: exit 0. Passed 38/38, Failed 0, Skipped 0 (MeetVault.Core.Tests net8.0, Duration 15s).
- Cmd dotnet build MeetVault.sln --nologo: exit 0. 0 warnings, 0 errors, 46.51s. Output win-x86 MeetVault.dll.
- publish/win-x64: 153 files; MeetVault.exe 152064, MeetVault.dll 364544 present.
- publish/msix: self-contained publish present (MeetVault.exe, runtimes, locale dirs, models).
- src/MeetVault/AppPackages: MeetVault_1.0.0.0_x64.msix 62829263 present; WindowsAppRuntime.1.8.msix deps present; Add-AppDevPackage.ps1, Install.ps1, psd1 variants present; no new MSIX built by this task.
- git status --short: clean; log -3: 7a0aa5f, 7fc99ae, 85d50c0.
