# Speech models — Vosk (English / Japanese)

MeetVault ships one engine family: **Vosk** — offline, streaming, CPU-only. Settings →
Transcription selects **Language** (`transcription.language`) and **Model size**
(`transcription.size`); the catalog (`src/MeetVault.Core/EngineCatalog.cs`) is the single
source of truth. Earlier engine families and the diarization/VAD pipeline were removed.

**The deliverable ships exactly two models — `large-en` and `large-ja`** (plus the language-ID
model). Vietnamese and the small/medium tiers are not shipped; there is no runtime download
path, so the catalog lists only what ships and `tools/pack-portable.ps1` stages only that. With
both large models staged the staged model payload is ~4.3 GB and the portable zip is ~3.0 GB.

## Tiers

| Tier | English | Japanese |
|---|---|---|
| large | `large-en` — `vosk-model-en-us-0.22` (~1.8 GB; 2.4 GB staged) | `large-ja` — `vosk-model-ja-0.22` (~1 GB; 1.3 GB staged) |

Vosk's own test numbers (word/character error rate, lower is better): English 5.69 (WER),
Japanese 8.40 (CER) — the large tier of each.

**Not shipped**: the small/medium tiers (`small-en`, `medium-en`, `small-ja`) and every Vietnamese
model (`small-vi`, `large-vi`). A stored setting that names one of them resolves to the shipped
default (`en`/`large`) rather than to a model that is not on disk.

## Staging

Models live under `models/vosk/<tier>-<lang>/` at the repo root (gitignored). `tools/pack-portable.ps1`
stages the shipped two into the publish folder; to stage a model by hand (use the system bsdtar —
GNU tar cannot read `.zip`):

    curl -L --fail --retry 3 -C - -o /tmp/model.zip https://alphacephei.com/vosk/models/vosk-model-ja-0.22.zip
    mkdir -p models/vosk/_tmp && /c/Windows/System32/tar.exe -xf /tmp/model.zip -C models/vosk/_tmp
    mv models/vosk/_tmp/vosk-model-ja-0.22 models/vosk/large-ja && rmdir models/vosk/_tmp

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
fall back to English/Large, the shipped default). The large models need more RAM (roughly 1–2 GB when running) and take
several seconds to load; the retired small models stay the lightest option. Overlay/feed rendering is
unchanged by the model choice.
