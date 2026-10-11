# Speech models — Vosk tier matrix (English / Japanese / Vietnamese)

MeetVault ships one engine family: **Vosk** — offline, streaming, CPU-only. Settings →
Transcription selects **Language** (`transcription.language`) and **Model size**
(`transcription.size`); the catalog (`src/MeetVault.Core/EngineCatalog.cs`) is the single
source of truth. Earlier engine families and the diarization/VAD pipeline were removed.

## Tiers

| Tier | English | Japanese | Vietnamese |
|---|---|---|---|
| small | `small-en` — `vosk-model-small-en-us-0.15` (~40 MB) | `small-ja` — `vosk-model-small-ja-0.22` (~48 MB) | `small-vi` — `vosk-model-small-vn-0.4` (~32 MB) |
| medium | `medium-en` — `vosk-model-en-us-0.22-lgraph` (~128 MB) | — (no medium model exists) | — (no medium model exists) |
| large | `large-en` — `vosk-model-en-us-0.22` (~1.8 GB) | `large-ja` — `vosk-model-ja-0.22` (~1 GB) | `large-vi` — `vosk-model-vn-0.4` (~78 MB) |

Vosk's own test numbers (word/character error rate, lower is better): English 9.85 → 7.82 →
5.69 for small → medium → large; Japanese 9.52 → 8.40; Vietnamese 15.70 for both.

## Staging

Models live under `models/vosk/<tier>-<lang>/` at the repo root (gitignored; copied into the
publish folder by hand). To stage one (example: the English medium model; use the system
bsdtar — GNU tar cannot read `.zip`):

    curl -L --fail --retry 3 -C - -o /tmp/model.zip https://alphacephei.com/vosk/models/vosk-model-en-us-0.22-lgraph.zip
    mkdir -p models/vosk/_tmp && /c/Windows/System32/tar.exe -xf /tmp/model.zip -C models/vosk/_tmp
    mv models/vosk/_tmp/vosk-model-en-us-0.22-lgraph models/vosk/medium-en && rmdir models/vosk/_tmp

An unstaged tier shows "— not installed" in Settings; Live falls back to an installed model
of the same language. Required files per model are the ones the catalog lists for it
(dynamic-graph models: `graph/Gr.fst` + `graph/HCLr.fst`; composed models: `graph/HCLG.fst`).
A legacy `transcription.model` value is migrated once to the language/size pair (unknown ids
fall back to English/Small). Big models need more RAM (roughly 1–2 GB when running) and take
longer to load; the small models stay the lightest option. Overlay/feed rendering is
unchanged by the model choice.
