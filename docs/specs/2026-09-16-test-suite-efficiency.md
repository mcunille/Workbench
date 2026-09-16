# Test coverage ownership and efficient verification

**Status:** Implemented and locally verified. The user approved implementation of all ten audit recommendations on 2026-09-16.

## Problem and scope

The source/release CI job at base revision 7ccc216 took 12m10s. Its 910 server cases took 503.3s across two balanced processes; 101 browser cases took 321.3s concurrently. Duplicate fixture preparation, full recovery drills for manifest-label permutations, and browser setup beyond the asserted boundary increase cost as features grow.

This change implements ten improvements: separate manifest compatibility from physical recovery; selectively reuse isolated current-schema clones; remove duplicated/unused readiness setup; give effective-authority matrices SQL ownership; move SQL-free contracts out of SQL fixtures; make browser evidence capture opt-in and consolidate shared shell matrices; remove browser-only API duplication; use small images for unrelated flows; reduce redundant pagination setup; and establish browser project/session ownership before bounded concurrency.

## Invariants and design

- Keep real SQL for grants, tenant isolation, persistence, transactions, readiness, and races. SQL integration fixtures retain independent databases and proof keys. Template reuse is current-run, unseeded, and disposable.
- Keep explicit fresh migration/upgrade/rollback drills, including a fresh migration-to-principal-provisioning integration case. No historical migration files change.
- Preserve the exact manifest validation behavior when extracting a pure internal method; characterize all accepted versions and mismatches. Retain physical recovery for attachment and acquisition-document paths.
- Preserve representative HTTP ready/unready/liveness/schema transitions; move all effective-authority permutations to the real readiness service under restricted principals.
- Keep native browser downloads, real file input/preparation, focus/history/scroll/geometry, session revocation, and representative persistence/retry/conflict workflows. Exhaustive pure validation/transport permutations have named lower-layer owners.
- Optional screenshots gate only capture, never navigation or assertions. Safe failure diagnostics remain on by default and retain their existing privacy limits.
- Browser intercepted tests own synthetic per-page state and fail on undeclared API access. Live mutation tests retain bounded worker ownership and distinct ordinary/secondary sessions. Destructive authentication scenarios must not revoke reused sessions. Preserve production authentication and network rate limits.
- Select concurrency using measured results. A serial live worker plus independent intercepted project is acceptable; increasing workers is not a completion criterion if contention or quota cost makes it slower.

## Alternatives and tradeoffs

Do not replace SQL with an in-memory provider, share mutable test databases, weaken rate limits, remove security matrices, or move required correctness checks to nightly-only CI. Per-test live tenants authenticated through UI would spend the shared network quota and increase latency. Worker-scoped ownership and intercepted UI fixtures isolate the practical boundaries while retaining representative true end-to-end coverage.

## Measurement and acceptance

Measure every numbered improvement against its immediate preceding state with a focused cohort or full browser context. Keep builds separate from fixture-inclusive process timing where feasible. Report repetitions/range and neutral or regressive outcomes; cohort savings are not additive. Compare a complete baseline and final gate on the same host, then report CI separately. Raw benchmark evidence stays in ignored artifacts; a concise results record is committed.

For moved tests record prior and replacement owners. Characterization protects behavior-preserving moves; new harness behavior gets a failing focused test before implementation. Scoped manual mutations must exercise important manifest, isolation, and fail-closed test-harness branches, with exclusions recorded accurately. Complete required verification, container smoke, independent review, and ready-for-review PR delivery. No merge or production action is authorized.

See the [measurement record](2026-09-16-test-suite-efficiency-results.md) for per-change results,
coverage accounting, mutation evidence, and full-gate comparison limits.
