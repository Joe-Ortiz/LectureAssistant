# Lecture Assistant

A Windows desktop app (Microsoft Store, .NET 10 + WinUI 3) that helps instructors turn lecture recordings into captioned, interactive video quizzes.

1. **Import** a lecture video.
2. **Captions** are generated on the instructor's PC with Whisper, then reviewed and edited in the app.
   - The instructor picks a **subject area** (remembered for the next lecture). Each subject has an example sentence full of correctly spelled terms that Whisper reads as preceding context, followed by terms from the instructor's **personal dictionary** (most recently used first, kept within Whisper's prompt limit). See `SubjectAreas.cs` and `RecognitionPrompt.cs`.
   - **Fix misheard words** lists words to check: words Whisper gave low confidence (from its per-token probabilities), then names, terms and acronyms. Each can be replaced everywhere at once and remembered. Remembered fixes (e.g. "cooper netties" → "Kubernetes") are applied automatically to future lectures. The dictionary is stored in `dictionary.json` in the app's data folder and managed in Settings.
3. **Questions** are suggested from the timestamped transcript by an AI model the app downloads and runs on the PC. Instructors can choose Claude with their own API key instead. The questions are then reviewed and edited on the video timeline.
4. **Publish**: the instructor uploads the video to YouTube (unlisted) with the caption file, pastes the link, and exports:
   - a **SCORM 1.2 package** for Canvas, Moodle, Blackboard or D2L. The LMS identifies the student and records the score in the gradebook; no server needed.
   - an **H5P Interactive Video** (`.h5p`) for schools with an H5P platform.

## Quiz settings

Set in step 3 for the whole lecture: passing score, whether students may skip past unanswered questions, whether the correct answer and explanation are shown, and retries:

- **Attempts allowed**: 1 (no retries), 2 to 5, or unlimited.
- **When a retry is correct**: full credit (any attempt within the limit earns full points), reduced credit (each retry loses a set percentage of the points, 50% by default, never below zero), or only the first attempt counts (retries are practice).

Each question can override the attempts and the retry scoring; it uses the quiz setting by default. The score sent to the LMS is points earned ÷ total points. A question counts as answered after its first attempt. Students can retry a wrong answer right away or later from its marker on the video timeline, and the correct answer stays hidden while another try could still earn points. `AttemptRules` (Core) and `creditPercent` in `player.js` hold the same credit rules.

H5P question types only support retries on or off. A question with more than one attempt gets unlimited retries there, H5P can't reduce credit for retries, and the H5P platform decides how a retried question is scored. The app warns before exporting an `.h5p` when the settings can't be matched.

Projects saved before attempts were configurable load with their old on/off retry switch: on becomes unlimited attempts where only the first counts, and off becomes one attempt.

## Why these choices

| Decision | Reason |
|---|---|
| YouTube for video | No streaming or hosting costs. Captions must live on YouTube: H5P can't overlay its own caption tracks on YouTube videos, and YouTube doesn't reliably pass automatic captions to embedded players. |
| Paste a YouTube link (no in-app upload yet) | Uploading through the YouTube Data API requires Google's OAuth verification and API compliance audit. Unaudited projects' uploads are locked to private, and quota is shared by every install. Add it once the audit is done. |
| SCORM as the primary export | Canvas's SCORM tool gives real student identity and gradebook passback with zero infrastructure. LTI 1.3 is the long-term upgrade (see Roadmap). |
| Local Whisper (Whisper.net) | Free, private, offline. Uses the GPU through Vulkan when present. Models download on first use (they're too large for the MSIX). |
| Audio via Media Foundation | Windows decodes the video; no FFmpeg to bundle or license. |
| Local model by default, Claude optional | Works with no account and nothing leaves the PC. The app picks a model for the PC's hardware, downloads it once (resumable, SHA-256 verified), and runs it with LLamaSharp on the GPU through Vulkan. If video memory is short it uses a smaller context window (more, shorter transcript sections), then falls back to the CPU automatically, and output is grammar-constrained JSON. Instructors who want the best quality can use their own Anthropic API key instead, stored in Windows Credential Locker; a Store app can't safely contain one of ours. |
| Progress counted in questions | Both generators stream their JSON and count questions as each one closes, so the bar shows real progress ("Writing question 3 of 8") with a rough time left once one is done. The local model's bar also includes loading the model (llama.cpp's load progress) and reading each transcript section, paced by measured speed. Claude's thinking time can't be predicted, so its bar is indeterminate until it starts writing. |

### Built-in local models

| Choice | Model | Size | Recommended when |
|---|---|---|---|
| Standard | Qwen 3.5 4B (Q4_K_M) | 2.7 GB | Default; runs on CPU-only laptops with 8 GB RAM |
| Best quality | Qwen 3.5 9B (Q4_K_M) | 5.7 GB | A GPU with at least 8 GB of video memory |

Both are Apache 2.0 licensed (Qwen team, Alibaba Cloud). Files are downloaded from Hugging Face, pinned to a specific commit in `LocalModelCatalog.cs`. Measured on an RTX 3070 with a 4-minute lecture and 6 questions: Standard took 19 s and Best took 34 s after the one-time GPU warm-up. To add or swap models, add an entry to `LocalModelCatalog` with its commit-pinned URL, byte size and SHA-256. Check first that the bundled LLamaSharp (llama.cpp) version supports the model's architecture.

### Where does the model run: graphics card or processor?

Settings says so before anything runs, and again after each run:

- **Before**: a line such as "Runs on your graphics card (NVIDIA GeForce RTX 3070, 8 GB video memory). Fast." or "Runs on the processor using system memory (16 GB) … Slower: expect a few minutes per lecture." It's predicted (`DeviceForecasts`) from the graphics card and memory Windows reports, the model's memory needs (estimated from the file size for a custom `.gguf`) and the GPU layers setting (0 = processor). Built-in graphics (Intel UHD/Iris, Ryzen Radeon) share system memory, so for those it says "if it can, otherwise the processor" and promises no speed.
- **During and after**: once a model loads, the app reads the runtime's log (llama.cpp's "offloaded 37/37 layers to GPU", whisper.cpp's "using Vulkan0 backend") and which native build loaded (`GgmlLog`). While questions are written it shows "Running on your graphics card (…)" or "Running on the processor (no graphics card was usable)" under the progress bar. The finished message for captions and questions says where they ran and how long it took, and Settings shows "Last run: Standard model on your graphics card (…), 34 seconds."

A question model goes onto the graphics card whole or not at all. If the card can't hold it or the driver fails, the app retries on the processor automatically and says so ("the graphics card couldn't run it").

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
- Caption editor features: split/merge cues.
