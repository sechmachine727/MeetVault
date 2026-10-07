# Run guide (local, 2026-10-07)

Launch the single canonical portable release:

    publish/win-x64/MeetVault.exe

- Self-contained, no dotnet needed. Natives (`onnxruntime.dll`, `sherpa-onnx-c-api.dll`) sit at the app root, models under `models/` (tiny 78MB, base 148MB, sherpa-seg 6MB, sherpa-emb 40MB).
- This is the only portable build. Do not create extra publish folders; rebuild in place with the commands below.
- Live starts only when you press Start recording; leaving Live pauses the meeting and keeps it as a recoverable draft on the Dashboard.
- Hardware: Settings -> Transcription -> Device lists what each backend can actually drive, queried not guessed. This portable build bundles both the CUDA user-mode runtime (`runtimes\cuda\win-x64`) and the MSVC/OpenMP runtime the whisper natives need, so CPU, CUDA and Vulkan all work without installing anything. CUDA still needs an NVIDIA driver of 580 or newer and a GPU the shipped CUDA native has code for (sm_8.6-8.9, RTX 30/40, or sm_120/121, RTX 50), because the display driver cannot be bundled and this build has no PTX for anything else. When nothing is usable the entry is greyed out and says why. The Live line shows the backend that actually loaded, and `%TEMP%\MeetVault\app.log` carries the driver version, each device's compute capability, and ggml's own device-selection messages.
- Audio: the microphone and system output are recorded together and mixed at 48 kHz mono. Settings -> Audio has record-microphone and record-system-audio toggles, and the system-audio source picks all apps or one running app.
- Recordings: the working file is a WAV; on Stop and save it is encoded to `.m4a` (AAC 256 kbps) and the WAV is removed. If Media Foundation cannot encode, the WAV is kept instead.
- Logging: Settings -> Logging sets the level (default debug) and opens or shows the log. App log: `%TEMP%\MeetVault\app.log`.
- Delete: a Meetings row has a Delete button (confirm) that removes the meeting, its transcript, and its recording.
- Data: `%APPDATA%\MeetVault\meetings.db` (SQLite) + `%APPDATA%\MeetVault\media` (AAC `.m4a`). Gemini results: `%APPDATA%\MeetVault\gemini`. Crash diagnostics: `%TEMP%\MeetVault\crash.log` + `nav-errors.log`.

Needs your ears: JA/VI accuracy, two-speaker separation, overlay feel, light/dark + small/large window pass.

Build and test (developer):

    D:\dotnet\dotnet.exe build MeetVault.sln -c Release
    D:\dotnet\dotnet.exe test tests\MeetVault.Core.Tests\MeetVault.Core.Tests.csproj -c Release --nologo -v q

Rebuild the canonical portable release (publish to a clean folder so stale files cannot linger):

    rmdir /s /q publish\win-x64
    D:\dotnet\dotnet.exe publish src\MeetVault\MeetVault.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
    xcopy models publish\win-x64\models /E /I /Y

The publish brings in the natives, and the CUDA and C++ runtimes when they are staged. Staging procedure: `docs\CUDA-SETUP.md`.

MSIX package:

    src\MeetVault\AppPackages\MeetVault_1.0.0.0_x64_Test\MeetVault_1.0.0.0_x64.msix

That MSIX predates the bundled CUDA and MSVC runtimes, so it will fail to load Whisper on a
machine without the VC++ redistributable. Rebuild it before use; the payloads are wired in as
`Content` items, so a normal MSIX build picks them up. The portable exe above is the artifact
that already carries them.
