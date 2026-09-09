# H8 narrated collection package walkthrough

[Transcript](transcript.md) · [Captions](captions.srt)

The walkthrough uses synthetic records and real uploaded photographs against the API and disposable
SQL. It demonstrates the CSV/ZIP choice, explicit record scopes, stored photograph mapping, an
explicit no-photo record, archived inclusion, navigation and theme retention, an injected preparation
failure and safe retry. The final view displays the actual downloaded manifest and image bytes.
Authentication occurs off camera. Automated checks do not establish collector usability.

Generate from current source:

```powershell
npm run build --prefix src/Workbench.Client
npm test --prefix tests/Workbench.BrowserTests -- --config package-walkthrough.config.ts
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/render-h8-walkthrough.ps1 -Ffmpeg 'C:/Tools/ffmpeg.exe'
```

The renderer uses Windows PowerShell System.Speech with Microsoft Zira Desktop and a local FFmpeg
installation. Adjust the example FFmpeg path. Capture, actual ZIP downloads and the narrated MP4
remain under ignored `artifacts/h8/walkthrough/`; the renderer writes this directory's transcript and
captions and verifies MP4 decoding. Keep generated videos out of Git and share them externally.

The separate `package.spec.ts` regressions independently parse ZIP entries and validate CSV/manifest
mapping, photo length/digest and exact stored detail bytes. They exercise both scopes, photo absence,
keyboard operation, light/dark desktop and 320px layouts, reduced-motion/transparency preferences,
navigation/reload behavior, partial responses, cancellation and retry.

See the [verification record](verification.md) for completed gates and coverage limits.
