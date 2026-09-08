# H7 narrated walkthrough

[Transcript](transcript.md) · [Captions](captions.srt)

The walkthrough demonstrates explicit export scope, search independence, complete preparation,
a real CSV download, reversible spreadsheet protection, navigation and appearance retention,
a deliberately interrupted request, and a new snapshot including archived records on retry.
It uses synthetic text records against the real API and disposable SQL. Authentication occurs off camera.
The network interruption is injected by Playwright; successful downloads use the real server.

Generate the capture from the current client build:

```powershell
npm run build --prefix src/Workbench.Client
npm test --prefix tests/Workbench.BrowserTests -- --config h7-walkthrough.config.ts
```

Capture and timed narration segments are written under ignored `artifacts/h7/walkthrough/`.
Render narration and captions with a local full FFmpeg installation (the path below is an example):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/render-h7-walkthrough.ps1 -Ffmpeg 'C:/Tools/ffmpeg.exe'
```

The process-scoped execution-policy override permits the renderer to use Windows PowerShell
System.Speech and Microsoft Zira's synthetic voice. The renderer produces
`artifacts/h7/walkthrough/h7-collection-export.mp4`, checks that it decodes, and writes
`transcript.md` and `captions.srt` beside this document. Review the resulting video, frames,
and narration before treating media as complete. Generated MP4s remain untracked; share outside Git.

The separate `export.spec.ts` regressions cover both scopes, more than 50 seeded active records,
archived exclusion/inclusion, independently parsed CSV text, search independence, keyboard selection,
320px and desktop layouts, both appearances, reduced motion/transparency, real mobile downloads,
navigation retention, reload clearing, and injected empty/failure/disconnection outcomes with retry, plus cancellation of a held complete response.
Server and client unit/integration tests cover limits, tenant isolation, consistency, cancellation,
session loss, and in-memory expiry separately. Automated evidence does not establish collector usability.

The capture passed on 2026-09-08. The 97-second narrated MP4 rendered and decoded with FFmpeg
9.0.1; desktop and mobile frames and caption placement were inspected. Audio analysis measured
-21.2 dB mean and -2.4 dB peak, confirming non-silent output without clipping. The four focused
export browser regressions also passed against the real API and disposable SQL.

See the [verification record](verification.md) for complete gate results and coverage limits.
