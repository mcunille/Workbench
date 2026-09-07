# H2 narrated walkthrough

[Watch or download the narrated video](h2-walkthrough.mp4) · [Transcript](transcript.md) · [Captions](captions.srt)

The recording source exercises the real photo workflow against a disposable SQL Server database and private local blob storage. Authentication happens off camera. The gemstone illustrations are synthetic demonstration fixtures, not photographs of real possessions. Narration uses Microsoft Zira, a synthetic voice.

The walkthrough shows local image preparation and preview, explicit upload, persistence after reload and in another authenticated session, Grid/List display, a replacement whose response is deliberately lost after the real commit, safe retry, mobile appearance, and confirmed removal.

## Reproduce on Windows

Use the repository development prerequisites, Playwright Chromium, Docker, and an FFmpeg build with H.264, AAC, and subtitle support. From the repository root:

```powershell
npm run build --prefix src/Workbench.Client
./scripts/record-h1-narration.ps1 -Scenario h2
npm test --prefix tests/Workbench.BrowserTests -- --config demos/h2.config.ts
python ./scripts/render-h1-video.py --scenario h2 --ffmpeg /absolute/path/to/ffmpeg.exe
```

The existing scripts retain H1 as their default. H2 recording is opt-in and excluded from the ordinary browser suite. Raw footage, narration, and timing metadata are written to ignored `artifacts/h2-video`. The renderer writes `h2-walkthrough.mp4`, captions, and a timestamped transcript here and decodes the entire result to check video/audio integrity. The recorded scenario passed against the real application at `http://127.0.0.1:4179`; the harness removed its disposable database and storage afterward. The two H2 browser tests also passed. Desktop/light, 320px/dark, gallery, preview, retry, phone, and removal frames were visually inspected. The finished MP4 is 1 minute 49 seconds and 2.7 MB, with complete audio/video decoding verified and a measured audio peak of -0.9 dBFS.

The actual Chromium preparation run resized the synthetic 4,096 × 3,072 PNG from 12,725,643 bytes to a 2,048 × 1,536 WebP of 56,552 bytes. This demonstrates the local preparation and smaller upload boundary; the synthetic fixture's compression ratio is not a claim about typical camera photographs.
