# Trilingual audio validation notes (Observed)

Clip: `C:/Users/sechmachine/Documents/Sound Recordings/Recording.m4a` (352,458 bytes).
Decoded direct via `MediaFoundationReader` + `MediaFoundationResampler` to 16 kHz mono in memory (no intermediate WAV file): 439,638 PCM bytes = 13.74 s, fed to the existing `WhisperTranscriber.TranscribeWindowAsync` path in 2 x 10 s windows. No DECODE-FAILED.

Models: `models/ggml-base.bin` (147,951,465 bytes), `models/ggml-tiny.bin` (77,691,713 bytes); diarizer `models/sherpa-seg.onnx` + `models/sherpa-emb.onnx`.

## Per-model results

Base: 5 segments, 23.77 s wall, RTF 1.73. Window langs (constrained set en/ja/vi): w0 `en` 0.426, w1 `en` 0.3288.

- w0 0-8000 ms: `This is an audio test file. I will speak English Vietnamese and Japanese in this. Is that okay?`
- w0 8000-10000 ms: `[Music]`
- w1 10000-13000 ms: `I'll come.` x3 (one segment per second, 10-11 s / 11-12 s / 12-13 s)

Tiny: 3 segments, 15.14 s wall, RTF 1.102. Window langs: w0 `en` 0.186, w1 `en` 0.3929.

- w0 0-8000 ms: `Ini adalah nowo deo test following. I will speak English, Vietnamese, Japanese, in this. It's that okay.`
- w0 9000-12000 ms: `Tegai nahi, kalau kita bisa menemukan.`
- w1 10000-12000 ms: `for the first time.`

## Language-switch evidence

None. All 4 window detections returned `en` (probabilities 0.19-0.43, low — consistent with music/sung content). The clip's non-English portion is sung (`[Music]` / `I'll come`), not spoken Japanese/Vietnamese, so this file does not exercise the ja/vi path despite the speaker's English intro announcing three languages. Tiny's w0 second segment looks like cross-language hallucination under music, but its window detection still reported `en`.

## Diarizer labels (single speaker)

- whole-clip window: `spk_1`
- 10 s window w0: `spk_2`
- 10 s window w1: `spk_2`

Windowed labels are mutually consistent (`spk_2`); the whole-clip label differs (`spk_1`) because each `Process()` call re-clusters independently. Labels are stable only within the same scope.

## Limits

RTF > 1 on CPU (base 1.73, tiny 1.10); file-level probe only, not streaming/VAD. Full JSON: `tools/TrilingualProbe/results.json` (scratch, removed after capture).
