# H7 verification record

Verified locally on 2026-09-08 against synthetic data and disposable SQL Server infrastructure.
No application schema migration was introduced.

| Check | Result |
| --- | --- |
| Locked restore, formatting, generated API drift, Release build | Passed through `scripts/verify.ps1`. |
| Full server suite, including migration drills | 536 passed, zero skipped; 14m37s. |
| Final focused export suite | 27 passed, including six cases added after the full server build. |
| Strengthened interruption assertions | Both passed again with a 120-second SQL command timeout; application deadline and cancellation must release capacity while the SQL blocker remains held. |
| Client lint, typecheck, tests, build | Passed; 128 tests. |
| Full browser suite after fixture cleanup | 45 passed; 2m42s. |
| Published release unit | Passed `scripts/test-publish.ps1 -SkipClientBuild`. |
| Hardened container and production Compose topology | Passed `scripts/smoke-container.ps1`. |
| Narrated walkthrough | Passed capture, render, decode and desktop/mobile frame inspection. |

The initial `verify.ps1` run completed server/client stages, then exposed H7 fixture pollution:
its 51-plus seeded active items pushed later scenarios' items off the first collection page.
H7 now tracks its own created IDs and archives them in teardown, including failed scenarios.
The entire browser suite and the remaining published-output gate were rerun successfully.
Earlier passing runtime checks remain applicable because that correction changed only browser
fixture cleanup. Additional server tests were built and run separately from current source.

## Behavioral evidence

Initial endpoint tests failed with 404 before implementation. Focused tests cover explicit scope,
more than one loaded page, tenant isolation, CSV dialect and reversible safety prefixes, Unicode,
newlines, null fields, timestamps, row/encoded-byte limits, busy preparation, database failure,
expired authorization, cancellation, and slot recovery. Independent SQL sessions demonstrate
edit/archive/restore/insert contention in both scopes, including an initially empty active scope.
Session revocation while preparation is paused prevents release of the prepared bytes.

Client checks cover navigation and appearance retention, identity/sign-out disposal, delayed
responses, explicit retries, complete response/body validation, ten-minute expiry and delayed timers.
Browser checks exercise real downloads, keyboard operation, 44px controls, 320px/desktop layouts,
both appearances and reduced motion/transparency. The first browser red attempt did not reach
behavior because dependencies/client build were missing; it is not counted as behavioral red.

Six targeted manual mutations were killed: removing CSV safety prefixes, rejecting the exact row
limit, weakening SERIALIZABLE to READ COMMITTED, accepting stale client responses, ignoring a byte
count mismatch, and allowing an expired download when the timer is delayed. All source mutations
were restored. Stryker tooling is not installed; this is bounded manual evidence, not a comprehensive
mutation score. Independent static review found no remaining substantive findings after interruption
assertions were strengthened; final fixture cleanup was also inspected locally.

The browser workflow used `http://127.0.0.1:4179/inventory/export`; published-output and hardened
container probes used ephemeral ports 55531 and 64631 respectively. Those disposable processes
were stopped by their verification scripts. See the [walkthrough instructions](README.md) for
local media generation. MP4s remain untracked. Automated evidence does not establish collector
usability, universal spreadsheet import behavior, public-CA issuance, SMTP delivery, or hosted drills.
