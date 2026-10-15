# BLOCKERS.md — genuine blockers for user review

> Updated 2026-10-15: the engine surface is one family — **Vosk** (English / Japanese, the
> `large` tier only), offline streaming, CPU-only, **single-speaker by construction**. The
> Vietnamese models and the small/medium tiers were removed from the deliverable the same day
> (owner steer); Whisper, Moonshine, Zipformer, the sherpa diarization/VAD stack and the GPU
> backends were removed earlier. Only items that stop progress with no local alternative go here.

## Active (need you physically present — no local alternative)

### 1. Mic-present recording
- This machine reports a microphone now ("0: Microphone Array (MacBook Pro M)"), but unattended synthetic testing of a real recording is not something I can validate for you.
- Next: run `publish/win-x64/MeetVault.exe`, press Start recording, speak, Stop, then open the meeting in Meetings and press play. Report anything that looks wrong.

### 2. Spoken Japanese accuracy
- The deliverable ships `large-en` + `large-ja` only. Both load live (`vosk: loaded dir=…`) and produce + save segments; `large-en` also passes the live contract test end-to-end (a 17 s fixture → partials → ≥2 sentence-cased endpoint finals, matched against the fixture's ground truth). The Vietnamese models (`small-vi`, `large-vi`) and the small/medium tiers (`small-en`, `medium-en`, `small-ja`) are no longer shipped: the catalog and `tools/pack-portable.ps1` carry only the two large models, and a stored setting that names a retired model resolves to the shipped default. Accuracy on real spoken JA has not been judged.
- Next: one short spoken JA clip, with Japanese selected in Settings.

### 3. Cloud service (not a blocker — verified 2026-10-15)
- The cloud flow is live: sign-in and the Cloud Storage list were driven end-to-end against the deployed backend (`https://meetvault.supersanta.io.vn`) on 2026-10-15, and the API smoke (login → create → upload → process → transcript → analysis) passed. The retired Gemini integration is gone from both source and strings.
- Remaining owner-side check: Analyze → summary → Word export on a *fresh real recording* from this machine (the earlier proof used a short probe clip).

### 4. MSIX install (needs an admin machine-root trust)
- The MSIX builds unsigned (no `signtool` on this box) and install needs the AppPublisher cert trusted at machine scope, which needs admin.
- Next: either install the signed MSIX after trusting the cert (thumb `BBD1DFCFC1F34E14A603110858C4CD019A7B70DD`), or keep using the portable exe.

## Not blockers (tracked for completeness)

- Audio capture mixes the microphone and system output (verified: the log shows `capture: started mic=on system=all sources=2`). Per-app capture uses the WASAPI process-loopback API (`ProcessLoopbackCapture`); it falls back to system-wide capture when activation fails. The per-app path needs a check on a machine with the target app rendering audio.
- Capture is paced against the wall clock; the working WAV is 48 kHz mono and is compacted to `.m4a` (AAC 256 kbps) on Stop. If you ever hear a recording too quiet or too fast, check `capture: no audio samples were captured` and the pacing lines in `%TEMP%\MeetVault\app.log`.
- `storage.path` governs export only; the transcript database stays in `%APPDATA%\MeetVault`.
- Meetings recorded by the retired diarized pipeline keep their speaker rows in Details; the speaker picker is still editable (only rename was removed); new meetings are single-speaker and mint no speaker identities (the schema-required `spk_1` stamp is constant).
- Contract tests that need the real model (`VoskStreamingTranscriberTests`) self-skip (early return) when `models/` is unstaged; CI has no models, so CI cannot prove live decoding — local runs on this box cover it.
- The solution builds x64 only (the ScreenRecorderLib mixed-mode package rejects AnyCPU): `MeetVault.Core` and the test project pin `Platform=x64`, and the .sln maps Any CPU to x64 for every project. Direct `dotnet build`/`dotnet test` invocations pick x64 up from the csproj; do not add an AnyCPU configuration.
- Screen recording uses WindowsGraphicsCapture inside ScreenRecorderLib — the DesktopDuplication default produced solid-black frames on this box (probe P2); window capture is WGC-only by the library's design. A destroyed target window raises no failure event, so the player polls the HWND and stops proactively.
- The language detector needs `Microsoft.ML.OnnxRuntime.dll` assembly version **1.31.0.0** in the publish root. `Microsoft.WindowsAppSDK.ML` ships a same-named stub (assembly version 0.0.0.0) that used to win the publish copy race; the app csproj now excludes that package's assets (`ExcludeAssets="all"`). If the log ever shows `langid: detector load failed: … Microsoft.ML.OnnxRuntime, Version=1.31.0.0`, check the version of that dll in the publish output first.
