# Speech models — Vosk (Japanese / Vietnamese)

MeetVault ships one engine family: **Vosk** — offline, streaming, CPU-only. Settings →
Transcription selects **Language** (`transcription.language`) and **Model size**
(`transcription.size`); the catalog (`src/MeetVault.Core/EngineCatalog.cs`) is the single
source of truth. Earlier engine families and the diarization/VAD pipeline were removed.

**The deliverable ships exactly two models — `large-ja` and `large-vi`** (plus the language-ID
model). English and the small/medium tiers were dropped to keep the portable zip near 1 GB
instead of 3.3 GB; there is no runtime download path, so the catalog lists only what ships and
`tools/pack-portable.ps1` stages only that.

## Tiers

| Tier | Japanese | Vietnamese |
|---|---|---|
| large | `large-ja` — `vosk-model-ja-0.22` (~1 GB; 1.3 GB staged) | `large-vi` — `vosk-model-vn-0.4` (~78 MB; 103 MB staged) |

Vosk's own test numbers (word/character error rate, lower is better): Japanese 8.40 (CER),
Vietnamese 15.70 (WER) — the large tier of each.

Retired and **not shipped**: `small-en`, `medium-en`, `large-en`, `small-ja`, `small-vi`.
A stored setting that names one of them resolves to the shipped default (`ja`/`large`) rather
than to a model that is not on disk.

## Staging

Models live under `models/vosk/<tier>-<lang>/` at the repo root (gitignored). `tools/pack-portable.ps1`
stages the shipped two into the publish folder; to stage a model by hand (use the system bsdtar —
GNU tar cannot read `.zip`):

    curl -L --fail --retry 3 -C - -o /tmp/model.zip https://alphacephei.com/vosk/models/vosk-model-vn-0.4.zip
    mkdir -p models/vosk/_tmp && /c/Windows/System32/tar.exe -xf /tmp/model.zip -C models/vosk/_tmp
    mv models/vosk/_tmp/vosk-model-vn-0.4 models/vosk/large-vi && rmdir models/vosk/_tmp

Staging a model the catalog does not list does nothing for the deliverable: the
`TrimUnshippedModels` target in `src/MeetVault/MeetVault.csproj` deletes it from the publish
output.

The auto-detect language-ID model lives under `models/langid/`: `voxlingua107.onnx`
(86,657,056 B) and `lang_map.json`, from the SpeechBrain VoxLingua107 ECAPA checkpoint
exported to ONNX (`christopherthompson81/voxlingua107-lid-onnx`). It is staged by hand like
the Vosk models (gitignored, not committed); without it Auto-detect is unavailable and
meetings fall back to the configured language.

An unstaged tier shows "— not installed" in Settings; Live falls back to an installed model
of the same language. Required files per model are the ones the catalog lists for it
(dynamic-graph models: `graph/Gr.fst` + `graph/HCLr.fst`; composed models: `graph/HCLG.fst`).
A legacy `transcription.model` value is migrated once to the language/size pair (unknown ids
fall back to English/Small). Big models need more RAM (roughly 1–2 GB when running) and take
longer to load; the small models stay the lightest option. Overlay/feed rendering is
unchanged by the model choice.
