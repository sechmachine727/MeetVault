# MeetVault WinUI 3 MVP — Risks and Ponytail Notes

Audience: Windows implementer.

## 1. Risks

- sherpa-onnx diarization is offline and gender has no validated drop-in model: validate clustering on target languages, noise, and overlap; auto gender stays opt-in estimate with honest `Unknown` fallback, neutral IDs plus manual rename guaranteed.
- Per-app capture is WASAPI process loopback (min build 20348) wrapped from the Microsoft sample; block with guidance rather than silently falling back to full endpoint loopback.
- Long-session timestamps and chunked-write memory need synthetic 60-minute tests before release claims.
- Gemini: model `gemini-3.8-flash` pinned; enforce 2 GB per file until quota confirms otherwise; confirm pricing on the build machine. .NET SDK 1.24.0 lacks an Interactions client, so interactions ship over `HttpClient`.

## 2. Ponytail (what was deliberately left out)

- No login, subscription, cloud sync, auto-upload, biometric identity, guaranteed auto gender, or second platform. No abstraction with one implementation beyond the engine interfaces that carry a named replacement (diart reference, model swaps). No settings that are not wired. No blobs in SQLite. Skipped: custom diarizer training, server-side processing, multi-app simultaneous capture. Add when a locked requirement demands them.
