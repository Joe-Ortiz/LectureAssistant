# Lecture Assistant

A Windows desktop app (Microsoft Store, .NET 10 + WinUI 3) that helps instructors turn lecture recordings into captioned, interactive video quizzes.

1. **Import** a lecture video.
2. **Captions** are generated on the instructor's PC with Whisper, then reviewed and edited in the app.
3. **Questions** are suggested from the timestamped transcript by Claude (instructor's own API key) or a local model, then reviewed and edited on the video timeline.
4. **Publish**: the instructor uploads the video to YouTube (unlisted) with the caption file, pastes the link, and exports:
   - a **SCORM 1.2 package** for Canvas, Moodle, Blackboard or D2L. The LMS identifies the student and records the score in the gradebook; no server needed.
   - an **H5P Interactive Video** (`.h5p`) for schools with an H5P platform.

## Why these choices

| Decision | Reason |
|---|---|
| YouTube for video | No streaming or hosting costs. Captions must live on YouTube: H5P can't overlay its own caption tracks on YouTube videos, and YouTube doesn't reliably pass automatic captions to embedded players. |
| Paste a YouTube link (no in-app upload yet) | Uploading through the YouTube Data API requires Google's OAuth verification and API compliance audit. Unaudited projects' uploads are locked to private, and quota is shared by every install. Add it once the audit is done. |
| SCORM as the primary export | Canvas's SCORM tool gives real student identity and gradebook passback with zero infrastructure. LTI 1.3 is the long-term upgrade (see Roadmap). |
| Local Whisper (Whisper.net) | Free, private, offline. Uses the GPU through Vulkan when present. Models download on first use (they're too large for the MSIX). |
| Audio via Media Foundation | Windows decodes the video; no FFmpeg to bundle or license. |
| Claude BYOK or local LLM | A Store app can't safely contain an API key. Instructors bring their own Anthropic key (stored in Windows Credential Locker) or run a GGUF model locally via LLamaSharp, with grammar-constrained JSON output. |

## Solution layout

```
src/
  LectureAssistant.Core                Models, interfaces, caption formats (VTT/SRT), YouTube URL parsing, project storage
  LectureAssistant.Transcription       Whisper.net transcriber + model downloads
  LectureAssistant.QuestionGeneration  Claude and local-LLM question generators, prompt, post-processing
  LectureAssistant.Export              SCORM 1.2 (with a YouTube-based quiz player) and H5P exporters
  LectureAssistant.App                 WinUI 3 app (MSIX packaged)
tests/                                 xUnit tests per library
```

## Build and run

Requires the .NET 10 SDK on Windows 10 1809+ (Windows 11 recommended).

```bash
dotnet test
```

```bash
dotnet build src/LectureAssistant.App -p:Platform=x64
```

To debug the packaged app, open `LectureAssistant.slnx` in Visual Studio 2026 with the "WinUI application development" workload, set `LectureAssistant.App` as the startup project, and press F5.

## Store submission checklist

- Associate the app with your Partner Center reservation (updates `Package.appxmanifest` Identity/Publisher).
- Replace the placeholder logos in `src/LectureAssistant.App/Assets`.
- Add a privacy policy covering: video stays local; the transcript is sent to Anthropic only when Claude is selected; the API key is stored in Windows Credential Locker.
- Package for x64 and ARM64 (`dotnet publish` with MSIX, or Visual Studio → Package and Publish).

## Roadmap

- YouTube Data API: upload the video and captions from the app (after Google verification and audit).
- LTI 1.3 tool (hosted) with Deep Linking and Assignment and Grade Services, for a smoother Canvas experience than SCORM.
- Optional hosted question-generation backend with a Store subscription, for instructors without an API key.
- Caption editor features: split/merge cues, find and replace across the transcript.
