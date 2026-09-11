# H11 acquisition document walkthrough

The walkthrough adds a synthetic receipt to an acquisition, loses the upload response after
commit, checks the recorded outcome without duplicating the document, downloads the original,
and corrects its label at 320px in dark appearance. Authentication happens off camera.

Generate from current source with output outside the repository:

```powershell
$env:WORKBENCH_H11_MEDIA = 'C:/WorkbenchMedia/h11'
npm run build --prefix src/Workbench.Client
npm test --prefix tests/Workbench.BrowserTests -- --config acquisition-documents-walkthrough.config.ts
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/render-h11-walkthrough.ps1 -Ffmpeg '<local ffmpeg executable>' -MediaRoot $env:WORKBENCH_H11_MEDIA
```

The renderer uses Windows System.Speech with Microsoft Zira Desktop and full FFmpeg. It measures
narration duration, writes the transcript and captions here, and checks final MP4 decoding.
Media stays outside Git. The local verification helper `imageio-ffmpeg==0.6.0` supplies FFmpeg;
it is not an application dependency. PdfPig 0.1.16 is the new runtime PDF parsing dependency.

See the [verification record](verification.md) and the [qualified PDF subset](../../operations/blob-and-service-providers.md).
