# H3 narrated walkthrough

[Transcript](transcript.md) · [Captions](captions.srt)

Generate `h3-walkthrough.mp4` locally using the commands below. MP4 recordings are ignored by Git;
share verification videos as external attachments instead of committing them.

The recording exercises the real search API and UI against disposable SQL Server and private
local blob storage. Authentication happens off camera. The records and gemstone illustration are
synthetic demonstration fixtures; narration uses Microsoft Zira, a synthetic voice.

It shows case-insensitive notes search, Grid/List presentation with a thumbnail, mobile details
and return-state restoration, appearance switching, an injected search failure and explicit retry,
and clearing a no-match search without modifying records. Automated browser regressions separately
exercise later-page results and position restoration at 320px and 1280px.

## Reproduce on Windows

Use the repository prerequisites, Playwright Chromium, Docker, and FFmpeg with H.264, AAC,
and subtitle support. From the repository root:

```powershell
npm run build --prefix src/Workbench.Client
./docs/demos/record-narration.ps1 -Scenario h3
npm test --prefix tests/Workbench.BrowserTests -- --config demos/h3.config.ts
python ./docs/demos/render-video.py --scenario h3 --ffmpeg /absolute/path/to/ffmpeg.exe
```

Recording is opt-in and excluded from the ordinary browser suite. Raw footage, narration, and
timing metadata are written to ignored `artifacts/h3-video`. The renderer produces the MP4,
captions, and timestamped transcript here and decodes the complete result to check media integrity.

The recorded scenario passed at `http://127.0.0.1:4179`; the harness removed its disposable
SQL database and storage afterward. The finished MP4 is 69 seconds and approximately 1.6 MB.
Desktop/light, mobile/dark, search, retry recovery, and cleared-result frames were visually
inspected. Complete audio/video decoding passed; narration peaks at -1.2 dBFS.
