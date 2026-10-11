# Speech model — Vosk small (English / Japanese / Vietnamese)

MeetVault ships exactly one engine family: **Vosk small** — offline, streaming, CPU-only.
The model is selected in Settings → Transcription → Model (`transcription.model`); the
catalog (`EngineCatalog`) is the single source of truth. Earlier engine families
(whisper, moonshine, zipformer) and the sherpa diarization/VAD pipeline were removed —
their code, packages and models are gone.

## Model table

| Model id | Language | Staged folder | Staged size | Source |
|---|---|---|---|---|
| `small-en` (default) | English | `vosk/small-en` | ~50 MB | `vosk-model-small-en-us-0.15.zip`, alphacephei.com/vosk/models |
| `small-ja` | Japanese | `vosk/small-ja` | ~62 MB | `vosk-model-small-ja-0.22.zip` |
| `small-vi` | Vietnamese | `vosk/small-vi` | ~50 MB | `vosk-model-small-vn-0.4.zip` |

One language per meeting (the model is fixed-language). A missing/unstaged model shows
"— not installed" in the picker; if the selected one is missing, Live falls back to any
installed model (`live: model <id> missing; falling back to <id>` in the log) and runs
demo mode only when none is staged. A legacy `transcription.model` value from a removed
engine (tiny, base, 20m-en, …) resolves to the default with no migration.

## Staging (gitignored; never commit)

```sh
cd <repo>/models
mkdir -p vosk
# downloads:
#   https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip   39.3 MB
#   https://alphacephei.com/vosk/models/vosk-model-small-ja-0.22.zip      47.4 MB
#   https://alphacephei.com/vosk/models/vosk-model-small-vn-0.4.zip       32.1 MB
# extract each with the SYSTEM bsdtar (it handles .zip; strip the top folder):
/c/Windows/System32/tar.exe -xf vosk-model-small-en-us-0.15.zip -C vosk/small-en --strip-components 1
# layout the catalog requires (EngineCatalog.RequiredFiles):
#   vosk/<model>/am/final.mdl
#   vosk/<model>/conf/model.conf
#   vosk/<model>/graph/HCLr.fst + graph/Gr.fst     (dynamic graph — NO HCLG.fst)
#   vosk/<model>/ivector/final.ie
```

Publish note: the portable build needs the staged tree copied in —
`cp -r models publish/win-x64/models` (the exe resolves models from its own folder).

## Behavior

- The engine consumes the raw 16 kHz stream through `StreamingRunner` (bounded 1024-chunk
  queue ≈10 s, dedicated decode thread, drop-oldest with a rate-limited warn). It emits
  interim text (`PartialReady` → a NORMAL transcript row in the Live list, updated in
  place and replaced by its final — never a special caption; the overlay keeps its dimmed
  partial line) and finalized utterances on Vosk's own silence endpoints (`SegmentReady` →
  the session's normal segment path).
- Single-speaker by construction: every segment carries the constant `spk_1` id and no
  speaker rows are minted. Legacy meetings with speaker rows still render them read-only.
- Timestamps are wall-clock (utterance start = previous endpoint, end = now).
- **Sentence case**: the small models emit all-lowercase, unpunctuated text; every
  partial and final passes `SpeechText.SentenceCase` (lowercase + first-letter capital +
  the English pronoun I), so every surface reads as sentence case.
- **`IsReady() => false`**: Vosk decodes inside `AcceptWaveform`; a `true` there would
  spin the runner's drain loop.
- The engine line in Live reads `live: vosk <model>`.

## Measured facts (this box, 2026-10-11)

- Vosk small decodes faster than real time on CPU and needs no preroll or VAD.
- The small models ship the dynamic-graph layout (`graph/HCLr.fst` + `graph/Gr.fst`,
  composed at load); there is no `HCLG.fst`. `libvosk.dll` ships through the `Vosk`
  NuGet package.
- Streaming segment ids are minted as `s<random8>-<n>` per runner instance: the
  `transcript_segments.id` column is a global PRIMARY KEY, so parallel meetings/engines
  must never both start at `s0`.
