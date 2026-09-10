# H10 verification record

Implementation began at `1cd7166f2a4049c0b16ef680d3d1a3249d4d815c`; the documentation-only
base update `45a999b0a24e59665a87fe71d6dcdd3b40866e73` was incorporated before delivery.
The owner approved the [shared acquisition design](../../specs/2026-09-09-shared-acquisitions.md)
on 2026-09-10.

## Focused verification

- TDD: the initial real-SQL link test returned 404 instead of the expected 200 before the
  implementation. The initial client tests failed for the missing acquisition picker,
  shared-edit scope notice and archived acquisition navigation.
- The expanded backend run passed 95 acquisition, provisioning and readiness tests, including
  five competing HTTP request scenarios against SQL Server. Schema/direct-SQL checks passed
  5 cases; the restored shared suite passed 13 cases. Two deterministic contention variants
  used separate runtime SQL connections and asserted the waiting SPID was blocked by the
  uncommitted winner, then verified opposite replacement and old creation replay outcomes.
- Client implementation passed 188 tests across 36 files plus lint and production build.
  Independent review found two P2 issues: a stale locally saved item token and incorrect
  collection return origin after traversing archive state. Both were reproduced with failing
  tests, fixed, and re-reviewed at `14d34debefebc6711f9cdbaafc1852af88953b61` with no remaining
  actionable findings. The fix run passed 26 focused tests and a current production build.
- Four bounded manual mutations were detected by behavioral assertions: removed SQL target
  version check; removed SQL archived-write guard; removed client newly-archived save guard;
  and reused stale base versions during explicit reconciliation. Original source was restored
  after each mutation and focused checks passed. This is not a comprehensive mutation score.
- All seven H9/H10 browser regressions passed on reviewed source in 45.3 seconds. These cover
  three-piece sharing, existing/new entry, replacement/removal, archive/restore, retained
  navigation, lost responses, explicit reconciliation, keyboard operation, 320px appearances,
  and reduced-motion/transparency preferences. The first run exposed two test defects
  (card accessible-name selection and premature save observation), corrected before this run.

## Delivery evidence

- Final-source `smoke-container.ps1` passed at `14d34de`: non-root/read-only SQL-backed runtime,
  local Compose TLS, Secure-cookie login, durable session after app replacement, forwarded
  headers, private listener and worker queue telemetry. Log: `artifacts/h10-smoke-final.log`.
- This checkout's isolated preview at `http://localhost:32769` was started with `dev-up.ps1`
  and exercised using its protected login. Two synthetic pieces were connected through the
  UI and opened in the shared view. Desktop/light and 320px/dark screenshots were inspected;
  the narrow document had no horizontal overflow. Preview credentials were not captured.
- The narrated Playwright capture passed against source `14d34de` with synthetic records and
  off-camera authentication. The 81-second MP4 rendered with measured narration and captions,
  passed complete FFmpeg decoding, and representative desktop/shared and mobile/archive
  captioned frames were visually inspected. Reproduction uses FFmpeg 7.1 from the pinned local
  `imageio-ffmpeg` 0.6.0 helper and Windows System.Speech.
- The complete `verify.ps1` gate result will be recorded before PR delivery.

Media remains at
`C:/Users/mcuni/.codex/visualizations/2026/09/10/01a08a18-209c-7912-8ffa-aa2ac6cd2ac9/h10/`:
`h10-shared-acquisition.mp4`, `shared-acquisition-desktop.png`, and
`archived-acquisition-mobile-dark.png`, plus local preview and video QA frames.

## Scope limits

Automated scenarios do not establish uncoached collector usability. No production migration,
deployment or traffic change is part of this work. H11 documents, H12 exports, financial fields,
purchase orders, lots, assembly relationships and public sharing are excluded.

Generated media remains outside Git. See [reproduction instructions](README.md).
