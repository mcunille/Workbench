# H4 narrated walkthrough

[Transcript](transcript.md) · [Captions](captions.srt)

Generate `h4-walkthrough.mp4` locally using the commands below. Recordings stay outside Git;
share the MP4 as a separate attachment. The walkthrough uses real API calls and disposable SQL,
synthetic collection text, and Microsoft Zira synthetic narration. Authentication happens off camera.

The scenes show canceling an edit, a failed save and safe retry on mobile, appearance changes,
a conflict between independently signed-in sessions, explicit reconciliation, and updated search
results after reload. The injected 503 models a server failure; the conflict uses actual saved data.
Separate browser regressions exercise loss of a committed save response, narrow conflict comparison,
keyboard focus, text contrast, and 320-pixel layouts.

## Reproduce on Windows

Use the pinned repository prerequisites, Playwright Chromium, Docker, and FFmpeg with H.264,
AAC, and subtitle support. From the repository root:

```powershell
npm run build --prefix src/Workbench.Client
./docs/demos/record-narration.ps1 -Scenario h4
npm test --prefix tests/Workbench.BrowserTests -- --config demos/h4.config.ts
python ./docs/demos/render-video.py --scenario h4 --ffmpeg /absolute/path/to/ffmpeg.exe
```

Recording is opt-in and excluded from the normal browser suite. Raw video, narration, and timings
are written to ignored `artifacts/h4-video`. Rendering writes the MP4, captions, and transcript here
and decodes the completed media to check integrity. The harness uses `http://127.0.0.1:4179` and
removes its disposable database and storage when it exits.

## Verification

The narrated Playwright scenario passed against disposable SQL. The roughly 76-second MP4
was rendered with captions and narration, then decoded completely without media errors.
Desktop, mobile retry, conflict comparison, and reconciliation frames were visually inspected.

H4 delivery also passed `scripts/verify.ps1 -SkipDependencyInstall` (423 server tests, 73 client
tests, 25 browser tests, migration drills, generated contracts, builds, and published-output
probes) and `scripts/smoke-container.ps1`. A final focused browser run passed all three H4 cases.
Four targeted manual mutants were detected: SQL version predicate, client checked token,
stale reconciliation initialization, and late cache invalidation. No automated mutation score
is claimed. These are local verification results; production acceptance is separate.
