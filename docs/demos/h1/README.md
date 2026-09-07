# H1 narrated walkthrough

[Transcript](transcript.md) · [Captions](captions.srt)

Generate `h1-walkthrough.mp4` locally using the commands below. MP4 recordings are ignored by Git;
share verification videos as external attachments instead of committing them.

The video showcases the actual H1 application through Playwright against a fresh, disposable
SQL Server database. Narration uses the local Microsoft Zira synthetic voice. Captions are
burned into a separate band below the application, with an SRT copy provided alongside.
All collection content is fictional demonstration data; authentication happens off camera.
Duration: approximately 2 minutes 20 seconds; MP4, H.264 video with AAC narration, about 3.2 MB.

The walkthrough covers an empty collection, adding a sapphire with notes and a storage location,
reopening and reloading its details, protecting and discarding a draft, recovering an uncertain
pendant save without duplication, appearance choices, Grid/List switching, and a narrow phone viewport. A separate
authenticated session is verified off camera. The lost response is deliberately induced by
Playwright after a real server commit; other persistence calls use the real application unchanged.

H1 supports individual records. Gemstone, jewelry, material, and consumable examples do not imply
implemented category-specific fields, quantity tracking, consumption, or composition workflows.
Photos, search, and editing also remain outside this slice.

## Reproduce on Windows

Use the repository's development prerequisites, installed Playwright Chromium, Docker, and a full
FFmpeg build with H.264, AAC, and subtitle support. FFmpeg is an artifact-generation dependency;
it is not added to the application runtime. For this recording, the Windows FFmpeg 7.1 binary
bundled in `imageio-ffmpeg==0.6.0` was installed into the ignored artifact directory.

From the repository root:

```powershell
npm run build --prefix src/Workbench.Client
./scripts/record-h1-narration.ps1
npm test --prefix tests/Workbench.BrowserTests -- --config demos/playwright.config.ts
python ./scripts/render-h1-video.py --ffmpeg /absolute/path/to/ffmpeg.exe
```

The recording configuration is opt-in and excluded from the ordinary `*.spec.ts` suite. It
asserts the demonstrated outcomes and deliberately pauses for narration pacing. The standard
browser harness provisions and cleans up the disposable database and application. Raw footage,
audio, and timing metadata stay in ignored `artifacts/h1-video`. The finished MP4 stays local;
captions, transcript, and recording source remain tracked. The renderer checks the finished file
by decoding its entire video and audio streams. The recorded scenario passed; ordinary browser
discovery reports 16 tests after the gallery refinement. Representative creation, retry, and mobile frames
were visually inspected, and the narration's measured peak remained below clipping.
