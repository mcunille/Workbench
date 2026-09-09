# H9 verification record

Implemented against base `00d61e8f223a621fc462911844cf4f541705fcfe`. The full delivery gate verified
code and tests at `e76bbf5fb90dbc4375a4c0fb4313da94432a7157` (2026-09-08).

## Focused evidence

- TDD: the initial client test failed because Add acquisition was missing; the initial real-SQL
  API test failed because the acquisition read route returned 404 instead of the empty context.
- Client: 164 tests passed, including 15 H9 cases; client build, typecheck and lint passed.
- Date validation TDD: four regression cases first failed for fractional/out-of-range input and
  generic rejected requests, then passed with editable validation feedback and preserved input.
- Server: 88 affected tests passed, including acquisition persistence, validation, tenant isolation,
  concurrent creation/edit/archive, transaction rollback, migration upgrade, readiness and provisioning.
- Browser: all three H9 workflows passed against a disposable SQL database at
  `http://127.0.0.1:4186`; the harness removed its server/database afterward. The final full gate
  also passed the form-layout, navigation and invalid-date assertions.
- Manual client mutations: all three were killed by assertions: rotating a retry UUID, reconciling
  from stale draft values, and inventing a year for an unknown date. Original source was restored
  and the focused suite passed again.
- Manual server mutations: all five were killed by assertions: moving the UTC date boundary,
  bypassing acquisition-version and archived-item guards, accepting mismatched creation retries,
  and disabling transaction abort. Source was restored and all 33 acquisition tests passed again.
  These eight mutations provide bounded evidence, not a full mutation score.
- Narrated Playwright walkthrough passed; its MP4 rendered with measured speech/captions and passed
  full FFmpeg decode. Desktop saved context, a captioned form frame, and archived mobile dark context
  were visually inspected. Final capture used production source at `4e8fcf7`; the later gate revision
  only changed test assertions, the authentication test helper, and caption timing.

## Media and reproduction

Generated media stays outside Git at
`C:/Users/mcuni/.codex/visualizations/2026/09/09/01a0843d-3272-7182-b35d-6939f37f92e4/h9/`:
`h9-acquisition.mp4`, `saved-acquisition.png`, and `archived-mobile-dark.png`.
The transcript/captions and [reproduction commands](README.md) are committed. The renderer used
FFmpeg 7.1 from the pinned `imageio-ffmpeg` 0.6.0 helper in ignored local artifacts, and Windows
System.Speech. Authentication happened off camera; records are synthetic.

## Delivery gates and limits

- Full `scripts/verify.ps1 -SkipDependencyInstall -ServerPartitions 4 -ServerConcurrency 4` passed:
  644 server, 164 client and 56 browser tests; formatting, Release build, typecheck, lint,
  generated API drift and published release-unit checks passed. Existing installed dependencies
  were reused; locked .NET restore ran. Run `938455b0efda48b5b277f749a3f3fb92` completed in 462.16 seconds.
  Local evidence is in `artifacts/verification/938455b0efda48b5b277f749a3f3fb92/`.
  Published release-unit inspection used `http://127.0.0.1:54962` and cleaned up afterward.
- `scripts/smoke-container.ps1` passed on the final production source, before the test-only
  corrections: migration/provisioning, non-root read-only runtime, internal TLS, secure-cookie
  login and durable session after app replacement. Evidence: `artifacts/h9-container-smoke.log`;
  its temporary local URL was `http://127.0.0.1:64320` and containers were removed.

Collector usability testing requires a human participant and remains separate from automated
acceptance. H10–H12, real production deployment, and acquisition-aware export are outside this delivery.
