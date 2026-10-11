# ASR engines — models, staging, behavior

MeetVault's Live pipeline supports four engine families, selectable in Settings →
Transcription → Engine (keys `transcription.engine` + `transcription.model`). All run
on-device; exactly one engine is active at a time (owner directive: no concurrent
multi-language engines, no translation).

## Model table

| Engine | Model id | Language | Decode style | Staged size | Source |
|---|---|---|---|---|---|
| Whisper (default) | `tiny` → `ggml-tiny.bin` | multilingual (auto en/ja/vi) | per VAD utterance | 77.7 MB | whisper.cpp GGML (existing installs; downloaded by the app) |
| Whisper | `base` → `ggml-base.bin` | multilingual | per VAD utterance | 148.0 MB | same |
| Moonshine | `tiny-en` | English | per VAD utterance (≤8 s chunks) | 38 MB | sherpa-onnx release `asr-models`, `sherpa-onnx-moonshine-tiny-en-quantized-2026-02-27.tar.bz2` |
| Moonshine | `tiny-ja` | Japanese | per VAD utterance (≤8 s chunks) | 65 MB | `…-tiny-ja-quantized-2026-02-27.tar.bz2` |
| Moonshine | `base-en`, `base-ja` (catalog rows; not staged here) | en / ja | per VAD utterance | ~111 / ~104 MB | `…-base-{en,ja}-quantized-2026-02-27.tar.bz2` |
| Zipformer | `20m-en` | English | streaming (native partials) | 130 MB | `sherpa-onnx-streaming-zipformer-en-20M-2023-02-17.tar.bz2` |
| Vosk | `small-en` | English | streaming (partials) | 50 MB | Vocab `vosk-model-small-en-us-0.15.zip`, alphacephei.com/vosk/models |
| Vosk | `small-ja` | Japanese | streaming (partials) | 62 MB | `vosk-model-small-ja-0.22.zip` |

Moonshine/Zipformer/Vosk are fixed-language: a mixed-language meeting needs Whisper.
"Moonshine Small" is not a shipped sherpa tier — the middle tier is `base`.

## Staging (gitignored; never commit)

```sh
cd D:/workspace/MeetVault/models
mkdir -p moonshine zipformer vosk
# downloads (GitHub release asr-models of k2-fsa/sherpa-onnx; Vosk from alphacephei.com):
#   sherpa-onnx-moonshine-tiny-en-quantized-2026-02-27.tar.bz2   29.9 MB
#   sherpa-onnx-moonshine-tiny-ja-quantized-2026-02-27.tar.bz2   47.9 MB
#   sherpa-onnx-streaming-zipformer-en-20M-2023-02-17.tar.bz2   127.9 MB
#   vosk-model-small-en-us-0.15.zip                              41.2 MB
#   vosk-model-small-ja-0.22.zip                                 49.7 MB
# extract with the SYSTEM bsdtar (git-bash tar may lack bzip2):
/c/Windows/System32/tar.exe -xf <archive>       # handles .tar.bz2 and .zip
# layout the catalog requires (EngineCatalog.RequiredFiles):
#   moonshine/tiny-en/{encoder_model.ort, decoder_model_merged.ort, tokens.txt}
#   moonshine/tiny-ja/{…same…}
#   zipformer/20m-en/{encoder,decoder,joiner}-epoch-99-avg-1.int8.onnx + tokens.txt
#   vosk/small-en/{am/final.mdl, conf/model.conf, graph/HCLr.fst, graph/Gr.fst, ivector/final.ie}
#   vosk/small-ja/{…same…}
```

Vosk small models ship the **dynamic-graph** layout (`graph/HCLr.fst` + `graph/Gr.fst`,
composed at load) — there is NO `HCLG.fst`; the catalog checks the files that exist.
`libvosk.dll` ships through the `Vosk` NuGet package (verified in publish output).

Publish note: the portable build needs the staged tree copied in —
`cp -r models publish/win-x64/models` (the exe resolves models from its own folder).
Any option whose files are absent shows "— not installed" in the picker; selecting it
falls back to whisper tiny with a `live: engine <engine>/<model> model files missing`
warning.

## Streaming vs per-utterance behavior

- Whisper and Moonshine run per VAD utterance (silero segmentation → one decode per
  utterance); diarization works exactly as before (one speaker per utterance).
- Zipformer and Vosk consume the raw 16 kHz stream through `StreamingRunner` (bounded
  1024-chunk queue ≈10 s, dedicated decode thread, drop-oldest with a rate-limited warn).
  They emit interim text (`PartialReady` → Live partial line + overlay partial caption)
  and finalized utterances on the engine's own endpoints (`SegmentReady` → the session's
  normal segment path).
- Streaming engines always run **single-speaker**: the diarization switch is ignored for
  them and the Live engine line says "(streaming)". Online diarization is out of scope.
- Streaming timestamps are wall-clock (utterance start = previous endpoint, end = now).

## Engine-specific facts (measured on this box, 2026-10-11)

- **Moonshine v2 decode cap**: the 2026-02-27 quantized exports fail beyond ~9.2 s of
  audio per decode (ORT broadcast error in the decoder attention → empty result;
  reproduced on tiny-en AND tiny-ja, int8 and float files alike). `SherpaOfflineTranscriber`
  splits longer utterances at the quietest 50 ms frame within 1.5 s before each 8 s
  boundary (`AudioWindows.Split`) — every chunk ≤8 s. Measured speed: 4.5 s utterance →
  ~62-69 ms per decode (vs whisper tiny ~1.0-1.1 s, whisper base ~2.1-2.3 s).
- **Zipformer 20M onset**: the model loses the head of speech that lands in the first
  ~2 s of a stream (reproduced on its own test wav; the official dotnet example's 0.3 s
  pad is too short here). `ZipformerStreamingBackend.Prime()` feeds 1.5 s of leading
  silence before the decode thread starts (below Rule1 so no early endpoint fires).
  Endpoint tuning (`ZipformerStreamingBackend`): Rule1 2.0 s (silence, nothing decoded),
  Rule2 1.0 s (after speech), Rule3 15 s (hard cap). Output is UPPERCASE — that is the
  model's documented voice (its `test_wavs/trans.txt` is uppercase too).
- **Vosk** output is lowercase; it needs no preroll. `AcceptWaveform` decodes internally
  (`IsReady() => false` — a `true` there would spin the runner's drain loop).
- **Streaming segment ids** are minted as `s<random8>-<n>` per runner instance: the
  `transcript_segments.id` column is a global PRIMARY KEY, so parallel meetings/engines
  must never both start at `s0` (a live run hit exactly that UNIQUE violation and could
  not save or stop until the prefix was added).

## faster-whisper

Assessed and deliberately NOT integrated: CTranslate2 + Python, no .NET binding. Revisit
only if CPU inference of larger Whisper models becomes the bottleneck (details in the
2026-10-11 asr-engine-expansion plan appendix).
