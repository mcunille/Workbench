# H12 verification record

Verified on 2026-09-11 on `codex/73-acquisition-history-export`, based on `052333a`.
Application implementation is commit `3b0d479`; follow-up tests cover metadata inconsistencies
and acquisition relinking. Only this evidence documentation and specification status changed
after the full passing gate. No schema migration or production operation was performed.

## Automated evidence

- `./scripts/verify.ps1 -SkipDependencyInstall` passed: 791 server tests in two isolated SQL
  partitions, 221 client tests across 41 files, 75 browser tests, locked restore, formatting,
  Release build, generated API drift, client lint/typecheck/build and published release checks.
  Gate ID `02ea509612c14d7e967caa9da19d6174`; elapsed 498.88 seconds.
- `./scripts/smoke-container.ps1` passed on a freshly built hardened Linux runtime: non-root,
  read-only runtime, SQL readiness/migrations/restricted principals, internal TLS, Secure-cookie
  login, session persistence across replacement, private listener and worker telemetry.
- Focused export testing passed 110 cases before nine additional metadata/relink cases entered
  the full gate. Standard CSV/ZIP parsers verify optional facts, all methods/date precisions,
  reversible text, shared documents, selected relationships and exact bytes. Real SQL sessions
  exercise acquisition edits, unlink/relink, and document changes around snapshot capture.
  Missing, corrupt, unreadable, recovery-unavailable and inconsistent metadata fail the package.
  Pending/removed documents and orphan/foreign acquisitions are excluded.
- Three manual mutations were killed by assertions: removal of the document-count guard,
  bypass of document type validation, and removal of CSV formula protection. Original source
  was restored before the full gate. This is scoped evidence, not an exhaustive mutation score.
- Independent implementation review of `052333a..3b0d479` found no actionable defects;
  additional metadata and relink tests address the identified coverage gaps.

## Preview and visual evidence

`./scripts/dev-up.ps1` produced the retained current-source preview at
`http://localhost:32768`. The H12 Playwright journey was run against it using a scratch runner
that changed only import paths and authentication to use this checkout's protected login.
Credentials were loaded privately, without capture or output. The journey passed in 1.1 minutes.
A separate console-health check passed with no browser errors or warnings during CSV preparation.

The journey connects three individual pieces to one acquisition, attaches synthetic receipt and
report images, retains a photograph, and archives one piece. Active/all downloads preserve scoped
relationships and one copy of each document; extracted bytes match uploads. An offline context
shows manifest relationships without Workbench access. It exercises both appearances at 1280px
and 320px, keyboard, navigation retention, reduced motion/transparency, and a simulated HTTP 503
preparation failure followed by successful retry. Screenshots were inspected for readable content
and wrapping; the normal fixed mobile navigation remains scroll-accessible.

External media: `h12-narrated.mp4`, `shared-pieces-desktop.png`, `paperwork-desktop.png`,
`export-1280-light.png`, `export-1280-dark.png`, `export-320-light.png`, `export-320-dark.png`,
`preparation-failure.png` and `offline-manifest.png`. The 61.68-second H.264/AAC recording uses
Windows System.Speech, aligned to capture segments; a complete decode passed. No media is in Git.
GitHub CLI was unavailable on this host, so the required `gh pr edit --attach` upload could not
be performed. Files remain available locally rather than being committed or uploaded by browser.

To reproduce capture with disposable test SQL after the [setup prerequisites](../../setup.md):

```powershell
npm run build --prefix src/Workbench.Client
$env:WORKBENCH_H12_MEDIA = 'C:/Temp/workbench-h12-media'
npm test --prefix tests/Workbench.BrowserTests -- --config acquisition-export-walkthrough.config.ts
```

The media directory must be absolute and outside the repository. Capture writes `capture.webm`,
`segments.json`, screenshots, downloaded packages and `verification.json`. Speech rendering used
local scratch tooling with System.Speech and FFmpeg; segment text/timing is retained in the capture
output. This record's interactive preview samples remain in the isolated preview for inspection.

## Limits

The browser storage failure is simulated; provider failures and snapshot races are exercised by
server integration tests. Stored PDFs/images retain H11's validation and embedded-metadata limits.
The package is not an application backup or import/restore format. Broader mutation coverage,
public CA issuance, production SMTP delivery and production deployment are not established here.
An uncoached collector trial has not occurred: scenario #73 remains open for participant evidence,
including terminology and independent comprehension of downloaded history. H12 delivery alone
does not establish human usability.
