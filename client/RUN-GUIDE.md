# Run guide (local, 2026-10-11)

All paths and commands below are relative to `client/` (this directory).

Launch the single canonical portable release:

    publish/win-x64/MeetVault.exe

- Self-contained, no dotnet needed. The native (`libvosk.dll`) sits at the app root; models under `models/vosk/` — small (~32–48 MB), medium (English only, ~128 MB), large (~78 MB–1.8 GB). Staging: docs/ASR-ENGINES.md.
- This is the only portable build. Do not create extra publish folders; rebuild in place with the commands below.
- Live starts only when you press Start recording; leaving Live pauses the meeting and keeps it as a recoverable draft on the Dashboard. Settings -> Language and Model size pick the speech model (applies the next time Live is opened).
- Audio: the microphone and system output are recorded together and mixed at 48 kHz mono. Settings -> Audio has record-microphone and record-system-audio toggles, and the system-audio source picks all apps or one running app.
- Recordings: the working file is a WAV; on Stop and save it is encoded to `.m4a` (AAC 256 kbps) and the WAV is removed. If Media Foundation cannot encode, the WAV is kept instead.
- Logging: Settings -> Logging sets the level (default debug) and opens or shows the log. App log: `%TEMP%\MeetVault\app.log` (rotates at 2 MB into `app.log.1`).
- Delete: a Meetings row has a Delete button (confirm) that removes the meeting, its transcript, and its recording.
- Data: `%APPDATA%\MeetVault\meetings.db` (SQLite) + `%APPDATA%\MeetVault\media` (AAC `.m4a`). Gemini results: `%APPDATA%\MeetVault\gemini`. Crash diagnostics: `%TEMP%\MeetVault\crash.log` + `nav-errors.log`.

Needs your ears: JA/VI accuracy on real speech, overlay feel, light/dark + small/large window pass.

Build and test (developer):

    C:\Program Files\dotnet\dotnet.exe build MeetVault.sln -c Release
    C:\Program Files\dotnet\dotnet.exe test tests\MeetVault.Core.Tests\MeetVault.Core.Tests.csproj -c Release --nologo -v q

Rebuild the canonical portable release (publish to a clean folder so stale files cannot linger):

    rmdir /s /q publish\win-x64
    C:\Program Files\dotnet\dotnet.exe publish src\MeetVault\MeetVault.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
    xcopy models publish\win-x64\models /E /I /Y

The publish brings in the native libraries the app loads (`libvosk.dll` via the Vosk package); only `models/` is copied by hand. Model staging: docs/ASR-ENGINES.md.

MSIX package:

    src\MeetVault\AppPackages\MeetVault_1.0.0.0_x64_Test\MeetVault_1.0.0.0_x64.msix

Rebuild it before use; installing needs the test certificate trusted at machine scope (admin). See BLOCKERS.md.
