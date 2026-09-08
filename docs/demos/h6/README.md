# H6 narrated walkthrough

[Transcript](transcript.md) · [Captions](captions.srt)

The walkthrough demonstrates archive search, retained details and photograph, confirmation and
cancellation, a deliberately lost committed restore response, same-token retry, and returning the
same record to active search after reload. It uses synthetic records and a generated gemstone
illustration against the real API and disposable SQL. Authentication occurs off camera.

Generate the browser capture from the current client build:

```powershell
npm run build --prefix src/Workbench.Client
npm test --prefix tests/Workbench.BrowserTests -- --config h6-walkthrough.config.ts
```

The capture and timed narration segments are written under ignored
`artifacts/h6/walkthrough/`. Render narration and captions using `scripts/render-h6-walkthrough.ps1`
with a local full FFmpeg executable. The script requires Windows PowerShell's System.Speech and
uses Microsoft Zira's synthetic voice. Supply the actual path of your full FFmpeg installation
(the verification used FFmpeg 7.1); the command below uses an example path:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/render-h6-walkthrough.ps1 -Ffmpeg 'C:/Tools/ffmpeg.exe'
```

The execution-policy override applies only to that process. Generated MP4s remain untracked; generate locally or share
outside Git. Review the video and decode it before treating the media as complete.

The separate `restoration.spec.ts` regressions cover two authenticated sessions, 320px and desktop,
both appearances, keyboard focus and touch targets, archive traversal restoration, pagination
beyond 50 rows, uncertainty/navigation protection, and renewed confirmation after re-archive and
failed recovery reads. These automated checks do not establish collector usability.

The final-source capture passed on 2026-09-07. The 82-second narrated MP4 rendered and decoded
successfully; desktop/mobile frames and caption placement were inspected, and the narration track
was checked for non-silent, unclipped output. An earlier capture setup encountered a transient
photo-processing busy response while another browser run occupied the shared test port; the
isolated rerun passed without application changes.

The [H6 verification record](../../specs/2026-09-07-h6-archive-recovery.md#verification-record)
records the full gate: 451 server tests, 92 client tests, 34 browser scenarios, four migration drills,
published-output checks, and final-source container smoke checks.
