# CI test work deletion and class fixture routing

**Status:** Implemented and verified; the ten-minute elapsed-time goal is not demonstrated.

The goal is at least ten minutes less hosted end-to-end CI, with client and Server assembly line/branch coverage losing at most 5% relative to the comparable baseline. This batch retains two server processes on one runner.

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
