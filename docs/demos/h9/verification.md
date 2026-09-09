# H9 verification record

Implemented against base `00d61e8f223a621fc462911844cf4f541705fcfe`. The final PR commits identify
this source; full delivery gate results are recorded below when complete.

## Focused evidence

- TDD: the initial client test failed because Add acquisition was missing; the initial real-SQL
  API test failed because the acquisition read route returned 404 instead of the empty context.
- Client: 160 tests passed, including 11 H9 cases; client build, typecheck and lint passed.
- Server: 88 affected tests passed, including acquisition persistence, validation, tenant isolation,
  concurrent creation/edit/archive, transaction rollback, migration upgrade, readiness and provisioning.
- Browser: all three H9 workflows passed against a disposable SQL database at
  `http://127.0.0.1:4186`; the harness removed its server/database afterward. Additional form-layout
  and navigation assertions are included in the final full gate.
- Manual client mutations: all three were killed by assertions: rotating a retry UUID, reconciling
  from stale draft values, and inventing a year for an unknown date. Original source was restored
  and the focused suite passed again. This is bounded mutation evidence, not a full mutation score.
- Narrated Playwright walkthrough passed; its MP4 rendered with measured speech/captions and passed
  full FFmpeg decode. Desktop saved context, a captioned form frame, and archived mobile dark context
  were visually inspected. Capture preceded final server-only hardening checks; the UI is unchanged.

## Media and reproduction

Generated media stays outside Git at
`C:/Users/mcuni/.codex/visualizations/2026/09/09/01a0843d-3272-7182-b35d-6939f37f92e4/h9/`:
`h9-acquisition.mp4`, `saved-acquisition.png`, and `archived-mobile-dark.png`.
The transcript/captions and [reproduction commands](README.md) are committed. The renderer used
FFmpeg 7.1 from the pinned `imageio-ffmpeg` 0.6.0 helper in ignored local artifacts, and Windows
System.Speech. Authentication happened off camera; records are synthetic.

## Delivery gates and limits

Full `scripts/verify.ps1`, `scripts/smoke-container.ps1`, final server mutation checks, and independent
implementation review are pending. Do not treat the focused checks as full-gate evidence.
Collector usability testing requires a human participant and remains separate from automated
acceptance. H10–H12, real production deployment, and acquisition-aware export are outside this delivery.
