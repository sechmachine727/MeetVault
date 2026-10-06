# Run guide (local, 2026-10-07)

Launch the single canonical portable release:

    publish/win-x64/MeetVault.exe

- Self-contained, no dotnet needed. Natives (`onnxruntime.dll`, `sherpa-onnx-c-api.dll`) sit at the app root, models under `models/` (tiny 78MB, base 148MB, sherpa-seg 6MB, sherpa-emb 40MB).
- This is the only portable build. Do not create extra publish folders; rebuild in place with the commands below.
- Live starts only when you press Start recording; leaving Live pauses the meeting and keeps it as a recoverable draft on the Dashboard.
- Hardware: Settings -> Transcription -> Device lists every GPU the machine actually exposes (query, not a vendor-name guess). CUDA is offered only when the NVIDIA driver reports a device; other GPUs are offered through Vulkan when the Vulkan loader and a hardware adapter are present. With no GPU, the GPU entry is greyed out.
- Data: `%APPDATA%\MeetVault\meetings.db` (SQLite) + `%APPDATA%\MeetVault\media` (WAV). Gemini results: `%APPDATA%\MeetVault\gemini`. Crash diagnostics: `%TEMP%\MeetVault\crash.log` + `nav-errors.log`.

Needs your ears: JA/VI accuracy, two-speaker separation, overlay feel, light/dark + small/large window pass.

Build and test (developer):

    D:\dotnet\dotnet.exe build MeetVault.sln -c Release
    D:\dotnet\dotnet.exe test tests\MeetVault.Core.Tests\MeetVault.Core.Tests.csproj -c Release --nologo -v q

Rebuild the canonical portable release (publish to a clean folder so stale files cannot linger):

    rmdir /s /q publish\win-x64
    D:\dotnet\dotnet.exe publish src\MeetVault\MeetVault.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
    rem stage the two natives + models/ from your kept copies into publish\win-x64

MSIX package:

    src\MeetVault\AppPackages\MeetVault_1.0.0.0_x64_Test\MeetVault_1.0.0.0_x64.msix

