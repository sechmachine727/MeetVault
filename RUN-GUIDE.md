# Run guide (local, 2026-10-06 evening)

Launch this build:

    publish/win-x64-fresh/MeetVault.exe

- Self-contained, no dotnet needed. Models alongside in `publish/win-x64-fresh/models` (tiny 78MB, base 148MB, sherpa-seg 6MB, sherpa-emb 40MB) plus the staging natives `onnxruntime.dll` and `sherpa-onnx-c-api.dll` at the app root.
- Do not copy a stale `publish/win-x64` over it: `publish/win-x64` (13:59) and `publish/msix` are old trimmed builds. Use `publish/win-x64-fresh`.
- Live uses the Settings model (tiny for live, base for refinement) when its file exists; without models Live runs in demo mode and the status line says so.
- With a mic, Live records + transcribes in ~6 s windows. Without models/mic it runs in demo mode.
- Gemini key: Settings page. The portable exe has no Credential Locker, so a key may not persist; the installed MSIX has the full vault.
- Data: `%APPDATA%\MeetVault\meetings.db` (SQLite) + `%APPDATA%\MeetVault\media` (WAV, beside the DB so temp cleanup cannot delete it). Gemini results: `%TEMP%\MeetVault\gemini`. Crash diagnostics: `%TEMP%\MeetVault\crash.log` + `nav-errors.log`.
- Leaving the Live page pauses the recording and keeps the draft; Dashboard offers to resume it. Use Stop to finalize.

Needs your ears: JA/VI accuracy, two-speaker separation, overlay feel, light/dark + small/large window pass.

Build (developer):

    D:\dotnet\dotnet.exe build MeetVault.sln -c Release
    D:\dotnet\dotnet.exe test tests\MeetVault.Core.Tests\MeetVault.Core.Tests.csproj -c Release --nologo -v q   # 53 green

Publish portable (publish to a clean dir, then mirror, so stale files cannot linger):

    D:\dotnet\dotnet.exe publish src\MeetVault\MeetVault.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64-new
    copy /y publish\win-x64-fresh\sherpa-onnx-c-api.dll publish\win-x64-new\
    copy /y publish\win-x64-fresh\onnxruntime.dll publish\win-x64-new\
    robocopy publish\win-x64-new publish\win-x64-fresh /MIR /XD models
