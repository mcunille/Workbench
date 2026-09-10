# Walkthroughs and historical evidence

## Capture and rendering prerequisites

Run commands from the repository root after completing [development setup](../setup.md).
Install the locked client/browser dependencies and Playwright Chromium as documented there;
use the repository's pinned .NET SDK, Node.js, PowerShell 7 and a running Docker engine.
Browser capture scripts start the real API with disposable SQL and synthetic records;
authentication happens off camera. They do not reuse the interactive preview database.

Build the current client once before the selected capture:

```powershell
npm run build --prefix src/Workbench.Client
```

Rendering requires Windows PowerShell (`powershell.exe`), System.Speech, Microsoft Zira Desktop
and a full local FFmpeg installation. Replace `C:/Tools/ffmpeg.exe` with its actual path.
The execution-policy override applies only to that renderer process. Renderers write each
walkthrough's transcript and captions and check MP4 decoding; inspect the resulting frames,
caption placement and narration before considering the media complete.

## H6–H10 commands

Run the capture and then its matching renderer:

| Scenario | Capture configuration | Scenario and evidence |
| --- | --- | --- |
| H6 archive recovery | `h6-walkthrough.config.ts` | [Narration](h6/README.md) · [Evidence](../specs/2026-09-07-h6-archive-recovery.md#verification-record) |
| H7 CSV export | `h7-walkthrough.config.ts` | [Narration](h7/README.md) · [Evidence](h7/verification.md) |
| H8 collection package | `package-walkthrough.config.ts` | [Narration](h8/README.md) · [Evidence](h8/verification.md) |
| H9 acquisition | `acquisition-walkthrough.config.ts` | [Narration](h9/README.md) · [Evidence](h9/verification.md) |
| H10 shared acquisition | `shared-acquisition-walkthrough.config.ts` | [Commands and narration](h10/README.md) · [Evidence](h10/verification.md) |

```powershell
# H6
npm test --prefix tests/Workbench.BrowserTests -- --config h6-walkthrough.config.ts
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/render-h6-walkthrough.ps1 -Ffmpeg 'C:/Tools/ffmpeg.exe'

# H7
npm test --prefix tests/Workbench.BrowserTests -- --config h7-walkthrough.config.ts
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/render-h7-walkthrough.ps1 -Ffmpeg 'C:/Tools/ffmpeg.exe'

# H8
npm test --prefix tests/Workbench.BrowserTests -- --config package-walkthrough.config.ts
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/render-h8-walkthrough.ps1 -Ffmpeg 'C:/Tools/ffmpeg.exe'

# H9: choose an absolute output directory outside the repository.
$env:WORKBENCH_H9_MEDIA = Join-Path $env:TEMP 'workbench-h9-media'
npm test --prefix tests/Workbench.BrowserTests -- --config acquisition-walkthrough.config.ts
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/render-h9-walkthrough.ps1 -Ffmpeg 'C:/Tools/ffmpeg.exe' -MediaRoot $env:WORKBENCH_H9_MEDIA
```

## Media and evidence policy

Generated media stays out of Git and is shared externally. H6–H8 scripts currently generate under
ignored `artifacts/h6/walkthrough/`, `artifacts/h7/walkthrough/` and `artifacts/h8/walkthrough/`;
H9 enforces an external output directory. Keep these legacy output locations untracked. For PR
attachments and reviewer evidence, follow [AGENTS.md](../../AGENTS.md#change-integration).
Transcripts and captions remain versioned alongside their unique scenario narration.

Dated verification records describe the cited implementation, not a fresh check of current source.
Automated and visual checks do not establish collector usability or production acceptance.
## Historical H1–H5 recordings

On 2026-09-08, a bounded local inventory found no MP4/WebM recordings in the main checkout's
`artifacts/`, local Codex worktrees, or September visualization directories. The inventory could not
read `60c3/Workbench/artifacts/self-host-live`; it does not establish that recordings are absent
elsewhere. H1 and H2 recordings remain in Git history, with matching [H1](h1/README.md) and [H2](h2/README.md)
companions and immutable media links. H3–H5 had no repository reproduction scripts or working media destinations, so their
orphan README, transcript, and caption files were retired together. No media was deleted.
The complete historical companions remain in Git at
[`9e618a1`](https://github.com/mcunille/Workbench/tree/9e618a1be40f47807c8164eab00d118305b9fdca/docs/demos).

Historical verification is recorded once per increment:

| Increment | Evidence owner |
| --- | --- |
| H1 | [Walkthrough verification](h1/README.md#verification) |
| H2 | [Walkthrough verification](h2/README.md#verification) |
| H3 | [Search verification](../specs/2026-09-07-h3-collection-search.md#verification-evidence) |
| H4 | [Editing verification](../specs/2026-09-07-h4-item-editing.md#verification-record) |
| H5 | [Archiving verification](../specs/2026-09-07-h5-item-archiving.md#verification-record) |

These historical transcripts describe their original increment; current behavior is specified in
the [documentation index](../README.md).
