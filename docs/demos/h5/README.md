# H5 narrated walkthrough

[Transcript](transcript.md) · [Captions](captions.srt)

The walkthrough shows confirmation and cancellation, an independent session changing a record,
renewed confirmation, loss of a committed archive response, safe retry, read-only bookmarked
details, and exclusion from Grid/List search. All data is synthetic; requests use the real API
and a disposable SQL database. Authentication happens off camera. Narration uses Microsoft
Zira's synthetic voice. The interrupted response is deliberately injected after server commit.

## Reproduce on Windows

Use the pinned repository prerequisites, Playwright Chromium, Docker, and FFmpeg with H.264,
AAC, and subtitle support. From the repository root:

```powershell
npm run build --prefix src/Workbench.Client
./docs/demos/record-narration.ps1 -Scenario h5
npm test --prefix tests/Workbench.BrowserTests -- --config demos/h5.config.ts
python ./docs/demos/render-video.py --scenario h5 --ffmpeg /absolute/path/to/ffmpeg.exe
```

Recording is opt-in. Raw footage, audio, and timings go to ignored `artifacts/h5-video`;
the generated MP4 remains outside Git and can be shared separately. The harness uses
`http://127.0.0.1:4179` and removes its disposable SQL container and storage on exit.

## Verification

The separate `archiving.spec.ts` browser regressions cover retained photographs at 320px and
desktop widths in both appearances, keyboard confirmation/cancellation focus, touch targets,
Grid/List/search exclusion, unchanged retry tokens after a lost committed response, two-session
conflict with a failed recovery read, and retention of a text draft after another session archives.
These are technical checks; they do not establish collector usability.

The H5 narrated browser scenario passed against disposable SQL on 2026-09-07. Its roughly
96-second MP4 rendered with narration and burned-in captions and decoded completely without
media errors. The mobile uncertain-outcome and desktop archived-state frames were visually
inspected. Separate photographed archived-record screenshots were inspected at 320px dark and
desktop light; the complete browser suite records the final regression outcome.

H5 verification passed 439 server tests and 82 client tests, generated contracts, formatting,
typechecking, builds, EF model consistency, and all four migration/recovery drills. The initial
full browser run passed 29 of 30 scenarios; its authentication test assumed a single session.
After targeting the current session explicitly, all 10 archive/authentication scenarios passed
together. The other 20 browser scenarios were unchanged. Published-output probes and the
SQL-backed container/Compose smoke gate passed separately. Thus the checks from `verify.ps1`
were completed across the main run and focused continuation after that test-only correction.

Four targeted manual mutation probes were detected (SQL version predicate, client expected token,
cache invalidation, and uncertain-navigation protection). No automated mutation score is claimed.
The ephemeral browser URL was `http://127.0.0.1:4179`; the harness shuts it down after each run.
