# CI test work deletion and class fixture routing

**Status:** Fresh matched coverage passed within 5%. One rebased hosted gate passed; its unchanged-head repeat failed a client focus assertion. A reliable ten-minute saving is not demonstrated.

The goal is at least ten minutes less hosted end-to-end CI, with client and Server/Database collector line/branch coverage losing at most 5% relative to the comparable baseline. This batch retains two server processes on one runner.

## Retired 15 cases and remaining owners

| Removed case | Decision and remaining proof |
| --- | --- |
| `ConcurrentUnapplicationsReleaseCapacityExactlyOnce(false)` | Retain true: same operation, amount, principal and initial state over interchangeable connections; one blocked contender, one inverse, conserved capacities and cash remain. |
| `ConcurrentAllocationsCannotOverspend(false,false)` | Retain true,false for shared funding. |
| `ConcurrentAllocationsCannotOverspend(false,true)` | Retain true,true for shared debt. Different capacity owners remain independent tests. |
| `ConcurrentPaymentsCannotOverSettleBill(false)` | Retain true: one successful payment, stale loser, zero debt, one receipt and exact cash result. |
| `GenericJournalCorrectionCannotDetachSupplierEvidence(application)` | Retain payment: all three histories satisfy the same movement-existence admission disjunct; this matrix does not independently prove the SourceKind predicate. |
| `GenericJournalCorrectionCannotDetachSupplierEvidence(reversal)` | Same keeper; remove conditional allocation/reversal setup. |
| `CorrectionAndDependencyCommandsRecheckBothSerialOrders(CorrectSupplierPayment,false)` | Retain true for identical correction contenders; both orders of genuinely different operations remain. |
| `ImmediatePaymentCorrectionDoesNotRestoreDebtTwice` | Transfer bank sum zero and correction count one to `DependentCorrectionRequiresAllocationAuthorityAndPreservesHistoricalAccountSnapshots`, which already proves restored debt, zero payment and exact historical inverse rows. |
| `ReplacementCommitsAfterCoordinationAndEvidenceAreReleased` | Retire generic any-lock resumption example. Exact coordination lock owners remain in both `ReplacementEvidenceWaitsForCompleteCoordinationBeforeLocking` cases, successful settlement in `ExplicitReplacementUsesOneGroupAndCorrectionAuthority`, and accounting-lock execution races. This reduces one execution-specific row-lock integration example; do not claim identical proof. |
| `IndependentPaymentGuardsRejectWithoutPartialWrites(card)` | Retain general forbidden-purpose rejection plus actual Bank/Cash successes. Card has both forbidden purpose and wrong type, so does not independently prove type enforcement. Retire this forbidden enum example and unused account setup. |
| `ActualPastCashPaymentCanUseAnOpenFuturePostingDate(false)` | Retain true: real past cash, future posting, allowed later effective boundary and exact cash result. Explicitly retire interior past-effective example. |
| `ExplicitReplacementUsesOneGroupAndCorrectionAuthority(120,30)` | Retain 80 with group, history, authority and recurrence checks. Explicitly retire larger-than-original successful replacement example; larger-only arithmetic errors might escape the remaining example. |
| `CapturedReportObservesCancellationAtComputationBoundary` | Move cancellation assertion into `CapturedReportReleasesAccountingBeforeResponseProcessing` after its existing lock-release and successful result checks. |
| `UnknownLegacyControlRemainsUnresolved` | Move repeated derivation and incomplete/unresolved/supported-total report assertions into `InvalidHistoricalControlDoesNotPreventSupportedDerivation(true)`, which already corrupts the same legacy SourceRevision and protects immutable rows. |
| `EqualDebtAttributionRelinkCannotGrantHistoricalPaymentAuthority` | Preserve non-bijective hostile relink alongside the existing bijective swap in `HistoricalPaymentProofRequiresExactDebtMovementCoverage`; restore exact links and prove valid control between attacks. Then delete the unique `equalDebtAttribution` seed and `SupplierEvidenceScenarios` fixture if no callers remain. |

## Consolidated 15 additional cases, retaining every probe

- Payment guards: merge amount/date/method contexts (one fewer case); merge stale-funding/over-payment/over-bill contexts (two fewer). Leave archived active account mutations isolated. Merge malformed/duplicate raw JSON contexts (one fewer).
- Allocation guards: seven compatible rejection rows share one real setup; future-funding keeps its distinct setup (six fewer). Restore currency and purchase-order mutations before subsequent probes and assert unchanged financial evidence for each labelled rejection.
- Partial reapplication: both malformed version fields share the existing context and full unchanged snapshot, then one success (one fewer).
- Payment documents: all five faults share one genuine baseline payment; restore document/recovery state between probes and retain rejection, immutable history and receipt checks (four fewer).

Keep each resulting case under the existing 30-second budget. Split an oversized group rather than dropping its guards or extending its timeout.

## Removed work inside retained tests

