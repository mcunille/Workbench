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
uses Microsoft Zira's synthetic voice. Generated MP4s remain untracked; generate locally or share
outside Git. Review the video and decode it before treating the media as complete.

The separate `restoration.spec.ts` regressions cover two authenticated sessions, 320px and desktop,
both appearances, keyboard focus and touch targets, archive traversal restoration, pagination
beyond 50 rows, uncertainty/navigation protection, and renewed confirmation after re-archive and
failed recovery reads. These automated checks do not establish collector usability.

Verification evidence will be recorded after the integrated gates and media inspection finish.
