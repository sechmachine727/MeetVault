# Findings 2026-10-06: Live/Details/start-meeting crash root cause

Session diagnosis for "app still crashes on Start New Meeting / Details / Live".
Status: root cause fixed in the working tree (not yet built/published), halted before rebuild.

## Root cause (Observed)

The Release portable publish was **IL-trimmed** (`src/MeetVault/MeetVault.csproj` had
`PublishTrimmed=True` for non-Debug). WinUI 3 + WindowsAppSDK 1.8 on .NET 8 is not
trim-safe, and it broke the app in two independent ways:

1. **ABI vtable NRE (nav to Live/Details):** ILLink stripped CsWinRT vtable glue, so any
   managed object marshaled across the ABI as IInspectable died with
   `NullReferenceException` in `WinRT.TypeExtensions.GetAbiToProjectionVftblPtr` during
   `IItemsControlMethods.set_ItemsSource` (crash.log 16:00, nav-errors.log 14:51).
   Observed split in the SAME trimmed exe: `ObservableCollection<T>` assignments crashed;
   array assignments worked (Settings `string[]`, Dashboard/Meetings `MeetingCard[]`
   pages open fine). The array-snapshot workaround (commits dfd3ccc, 5c34d07) was correct
   but was **never republished** — publish\win-x64\MeetVault.exe on disk is 13:59, older
   than all the fixes; the user kept retesting a stale, trimmed build.
2. **Engine-init native AV (Start New Meeting):** the trimmer halved the sherpa-onnx
   managed wrapper (`sherpa-onnx.dll` 51,200 bytes in NuGet/test bin vs 24,576 bytes in
   the trimmed publish), breaking native marshaling inside
   `SherpaOnnxCreateOfflineSpeakerDiarization` (matches the earlier AV report).
   Trim evidence: same package, same IL, only the publish differs; csproj said trim=true.

## Fix applied (working tree, uncommitted)

`src/MeetVault/MeetVault.csproj`: `PublishTrimmed=False` unconditionally, with a comment
never to re-enable (ILLink breaks CsWinRT marshaling + native wrappers). PublishReadyToRun
stays true (R2R is safe). Kept the array-snapshot ItemsSource pattern and the
engines-at-OnLoaded deferral — both work fine untrimmed.

## Verified evidence (Observed)

- `dotnet test tests\MeetVault.Core.Tests` **unsandboxed (escalated): 46/46 passed, 15 s**,
  including `DiarizerContractTests` constructing the real SherpaDiarizer against
  repo `models\sherpa-seg.onnx` + `sherpa-emb.onnx`. The engine stack + native pair are
  healthy when not trimmed. Tests now number 46 (docs claim 38 — stale).
- Sandbox runs of the SAME suite abort with "vstest.console process failed to connect to
  testhost" (90 s / 300 s waits). This is a **sandbox IPC artifact, not an app bug** —
  always run `dotnet test` escalated on this machine.
- Stale artifacts: `publish\win-x64\MeetVault.exe` (13:59) and
  `src\MeetVault\AppPackages\MeetVault_1.0.0.0_x64.msix` (12:02) both predate every
  crash fix. Nothing was ever rebuilt for the user to test.

## Machine-state notes

- `C:\Windows\System32\onnxruntime.dll` exists (foreign copy, provenance unknown).
  Do not touch it. The published app ships its own pair at the app root
  (`sherpa-onnx-c-api.dll` + `onnxruntime.dll`, staged manually 2026-09-11 10:19, official
  sherpa-onnx 1.13.8 win-x64 release), and the app-root copy wins the loader search, so
  the app is unaffected. It is a hazard only for processes whose own dir lacks the pair.
- `org.k2fsa.sherpa.onnx` 1.13.8 NuGet ships only the managed wrapper (no natives).
  The natives + models in `publish\win-x64\` are manually staged and gitignored; a
  fresh clone needs restaging (documented in RUN-GUIDE/BUILD-LEDGER).

## Remaining steps (resume here)

1. Build: `D:\dotnet\dotnet.exe build MeetVault.sln -c Release` (expect 0 warn/0 err).
2. Publish portable: `D:\dotnet\dotnet.exe publish src\MeetVault\MeetVault.csproj -c Release -r win-x64 -o publish\win-x64`
   (merges into the launch folder; models/ + root natives are preserved — verify
   `sherpa-onnx.dll` in publish is now full size around 51 KB or more with R2R, not 24,576).
3. Verify headlessly first: run tests escalated (46/46), then LAUNCH the app escalated
   (user approved launching + UI walkthrough) and drive Dashboard, Start New Meeting,
   Live, Details, Settings via UI Automation; check EngineStatus shows the live engine
   string (proves sherpa native path works in-app) and that no new crash.log/nav-errors.log
   entries appear. Windows PowerShell 5.1 + System.Windows.Automation can drive it (pwsh7
   is broken in this harness). winapp CLI is not on PATH.
4. Rebuild + sign + reinstall the MSIX with the existing dev cert (find prior command in
   git history / BUILD-LEDGER; MSIX on disk is stale + was built from trimmed Release).
5. Update docs: COVERAGE.md evidence rows (46 tests; portable ABI blocker resolved),
   BUILD-LEDGER.md (never trim; rebuild artifacts), BLOCKERS.md (close the portable-crash
   blocker), RUN-GUIDE.md (rebuild before user tests — stale-exe trap).
6. Commit + push (gh CLI at `C:\Program Files\GitHub CLI\gh.exe`; git needs escalated shell).

## Tooling quirks this session

- pwsh via the exec tool: completely broken (empty output even for Write-Output).
  Use cmd with login:false, one command per call; chained ampersand commands and
  parallel batches flake to empty output. Quotes and pipes get mangled — use quote-free
  rg patterns (`rg -n -e Pat1 -e Pat2`).
- Long test output: redirect to a file, then type it.
- `dotnet test` in the sandbox aborts at testhost IPC — always escalate.