- Do not record an unused deposit in the three `FailedPostingLeavesNoFinancialEvidence(application:false)` cases. Retain all actual failure stages, unchanged evidence, and successful retry.
- In `RebuildRestoresSupportedAttribution`, retain each corrupted derivation and one restored/idempotent positive cycle rather than repeat the complete successful derivation/report after every fault.
- Fold the missing-evidence-reason assertion into the retained successful retry in `MissingDocumentEvidenceIsExplicitAndReceiptFaultRollsBackEverything`; remove its unrelated first successful payment.
- Remove the experiment's SQL memory limits and `SqlFixtureResourceTests`: two-process observations do not establish a need for new caps or a permanent test protecting the arbitrary 1536 MB choice. Retain temporary measurement telemetry until final comparisons finish.

## Class fixture routing

The existing deterministic weighted partitioner now assigns complete test classes. It still sums trusted row durations, keeps all theory rows together, assigns every discovered case exactly once, and fails on missing/duplicate/skipped/failed evidence. Each class fixture initializes in one process. No lazy preparation is moved inside timed case bodies. It requires enough classes for nonempty partitions and verifies predicted balance before executing. This does not eliminate history shared by different classes or promise ten minutes by itself.

## Verification and coverage

Main at `ce903ea` discovers 1834 server cases. This batch discovers 1804: 15 retired
rows and 15 consolidation reductions. The resource-limit experiment's extra case
was also retired. Main had added six cases since the earlier 1828-case baseline. After these
measurements, [#210](https://github.com/mcunille/Workbench/pull/210) independently
retired five dedicated GEM pilot migration cases at `8c51a86`. Those deletions
belong to that change. This record reports the measured `f5f07ec` inventory; the
expected integrated inventory is 1799 against newer main's 1829. PR integration
checks must verify that count rather than treating it as already measured.

Transferred keepers passed before deletion. The class-cohesion contract failed
before the scheduler edit and passed afterward; complete partition contracts pass.
The focused real-SQL run passed 67 cases. Two compilable mutations were killed:
removing report cancellation checks failed the transferred cancellation assertion;
bypassing immutable payment proof failed the retained hostile-relink assertion.
The initial cancellation mutant failed compilation and is not a mutation kill.
Both production owners were restored and the rebuilt keepers passed. This is
focused mutation evidence, not a whole-suite score.

At `f5f07ec`, the full local gate passed all 1804 server cases exactly once, 512
client cases and 122 browser cases. Gate elapsed was 2907.48s, with the server stage
2798.37s. The hardened SQL-backed container smoke passed, including internal TLS,
secure-cookie login, session durability after application replacement and worker
telemetry. Public CA issuance and SMTP delivery were not exercised.

Fresh serial coverage passed all 1804 server cases with zero failures/skips;
test duration was 54m16s. Fresh client coverage passed all 512 cases.

| Scope | Metric | Baseline covered/total | Batch covered/total |
| --- | --- | --- | --- |
| Client | Lines | 2747/3047 (90.1543%) | 2747/3047 (90.1543%) |
| Client | Branches | 3903/4720 (82.6907%) | 3902/4720 (82.6695%) |
| Server assembly | Lines | 92660/94223 (98.3412%) | 97558/99122 (98.4221%) |
| Server assembly | Branches | 5310/6779 (78.3301%) | 5313/6781 (78.3513%) |
| Unchanged server source | Lines | 92620/94183 (98.3405%) | 92619/94183 (98.3394%) |
| Unchanged server source | Represented branches | 5231/6661 (78.5318%) | 5233/6661 (78.5618%) |

The assembly denominator changed because main added the GEM pilot migration and
its designer after the coverage baseline. The unchanged-source comparison excludes
new files and the changed `CurrentSchema` manifest. Cobertura omits some compiler
branch points from per-line conditions; represented branches are a secondary
projection, with full-package branch counts primary. Each metric satisfies the
5% relative-loss limit; the largest relative loss is about 0.026% for client branches.
Collector settings and assembly scope match the earlier serial coverage baseline.
SQL strings' C# coverage does not measure SQL engine branches; real SQL keepers and
the focused mutation probes remain the evidence for those contracts.

## Hosted measurements and limits

| Run | Source / result | End-to-end elapsed |
| --- | --- | --- |
| [Initial reference](https://github.com/mcunille/Workbench/actions/runs/37030188879) | `0b3f26a`, success, 1720 cases | 49m52s |
| [Newer successful main](https://github.com/mcunille/Workbench/actions/runs/37052931553) | `6bc83da`, success, 1828 cases | 37m05s |
| [Deletion batch](https://github.com/mcunille/Workbench/actions/runs/37063469723) | `f5f07ec`, success, 1804 cases | 41m15s |
| [Matching-schema main repeat](https://github.com/mcunille/Workbench/actions/runs/37061821027/attempts/2) | `ce903ea`, failed | 47m54s |

The candidate is 8m37s faster than the initial reference and 4m10s slower than the
newer successful main. Neither establishes a ten-minute gain. The newer green
main predates the GEM pilot migration; the matching-schema repeat failed because
`ConcurrentPostsHaveOneDurableFinancialResult(sameRequest:true)` returned SQL error 51009
instead of sharing the exact retry receipt. That unchanged test remains in the
batch and passed its hosted and local gates. Failed runs are not successful timing
baselines. Do not attribute separate elapsed savings to deletion, duration weights
or fixture routing from this combined change.

Candidate partitions took 2212.17s and 2197.38s, so imbalance is negligible against
the goal. Unchanged client and browser stages were also slower than newer green
main (140.04s versus 100.99s and 225.68s versus 183.14s), showing material run confounding.
The 74 valid resource samples had zero sampling errors, at least 8.42 GiB available
memory, 45.52% weighted busy CPU and 3.16% I/O wait. Peak usage of any recorded
container was 1.512 GiB; IDs were not image labels. The brief browser overlap had
higher CPU use than the long server-only phase. Average headroom does not prove
safe additional concurrency.

A separate default-storage diagnostic on the local Windows host initialized its
owned SQL fixture in 11.76s and restored three independent current-schema clones
in 0.272s, 0.279s and 0.288s. Each had 8 MiB data and 72 MiB log files. The fixture was
disposed afterward. This is not hosted storage evidence and does not establish a
ten-minute storage opportunity. No storage probe or additional concurrency was
implemented in this batch.

Raw gate, coverage, mutation and resource records remain in ignored `artifacts/`
for this checkout. Keep all case budgets and exact inventory checks when evaluating
follow-ups; further deletion needs its own retained-owner evidence and approval.

## Approved uncapped concurrency follow-up

The approved follow-up varied only hosted partitions/concurrency from two to four.
It retained physical SQL storage, independent databases and authentication, every
test and deadline, the same duration weights, and no SQL/container memory caps.

| Run | Result | End-to-end elapsed |
| --- | --- | --- |
| [Current main](https://github.com/mcunille/Workbench/actions/runs/37072918072) | `8c51a86`, success, 1829 cases | 48m31s |
| [Two-process PR](https://github.com/mcunille/Workbench/actions/runs/37076929038) | `585cc0e`, success, 1799 integrated cases | 47m18s |
| [Four uncapped processes](https://github.com/mcunille/Workbench/actions/runs/37137426410) | `edd9cea`, failed, 1798 passed and one deadline failure | 26m32s |

Current main and the two-process PR have matching production source; main's separate
five-case retirement explains the integrated count. The successful PR is only 1m13s
faster than current main. The failed four-process run does not establish a saving.
Its discovered and executed identities still match all 1799 cases exactly; the gate
correctly rejected the failed result. Client, browser, published-output and hardened
container checks passed.

The failure was `CorrectionAndDependencyCommandsRecheckBothSerialOrders` with
`ReverseSupplierApplication` and `correctionFirst:false`, exceeding the existing
30000ms deadline. This protects financial graph coordination and stale-command
rechecking; retain it and its deadline. CPU samples near the failure were about
96% busy, consistent with contention but not proof of causality. All 47 samples
were valid: weighted CPU busy 71.48%, I/O wait 5.89%, minimum available memory
5.06 GiB, and peak usage of any recorded container 1.396 GiB. Thirty-second samples
cannot exclude brief pressure between observations. Gate time was 1504.00s, server
1377.25s and prerequisites 161.67s; browser time increased to 319.74s versus 292.13s.

Four-process concurrency was reverted under the agreed rejection rule. No tests,
deadlines, caps or storage changed in that follow-up. The subsequent approved
three-process probe is recorded below. The earlier coverage measurements apply to
the approved test edits; these concurrency-only probes do not change coverage source.

## Approved three-process follow-up and repeat

The approved probe changed only hosted partitions/concurrency to three. Production
source, all 1799 integrated cases, weights, deadlines, physical SQL storage and
uncapped engines stayed fixed. Both attempts built revision `409646e`.

| Run | Result | End-to-end elapsed |
| --- | --- | --- |
| [Three-process attempt 1](https://github.com/mcunille/Workbench/actions/runs/37143508177/attempts/1) | success, all 1799 server cases passed | 38m26s |
| [Three-process attempt 2](https://github.com/mcunille/Workbench/actions/runs/37143508177/attempts/2) | failed, 1791 server cases passed and eight deadline failures; browser failed | 38m33s |
| [Restored two-process run](https://github.com/mcunille/Workbench/actions/runs/37139402399) | failed, 1798 server cases passed and one deadline failure | 47m40s |

Attempt 1 was 10m05s faster than successful matching-production-source current main
(48m31s), and 8m52s faster than the successful two-process PR (47m18s). All discovered
and passed identities exactly matched its 1799-case comparator. The five-second
overall target margin justified an unchanged-source repeat. Attempt 2 failed the
agreed reliability gate; do not count it as a saving or retain three processes from
the better run alone. Hosted and local defaults were restored to two processes.
The ten-minute goal remains unproven for a retained reliable configuration.

Both attempts had 68 valid resource samples and no sampling errors. Attempt 1 had
60.41% weighted CPU busy, 5.08% I/O wait, at least 6.92 GiB available memory and
1.472 GiB peak usage of any recorded container. Attempt 2 had 77.98% CPU busy,
0.70% I/O wait, at least 6.56 GiB available memory and 1.596 GiB peak recorded
container usage. Thirty-second samples cannot exclude transient pressure or
establish the cause of a whole-test timeout. Gate times were 2194.40s and 2199.29s;
server times 2030.56s and 1995.11s; browser times 312.49s and 401.32s respectively.
The failed browser report has 121 passed cases and one 30-second timeout in
`inventory.spec.ts`.

The repeat's server deadlines affected partial reapplication, unapplication,
standalone reversal, paid-bill replacement, replacement correction, final-receipt
rollback and two serial-order correction cases. These financial assertions and
every existing deadline remain. The reversal-first correction case also failed
with two processes at about 54.6% sampled CPU busy and 10.46 GiB available memory;
four-process-only contention is not a demonstrated root cause. Historical trusted
data already places this case at 28.747s, and attempt 1 passed it at 28.362s against
its 30-second deadline. Static inspection found no demonstrated coordination defect.
The artifacts cannot separate setup, SQL execution, lock observation and cleanup
costs. A fresh local focused build could not initialize its SQL fixtures because
the local Docker engine was unavailable; this did not reproduce the hosted failure.

Read-only rebalancing with attempt 1's case durations predicts a 74.15s reduction
in the longest case-time sum. This excludes materially different process residuals
and large gaps between classes, so it is not a measured wall-time opportunity.
Tracked weights remained unchanged. Whole-branch review found no critical or
important implementation defects; future optimization should measure the costly
phases and remove justified redundant work while preserving financial safeguards.


## Approved phase-cost diagnostic

The approved temporary, test-only diagnostic ran in [37154088114](https://github.com/mcunille/Workbench/actions/runs/37154088114), head `90d622308a2aa04ac2e911429fca7fb6a436ab5e`, checkout merge `88eae7581c1d013fb74e8c428e01b61a7a91c21d`. Every assertion, test identity, deadline, independent database/authentication, physical SQL file and uncapped engine was preserved at two-process concurrency. Fixed-label elapsed records contained no SQL, parameters, connection values, credentials or exception messages. A formatting failure stopped the first attempt before SQL; the corrected source passed the full formatter and fresh compilation before this run.

Main advanced to `c7f1b22844081c9aec3f953a4b46e67de770b030` (GEM-05) before checkout, adding 48 cases. The probe retained every prior 1,799 PR case plus all 48 upstream additions, with no duplicates or unexpected omissions. The net 30-case difference from current main is the previously approved retirement/consolidation set (38 removed identities and eight added keeper/group identities). The new green main baseline [37153048521](https://github.com/mcunille/Workbench/actions/runs/37153048521) passed 1,877 cases in **50m51** overall; gate 2,941.227s, server 2,740.472s, browser 284.891s. The diagnostic ran **49m07** overall, gate 2,831.687s, server 2,604.093s, browser 307.557s: 1,845/1,847 server cases passed and 121/122 browser cases passed. Its failure cannot establish a retained speed improvement or the ten-minute goal. Older timing comparisons refer to the older main source.

The two whole-case 30-second server deadlines were the historical reversal-first serial-order case and `PaidBillReplacementPreservesCash`. The browser inventory case at line 51 failed after 29.927s; its safe report does not expose the assertion or exact slow step. Earlier successful inventory runs took 24.436–25.724s, and later two/three-process repeats reached their existing deadline.

| Observed phase | Measured elapsed | Interpretation |
|---|---|---|
| Fresh correction context, 13 cases | 1.042–1.559s | Context-only caching is a small candidate, not the dominant cost. |
| Posted-bill precondition, 13 cases | 6.113–7.054s (83.778s total) | Repeated real bill posting is a measurable precondition cost. |
| Existing scenario restore, two cases | 0.442–0.481s | Existing independent snapshot restoration is inexpensive in this run. |
| Full financial snapshot assertions, 11 calls | 0.015–0.236s (0.779s total) | Deleting these assertions is not supported as a useful time saving. |
| Completed context cleanup, 13 cases | 0.285–0.644s | No observed dominant cleanup cost; timed-out late cleanup may be unavailable. |

In the failed serial-order case, the first financial command occupied 13.307s including its barrier wait; first lock observation finished after 2.266s, second observation after 0.467s, and transaction commit after 0.001s. Commit finished at case time 28.340s; the second command was still pending at the case deadline. The timed-out paid-bill case completed a financial command in 15.393s and began its next financial command at 26.686s. These records localize elapsed cost but do not distinguish SQL compilation, CPU execution or other internal waits.

The grouped partial-reapplication case passed in 29.563s: setup 1.135s, bill seed 6.373s, successful reversal/reapplication command 14.874s, cleanup 0.493s. Its three full snapshots totaled 0.198s. The malformed-input probes add some budget cost, but the evidence does not justify blaming the snapshots or removing rollback protection; the dominant command and repeated bill precondition consume most of its headroom.

Resource evidence: 88 valid samples, mean busy CPU 56.257%, mean I/O wait 0.596%, minimum available memory 8.166GiB, peak recorded memory for any single container 1.477GiB. Machine averages cannot explain an individual SQL command's internal cost. Nested phase timings overlap and must not be summed; unmatched begin records identify pending work at the output boundary, not a complete post-timeout trace.

All temporary instrumentation was removed after preserving allowlisted evidence. The pre-rebase test source was verified byte-equivalent to `6ae1fd5`; the branch is then updated to current main. No further optimization, deletion, deadline extension, concurrency increase or permanent diagnostic process is part of this approved probe. Coverage evidence above belongs to the approved deletion batch; this temporary diagnostic neither establishes new coverage nor measures upstream GEM-05 coverage.

## Approved posted-bill and browser follow-up

The next approved design questions and deletes repeated precondition work before
increasing concurrency. Thirteen financial cases now restore genuinely posted
100/150/200/300 bills from the existing immutable supplier-history machinery;
each fixture prepares only the variants its class needs. Every case still receives
an independent physical database, fresh authentication and copied history metadata.
Every financial operation, assertion, deadline and test identity remains. The
existing isolation keeper additionally checks independent posted-bill state and
metadata across later restores. Browser inventory layout checks batch DOM reads
while retaining the same overflow, contrast and visible-target math, thresholds
and full viewport/theme/view matrix. Production source is unchanged from main.

Focused characterization failed on the missing bill history before implementation,
then passed. A meaningful wrong-bill-amount mutant was killed. A stale incremental
binary run after restoring the mutant source was discarded; a forced fresh build
and all 16 affected keeper cases passed. The complete local browser suite passed
122 cases, the client suite passed 512 cases, and the formatter, workflow pins and
partition contracts passed. Internal review found no critical or important defects.

Fresh full-suite coverage against main `c7f1b22844081c9aec3f953a4b46e67de770b030`
passed 1,877 baseline and 1,847 branch server cases, with no failures or skips.
Both reports cover 104,621/106,627 lines (98.119%) and 5,497/7,102 branches
(77.401%): **zero relative coverage loss**. Client source and tests are identical
to main; the fresh shared client suite covers 2,747/3,047 lines and 3,903/4,720
branches. Coverage of C# lines containing SQL does not establish SQL semantics;
the retained financial keepers and focused meaningful mutants provide that evidence.

The full hosted two-process gate on `0dda6c77c0296069a4098d5920e19a61ca5f6378`
[passed in 49m14](https://github.com/mcunille/Workbench/actions/runs/37189363922):
all 1,847 server identities exactly match the pre-optimization inventory, and all
122 browser cases passed. The inventory matrix case took 8.207s. Server stage
2,637.997s, client 197.313s and browser 268.638s. Eighty-eight valid samples had
55.83% weighted busy CPU, 0.54% I/O wait, at least 8.25 GiB available memory and
1.489 GiB peak recorded memory of any container. Against current main's 50m51,
this establishes only a 1m37 overall difference, below the requested ten minutes.

The earlier pre-optimization two-process source `d4dbf9e`
[passed in 34m46](https://github.com/mcunille/Workbench/actions/runs/37157466895)
with the same 1,847-case inventory. Its setup and client stages were also faster;
62 samples show 45.91% busy CPU and at least 8.44 GiB available memory. This large
runner variation prevents attributing its entire 16m05 difference to the deletion
batch. Thirty-second resource samples cannot exclude transient contention.

With correctness and coverage green, the approved three-process experiment now
runs the complete hosted gate twice on unchanged source and unchanged trusted
duration weights. Its four-CPU, 16 GiB runner retains physical SQL storage,
uncapped independent SQL engines and every existing deadline. Accept three only
if **both** runs are green with no budget regressions and each finishes within
**40m51** against current main's 50m51; otherwise restore two. Local defaults
remain two during this hosted experiment.

The first full trial on `f7b786b`
[failed in 39m16](https://github.com/mcunille/Workbench/actions/runs/37192468267).
All 1,847 unique server identities ran and exactly matched the two-process
inventory; 1,841 passed and six hit their existing whole-case deadlines. These
were reversal-first serial-order correction, unsupported corrected application,
previously unapplied embedded payment correction, final-receipt rollback with
reversal, grouped partial reapplication, and paid-bill replacement. The browser
suite passed 121/122 cases. This failure rejects the experiment; an unchanged
repeat cannot satisfy the agreed two-green-run criterion. Hosted concurrency is
restored to two, retaining the verified bill-history and browser optimizations.

Seventy valid resource samples had 79.18% weighted busy CPU, 0.48% I/O wait,
at least 6.35 GiB available memory, and 1.451 GiB peak recorded memory of any
container. The one-minute load peaked at 16.76. These samples suggest materially
greater CPU pressure than the two-process trial, but cannot identify an
individual timeout's cause or exclude brief pressure spikes. No assertion,
financial deadline, SQL memory setting or case identity is removed to make the
probe green. Fresh coverage remains unchanged because source and tests are
identical to the verified two-process revision. The retained configuration's
latest full gate passed in 49m14; **the ten-minute goal remains unproven**.

## Approved MAXDOP experiment (October 8)

The restored two-process revision `72cdd25`
[passed in 47m31](https://github.com/mcunille/Workbench/actions/runs/37194663307).
The next approved bounded experiment questions parallel query plans within each
test SQL engine while three engines share a four-CPU runner. It removes that
additional level of parallelism by setting `max degree of parallelism` to one
only in disposable server test fixtures. An effective-setting query and startup
guard ensure the engine applied the requested configuration before schema or case
execution. The existing isolation keeper failed on the guard before the setter
was added; its captured output reported the engine's original setting as zero.
This validates experiment configuration, not a claim that a particular failed
financial command previously used a parallel execution plan.

Every case, financial assertion, 30-second deadline, physical database,
authentication boundary, uncapped memory setting and duration weight remains.
Production configuration and SQL are unchanged. Fewer parallel query workers may
reduce contention, but queries can become slower and the suite no longer covers
parallel query plans under this setting. These tradeoffs require measurement;
the prior CPU samples do not establish the cause of individual deadlines.

Before this experiment, main advanced to GEM-06 revision
`afdde62dddee4835a16eb8ccde86a318f8cc553c`. The branch was rebased without conflicts.
Its [green main baseline](https://github.com/mcunille/Workbench/actions/runs/37217965497)
passed all 1,998 server cases in **37m04** overall: server stage 2,021.745s,
client 98.874s, browser 186.980s, prerequisites 133.285s. Main added 121 cases;
the branch's expected inventory is 1,968 after the approved net retirement of
30. Source changes and faster setup/client stages mean older timings cannot
establish a ten-minute improvement against this new baseline.

The approved probe runs the complete three-process hosted gate twice on unchanged
source, records CPU/memory and effective MAXDOP, and retains the original 40m51
ceiling. A ten-minute claim against the newer 37m04 baseline additionally requires
**27m04**. Do not use the older 50m51 comparator to claim the goal on newer source.
Acceptance requires both green full gates, exact inventory coverage, no deadline
regressions and at most 5% relative coverage loss. Otherwise restore the prior
engine setting and two processes. All 16 fresh focused financial/isolation cases
passed with effective MAXDOP one. The branch has 1,968 ordinal-unique discovered
identities: all 121 upstream additions remain, and the approved retirement set is
unchanged at 38 removed examples and eight keeper/group identities. Fresh client
coverage matches GEM-06 main exactly across lines, branches, statements and
functions (512 tests passed on each side). Formatter, workflow pins and partition
contracts passed; fresh internal review found no critical or important defects.
Hosted probe results and restored-source verification are recorded below.

The first full hosted MAXDOP trial on `b9b85d8`
[failed in 37m42](https://github.com/mcunille/Workbench/actions/runs/37877891308).
All 1,968 unique server identities ran, with 1,966 passed and two existing
whole-case deadlines: reversal-first serial-order correction and grouped partial
reapplication. All 122 browser cases passed. Each of the three SQL fixtures
reported effective MAXDOP one. Sixty-seven valid resource samples had 78.07%
weighted busy CPU, 0.75% I/O wait, at least 6.39 GiB available memory and 1.441 GiB
peak recorded usage of any container; peak one-minute load was 17.29.

This run is 38 seconds slower than pinned GEM-06 main's 37m04 and fails reliability;
it establishes neither a retained saving nor the ten-minute goal. The CPU samples
do not show a large reduction from the prior three-process experiment, whose
production baseline differs, and cannot establish a timeout cause. Because the
first run failed, no repeat can satisfy the agreed two-green-run criterion.
The temporary MAXDOP setter, effective-setting guard and recording are removed,
and hosted concurrency returns to two. Source, tests, scripts and workflow match
the rebased pre-probe revision `d94c49d`; production remains identical to GEM-06
main. Current/default documentation again describes the retained two-process
configuration. The rejected probe's local collector also hit a reversal-first
deadline; it was cancelled rather than used as passing coverage evidence.

The restored revision `85f0c43`
[passed its full hosted gate in 37m00](https://github.com/mcunille/Workbench/actions/runs/37880982290).
All 1,968 unique server identities passed and exactly match the rejected probe's
inventory; all 122 browser cases passed. The server stage took 2,011.535s,
client 106.437s, browser 184.666s and prerequisites 140.886s. Sixty-six valid
resource samples with no sampling errors show 44.79% busy CPU, 3.32% I/O wait,
at least 8.04 GiB available memory, 1.535 GiB peak recorded usage of any
container and peak one-minute load 9.28. These are sampled observations, not
proof of an individual failure's cause or absence of brief pressure spikes.

Against pinned GEM-06 main's 37m04, the four-second difference establishes no meaningful
saving and does not meet the ten-minute goal. Untouched GEM-06 main's full local
coverage run passed all 1,998 cases with zero failures or skips: 110,500/112,534
lines and 5,897/7,528 branches. Its XML, TRX and log were preserved and the copied
XML's hash verified. The restored-source serial collector stopped before producing
a report. Its log records 28 failures: 26 from a shared coordination-history SQL
setup timeout, one atomicity fault-injection timeout and one recognition-matching
failure. A fresh isolated correction class run with collection passed 25/26;
the receipt fault-injection case received command timeout -2 instead of injected
error 51995. Its exact two-case method subsequently passed without collection
using the same verified separate build. These observations do not establish a
root cause or fix; failed or interrupted collectors are not passing verification.

On October 11, coverage verification restarted using the green hosted run's two
cohorts (996 and 972 cases) and separately built and instrumented assemblies.
Collector inclusion remains Server and Database;
no test, assertion or deadline changes. Matched Debug collection finished on
October 12: all 1,968 unique identities ran with exact hosted inventory parity,
1,967 passed, one failed and none skipped. Cohort one passed 995/996 in 42m41;
cohort two passed 972/972 in 42m16. The failed collector is not passing reliability
or final coverage acceptance evidence.

The first isolated cohort attempt hit 32 operator-command setup failures because
its relocated build output did not match the test helper's database CLI lookup.
That attempt was stopped and excluded. Copying the freshly built CLI to the
helper-required ignored output location, with its hash verified, allowed all 32
affected cases to pass. The second Release cohort passed all 972 cases with exact
inventory parity, but its coverage cannot be directly compared to the saved Debug
baseline. The remaining Release collector was cancelled and both cohorts rebuilt
in Debug. Both builds passed with zero warnings or errors. The stop guard initially
matched both parent and child processes, delaying cancellation; a brief third owned
fixture overlap during transition cannot be excluded. The final retry used two
cohorts. No stale standard-output CLI is used. Other
checkout tests started on the same host during collection; local timings are
therefore not controlled CI performance measurements.

The matched Debug retry encountered the existing 30-second replacement-preview
deadline in `PreviewIncludesReplacementEffectsAndRejectsClosedReplacementWithoutWrites`.
The case body is identical to pinned GEM-06 main. Post-failure numeric SQL samples
showed no blocking and roughly CPU-bound active requests, but do not establish the
failure's cause. No fix is claimed and no assertion or deadline is relaxed.

Native Coverlet 6.0.4 summaries reproduce each original collector report's counts.
The merge normalizes exactly two generated-document paths and their file-local
type hashes; complete instrumentation structures then match after excluding hit
counts, with collision checks. Native merging preserves both baseline denominators
rather than double-counting generated code from the isolated build directories.
Original JSON/XML reports and the normalization record remain in ignored local
artifacts. The measured counts are:

| Scope | Metric | Pinned GEM-06 baseline | Restored measurement | Relative loss |
| --- | --- | --- | --- | --- |
| Server/Database collector | Lines | 110500/112534 | 110499/112534 | 0.000905% |
| Server/Database collector | Branches | 5897/7528 | 5895/7528 | 0.033916% |
| Client | Lines | 2747/3047 | 2747/3047 | 0% |
| Client | Branches | 3902/4720 | 3902/4720 | 0% |

Each numerical loss is below 5%, but the one failed local case remains a verification
limit. These counts do not turn that collector green or establish SQL-engine branch
coverage. The restored hosted full gate above passed every case; no ten-minute
saving or completed root-cause fix is claimed.

Main has since advanced to `8067146`, including GEM-07 through GEM-09. These
measurements retain `afdde62` as their pinned GEM-06 baseline; they do not establish
a saving against newer main. A future timing claim requires a matched newer source
baseline and cannot inherit the older 50m51 reference.

The retained hosted TRX reports sum to 3,424.253 seconds of case execution across
two serial workers. With those observed case durations unchanged, ideal balancing
and zero setup still require 1,712.126 seconds (28m32) before any other gate work.
That exceeds the pinned baseline's 27m04 whole-gate target. Fixture startup and
balancing alone cannot close this gap with those case durations unchanged.
Optimizing preconditions inside cases can change the case-work bound. Runner
variation prevents treating this conditional bound as a prediction of future timings.

## October 12 rebase and verification

The retained change was rebased onto main `8fe565a` (GEM-07 through GEM-09 and
the shared-skill routing update). Main independently adopted the browser inventory
DOM batching, so the conflicting branch helper was dropped in favor of main's
implementation. Production source and `inventory.spec.ts` are identical to this
new base. The retained scheduling, snapshot and test-retirement changes received
a fresh read-only internal review with no critical or important findings.

The coverage and hosted timing measurements above remain pinned to GEM-06 main
`afdde62` and restored revision `85f0c43`. They are historical evidence, not
coverage or timing measurements of the newly rebased source. Current-source
verification uses an isolated Node.js 26.7.0 installation because the machine's
global Node version has advanced; repository toolchain pins remain unchanged.

Exact main `8fe565a` subsequently
[passed run 38216139502, attempt 1](https://github.com/mcunille/Workbench/actions/runs/38216139502/attempts/1):
1,999 unique server cases passed across its two partitions and 141 browser rows
passed. Its source gate took 3,338.246s (55m38), including 3,153.260s for the
server stage. These timings differ substantially from the earlier pinned baseline;
they require a comparable current-source hosted branch run before attributing
any saving. The rebased branch discovers 1,969 server cases, retaining the net
30-case reduction and the new upstream case.

The first local post-rebase gate passed its build and 633 client cases but rejected
a documentation edit made after the build receipt. Its source-provenance guard
correctly prevented publication. The two ownership-verified testhosts were stopped
and this attempt is excluded from passing full-gate evidence. The retry freezes
tracked files throughout verification.

The frozen retry passed `verify.ps1 -SkipDependencyInstall` on revision `7618184`
using the dependencies installed by the first attempt. All 1,969 unique server
cases passed exactly once (1,045 and 924), all 633 client cases and 141 browser
cases passed, and the current-source published-output probe passed. Build,
formatting, API drift, documentation and client checks passed. Gate
`0099e378ac9047bdb07a01c13f2a3351` took 1,855.747s (30m56), with 1,736.005s
in the server stage, on a 16-CPU local host. This is not a hosted CI timing
comparison. The subsequent `smoke-container.ps1 -ComposeIngressPrefix '172.30'`
passed the hardened SQL-backed runtime and local Compose checks using disposable
resources. Public CA issuance and SMTP delivery remain outside that smoke test.

At that checkpoint, current-source non-collector verification was green and the
fresh matched coverage comparison was pending. The older failed collector remains
failed; the following fresh comparison supplies separate current-source evidence.

## Fresh matched coverage and repeated hosted measurements

Main source `8fe565a` and retained head `35fbb83` were collected in Debug using
Coverlet 6.0.4 with the same Server/Database inclusion. Each used two separately
built, independently instrumented assemblies and its exact passing hosted cohorts.
All 1,999 main cases and all 1,969 branch cases passed exactly once, with no missing,
duplicate, failed or skipped identities. Main cohorts passed 985 and 1,014 cases;
branch cohorts passed 1,045 and 924. Both paired client collectors passed all 633
cases across 94 files using pinned Node.js 26.7.0 and one worker. Local SQL
collection stayed at two owned fixtures, and client collection ran between the
baseline and branch SQL collections.

The baseline source was an exact Git archive inside the existing isolated
workspace. Its generated assembly informational version inherited parent head
`35fbb83`, matching the branch's display metadata; it did not carry `8fe565a` as
its binary version identifier. The source archive hash and original reports are
retained. Fresh database CLI outputs were copied to each existing helper's expected
ignored location and their hashes verified. No stale standard-output CLI supplied
the evidence. Native summaries reproduce every original XML report. Merging
normalizes exactly two generated-document paths and file-local type hashes with
collision guards and complete instrumentation-shape checks. Mapping source roots
also proves identical production probe identities across baseline and branch.

| Scope | Metric | Main `8fe565a` | Branch `35fbb83` | Relative loss |
| --- | --- | --- | --- | --- |
| Server/Database collector | Lines | 110502/112537 | 110502/112537 | 0% |
| Server/Database collector | Branches | 5895/7528 | 5895/7528 | 0% |
| Client | Lines | 3489/3829 | 3489/3829 | 0% |
| Client | Branches | 4969/6005 | 4965/6005 | 0.080499% |
| Client | Statements | 4865/5613 | 4863/5613 | 0.041110% |
| Client | Functions | 1408/1639 | 1408/1639 | 0% |

All measured losses satisfy the 5% relative limit with passing collectors.
Client implementation and tests are unchanged from the baseline; their small
counter differences are observations rather than a client test retirement.
Coverlet measures .NET probes, not SQL-engine branches. These fresh passing
collectors supersede the pending coverage acceptance above, but do not establish
a root-cause fix for the historical failures.

Hosted repetitions retained identical source within each pair, two SQL workers
and four reported CPUs per runner. End-to-end elapsed time runs from the attempt's
start to completion of its required aggregate job, including setup and queuing
within that interval.

| Source and attempt | Full CI | Source gate | Outcome |
| --- | --- | --- | --- |
| [Main 1](https://github.com/mcunille/Workbench/actions/runs/38216139502/attempts/1) | 57m23 | 55m38.246 | Passed: 1999 server, 141 browser |
| [Main 2](https://github.com/mcunille/Workbench/actions/runs/38216139502/attempts/2) | 50m44 | 48m47.038 | Passed: 1999 server, 141 browser |
| [Branch 1](https://github.com/mcunille/Workbench/actions/runs/38226111747/attempts/1) | 46m13 | 44m42.564 | Passed: 1969 server, 141 browser |
| [Branch 2](https://github.com/mcunille/Workbench/actions/runs/38226111747/attempts/2) | 46m51 | 45m11.996 | Failed client focus assertion; all 1969 server and 141 browser cases passed |

The first paired difference is 11m10, but main's repeat is 6m39 faster than its
first attempt. The green branch run is only 4m31 faster than that repeated main
baseline. The failed branch gate is ineligible as passing performance acceptance.
These observations do not establish a reliable ten-minute improvement; no overall
objective completion is claimed.

The repeated branch gate passed 632/633 client cases. Its sole failure was
`associates server field and source errors with the editable controls` in
`GemTenantEditor.test.tsx:198`: the alert existed but the heading held focus.
Both component and test are unchanged from pinned main, as is the gate's
single-worker client stage. Twenty unchanged focused local repetitions passed;
they neither reproduce nor fix the hosted failure. Waiting for DOM presence before
asserting focus is a synchronization hypothesis, not an established root cause.
No assertion, deadline or client source was changed.

| Sampled branch resources | Attempt 1 | Attempt 2 |
| --- | --- | --- |
| Valid samples / sampling errors | 83 / 0 | 84 / 0 |
| Weighted busy CPU | 57.75% | 43.98% |
| I/O wait | 0.47% | 2.90% |
| Minimum available memory | 8.38 GiB | 7.43 GiB |
| Peak recorded usage of any container | 1.497 GiB | 1.557 GiB |
| Peak one-minute load | 14.78 | 9.67 |

These sampled values do not establish the focus failure's cause or exclude brief
pressure spikes. No new memory caps, SQL engine settings or worker increases were
retained. A proposed two-runner capacity spike remains unapproved and unimplemented;
the retained batch continues to use one runner with two server workers.
