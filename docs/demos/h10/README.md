# H10 shared acquisition walkthrough

The reproducible Playwright walkthrough connects three independently recorded stones to one
acquisition, demonstrates recovery after a lost connection response, opens the shared piece list,
corrects common notes, removes a mistaken connection, and inspects retained read-only context
on an archived piece. It uses synthetic data and authenticates off camera.

Generate from current source with output outside the repository:

```powershell
$env:WORKBENCH_H10_MEDIA = 'C:/Users/mcuni/.codex/visualizations/2026/09/10/01a08a18-209c-7912-8ffa-aa2ac6cd2ac9/h10'
npm run build --prefix src/Workbench.Client
npm test --prefix tests/Workbench.BrowserTests -- --config shared-acquisition-walkthrough.config.ts
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/render-h10-walkthrough.ps1 -Ffmpeg '<local ffmpeg executable>' -MediaRoot $env:WORKBENCH_H10_MEDIA
```

The renderer uses Windows System.Speech with Microsoft Zira Desktop and a local FFmpeg
installation. It measures narration duration, writes a transcript and captions here, and
decodes the final `h10-shared-acquisition.mp4` to check the render. Videos, audio and screenshots
remain in the external media directory and are never committed.

The regular `shared-acquisition.spec.ts` suite covers shared context, collection navigation,
archive visibility, removal, lost responses and narrow appearance/keyboard recovery. Server
and client suites provide additional persistence, concurrency, isolation and draft checks.
Automated checks do not establish uncoached collector usability. H11 documents and H12 export
changes remain outside this delivery.
