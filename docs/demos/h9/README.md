# H9 narrated acquisition walkthrough

The reproducible walkthrough creates a synthetic collection item, records an inheritance with a
known year, injects a lost success response, retries the original request, retrieves the saved
context after reload, corrects it to a gift on a narrow dark layout, and confirms that archiving
retains readable context. Authentication occurs off camera. Automated checks do not establish
collector usability or complete H10–H12.

Generate from current source, with output outside the repository:

```powershell
$env:WORKBENCH_H9_MEDIA = 'C:/Users/mcuni/.codex/visualizations/2026/09/09/01a0843d-3272-7182-b35d-6939f37f92e4/h9'
npm run build --prefix src/Workbench.Client
npm test --prefix tests/Workbench.BrowserTests -- --config acquisition-walkthrough.config.ts
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/render-h9-walkthrough.ps1 -Ffmpeg 'C:/Tools/ffmpeg.exe' -MediaRoot $env:WORKBENCH_H9_MEDIA
```

The renderer uses Windows PowerShell System.Speech with Microsoft Zira Desktop and a local FFmpeg
installation. Adjust the FFmpeg path. Capture, narration audio, screenshots and the final
`h9-acquisition.mp4` remain in the external output directory. The renderer measures speech duration,
creates captions and a transcript in this documentation directory, and checks the MP4 by decoding it.
Share the video externally; never commit generated media.

The separate `acquisition.spec.ts` regressions cover explicit Unknown/Gift/Inheritance/Trade choices,
unknown/year/month/exact dates, fresh-login retrieval, archived read-only context, literal notes,
independent-session conflicts, failed conflict reads, retained drafts, deliberate reconciliation,
320px light/dark comparison layouts, keyboard focus, reduced-motion/transparency preferences,
and lost creation/edit responses. See the [verification record](verification.md) for executed checks
and remaining limits.
