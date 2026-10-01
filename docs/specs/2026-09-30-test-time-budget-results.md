# Local test case budgets and SQL setup reduction

**Status:** Implemented and verified

## Scope and retained owners

Default gate server and browser cases have a 30-second maximum. xUnit facts and
individual theory rows receive the shared timeout. xUnit 2 requires async test
methods and disabled collection parallelism for that timeout; formerly synchronous
test bodies yield once and retain their assertions. Independent server partitions
still run concurrently. Vitest retains its shorter default. Node infrastructure
checks use a 30-second test budget and shorter child-process deadlines.

Playwright uses a 30-second case timeout. Its longer file and runtime overrides
were removed. The gate reporter fails
runs containing runtime timeout extensions or durations over 30 seconds. Runtime
extensions are detected at case completion. Shared container/server startup and
separate narrated-media recording configurations are outside the default case
budget. xUnit timeout failure does not cooperatively cancel pending test work.

| Previous expensive owner | Replacement and retained contract |
| --- | --- |
| `FullRetainedClosureAccepts1000AndRejects1001WithoutTruncation` | `RetainedClosureCountsReversalsAndRejects1001BeforeEvidenceValidation`: genuine payment/application/reversal preview with exact identities and event count; 997 FK-valid application identities inserted in one SQL batch; exactly 1000 reaches the specific later evidence guard, and 1001 reaches the specific cap guard; each preview leaves its snapshot unchanged. |
| 18 payment guard rows | 10 isolated test rows still exercise all 18 inputs. Three rows group immutable malformed-input variants; account state, stale versions and capacity remain isolated. Each attempt has a fresh command and labelled SQL rejection, evidence-count and bill-balance assertions. |
| 12 application guard rows | Eight isolated rows still exercise all 12 inputs. Two rows group malformed target variants; source identity/date, purchase-order scope, currency and stale versions remain separate. Every attempt retains labelled rejection and no-partial-evidence assertions. |
| Longer browser timeouts | All 122 baseline browser cases were already below 30 seconds; no journey splits or assertion removals were needed. |

The closure replacement deliberately removes the fully valid 1000-event preview
and its 500-application/499-reversal serialization assertion. It retains actual
SQL guard ordering and a small genuine complete preview. Synthetic boundary rows
are intentionally missing posting evidence, so accepting exactly 1000 is proved
by reaching that later guard rather than obtaining a valid large plan. The user
approved this coverage tradeoff. SQL remains the owner of these authoritative
guards; no production mocks, schema changes or test-only production seams were added.

This change consolidates 12 server rows and adds one budget regression case.
The final branch also incorporates main's already merged reduction of 60 redundant
cases in #188; those removals belong to that change, not this optimization. Final
server inventory is 1682 cases, down from main's 1693. Browser inventory remains
122 cases; client inventory is 508 cases after main's separate pruning.

## Measurements

Same Windows host; baseline full gate used two partitions/two concurrent SQL
containers. Focused after measurements used one disposable SQL container, so
those results are not a controlled whole-gate comparison. Both built current
source. Final full-gate measurements used the original concurrency, with main's
separate case reductions also incorporated.

| Cohort | Baseline full gate, summed case seconds | Focused after, summed case seconds | Final full gate, summed case seconds |
| --- | ---: | ---: | ---: |
| Closure boundary | 387.446 | 25.954, including cold template creation | 18.187 |
| Payment guard matrix | 105.80 | 67.664 | 66.997 |
| Application guard matrix | 94.00 | 45.094 | 53.628 |

Browser baseline: 122 passed in 3.2 minutes including startup; maximum case
duration 14.046 seconds. The server baseline full gate took 36m28s overall,
including a browser startup failure from Windows Application Control; its server
stage passed all 1742 cases in 34m35s. The successful standalone browser run did
not reproduce that host-policy failure.

Raw local timing evidence is ignored under `artifacts/local-sql-timing-20260929/`
and `artifacts/sql-budget/`, including per-case CSVs, TRX, browser JSON, probe logs
and gate logs. Final exports are `artifacts/sql-budget/final/server-test-times.csv`,
`server-class-totals.csv` and `browser-test-times.csv`.

Final full gate passed in **30m17s** (1817.137 seconds), versus 36m28s historically.
Its server stage took **28m27s** (1707.040 seconds); the two partitions passed
827 cases in 1551.478 seconds and 855 cases in 1701.3 seconds. All 1682 server
cases passed, with zero recorded durations over 30 seconds. The maximum was
23.456 seconds (`MixedPaymentCorrectionAndCompensationKeepTheirExactOwners`).
The summed server case duration was 3147.853 seconds.

All 122 browser cases passed; maximum case duration was 16.622 seconds and its
stage took 170.832 seconds. All 508 client tests passed within Vitest's shorter
default timeout. Client, published-output and browser stages passed concurrently
with the server stage. Locked dependency installation, formatting, Release build,
OpenAPI drift, client lint/typecheck/build and published-output verification passed.

The complete gate still exceeds 30 minutes slightly. The remaining aggregate
cost is spread across many isolated SQL cases and their setup. The total reduction
also includes main's separate pruning and run-to-run variation; it is not wholly
attributable to this change.

## Regression evidence

- The server budget test was red with all 870 method attributes lacking a timeout,
  then green with the shared attributes. The original slow closure failed with
  `Test execution timed out after 30000 milliseconds` in the verified prior binary.
- The initial browser discovery check was red with longer declarations, then green
  after their removal. It was subsequently removed in review as redundant with
  execution-time enforcement; the runtime reporter regression test remains.
- A real four-case Playwright probe first exited successfully despite runtime
  timeout extensions. After the reporter fix, all four bodies still pass but
  `test.setTimeout`, `testInfo.setTimeout` and `test.slow` extensions make the run fail.
- Four manual, current-source SQL mutation probes were killed by the intended
  assertions: `>=1000` rejected the inclusive boundary; `>1001` allowed one excess
  event; omitting reversal count changed the genuine preview count from three to
  two; allowing overlong payment notes produced a successful write and the labelled
  `longNotes` rejection assertion failed. Production source was restored after each.
  This is scoped mutation evidence, not a whole-suite mutation score.
- All 13 initial Node policy/isolation/diagnostics/build-tool checks passed under
  the budget. After removing the redundant discovery check, all 12 retained checks
  passed again. This test-only deletion does not change runner or reporter behavior.
- `scripts/smoke-container.ps1` passed from current source, including internal TLS,
  SQL readiness, Secure-cookie login, app replacement/session durability,
  forwarding-header checks and worker telemetry. Public CA issuance and SMTP
  delivery were not exercised by that gate.
- Final `scripts/verify.ps1` passed, including locked dependency installation.
  Evidence run: `ed39bdc33b4f4612af6542941328ca7d`. An initial
  run was intentionally interrupted to address the runtime-policy review finding;
  a second was interrupted to incorporate the already merged main test reductions.
  Neither is completion evidence. Final verification installed main's updated
  locked dependencies and exercised the combined source.
