# H7 verification record

Verified locally on 2026-09-08 against synthetic data and disposable SQL Server infrastructure.
Historical delivery revision: `8811d2154e9499cb3a9fd8ce67cf5c579e415fdd`.
No application schema migration was introduced.

| Check | Result |
| --- | --- |
| Locked restore, formatting, generated API drift, Release build | Passed through `scripts/verify.ps1`. |
| Full server suite, including migration drills | 536 passed, zero skipped; 14m37s. |
| Focused export suite | 27 passed, including six cases added after the full server build; built and run separately from the 536-test full suite. |
| Strengthened interruption assertions | Both passed with a 120-second SQL command timeout; application deadline and cancellation must release capacity while the SQL blocker remains held. |
| Client lint, typecheck, tests, build | Passed; 128 tests. |
| Full browser suite | 45 passed; 2m42s. |
| Published release unit | Passed `scripts/test-publish.ps1 -SkipClientBuild`. |
| Hardened container and production Compose topology | Passed `scripts/smoke-container.ps1`. |
| Narrated walkthrough | Passed capture, render, decode and desktop/mobile frame inspection. |

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
both appearances and reduced motion/transparency.

Six targeted manual mutations were killed: removing CSV safety prefixes, rejecting the exact row
limit, weakening SERIALIZABLE to READ COMMITTED, accepting stale client responses, ignoring a byte
count mismatch, and allowing an expired download when the timer is delayed. All source mutations
were restored. Stryker tooling is not installed; this is bounded manual evidence, not a comprehensive
mutation score.

The browser workflow used `http://127.0.0.1:4179/inventory/export`; published-output and hardened
container probes used ephemeral ports 55531 and 64631 respectively. Those disposable processes
were stopped by their verification scripts. See the [walkthrough instructions](README.md) for
local media generation. MP4s remain untracked. Automated evidence does not establish collector
usability, universal spreadsheet import behavior, public-CA issuance, SMTP delivery, or hosted drills.
