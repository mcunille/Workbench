# Duration-balanced server partitions

**Status:** Implemented; verification evidence recorded below.

## Scope and decision

Issue #113 requests runtime balancing within the existing partition contract. Keep whole
methods (including every theory row), 2–4 processes, sequential collections, disposable SQL
fixtures, exact discovery/result coverage, and failure propagation unchanged. Class grouping
is not introduced: the supported unit was already a method. No application contract changes.

Sum historical TRX row seconds for each method. Assign methods in descending predicted duration
to the lowest-total partition, breaking ties by ordinal method name and numeric partition ID.
Sort theory rows ordinally before summing and emitting inventory. New or renamed display names
receive **1 second per row**, including new rows of an existing theory. Obsolete rows are ignored
for scheduling but still validated. This fixed fallback avoids dataset-dependent behavior and
lets every new test run; it cannot predict an unusually expensive new test.

Count balancing is retained only as the historical comparison; it misses known runtime skew.
Increasing concurrency or weakening isolation is outside scope. Measurements are estimates:
fixture startup, shared runner pressure and build/discovery overhead are not TRX test durations.

## Dataset and trusted refresh

`scripts/server-test-durations.json` is reviewed, committed JSON data, schema version 1.
Each run records SHA256 of its exact bytes, source revision/run URL, schema version and fallback
in `duration-dataset.json`; `inventory.json` records predicted totals and fallback counts.
Missing/malformed datasets fail closed rather than silently selecting a different strategy.
An explicit `-TimingDataPath` permits controlled comparisons with other reviewed datasets.

Refresh only from a completed successful **CI push to main** in this repository. Verify the
run event, branch, conclusion and SHA using `gh run view` before downloading its
`verification-evidence` artifact. Never refresh automatically from pull-request artifacts.
The importer validates successful gate evidence, partition inventories, exit receipts,
exact passing TRX coverage and finite nonnegative durations. XML DTDs are prohibited;
artifacts are never executed. Provenance parameters describe evidence the operator verified;
the importer cannot authenticate downloaded local files. Zero-resolution rows use 0.001 second.

```powershell
./scripts/update-server-test-durations.ps1 -EvidenceDirectory <gate-directory> `
    -SourceRunUrl https://github.com/mcunille/Workbench/actions/runs/<run-id> `
    -SourceRevision <full-commit-sha> -OutputPath scripts/server-test-durations.json
./tests/Workbench.BuildTests/ServerDurations.Tests.ps1
./tests/Workbench.BuildTests/ServerPartitions.Tests.ps1
```

Review the data diff and provenance together. Refresh after substantial test changes or observed
imbalance, not during a running gate. Revert the dataset/scheduler commit to restore prior behavior;
there is no database migration or production operation.

## Acceptance and verification

Contracts cover skewed durations, theory aggregation, ordinal identities, reordered discovery,
new-test fallback, invalid weights, dataset identity and the existing coverage/filter/failure
checks. CI executes both partition and import suites. Benchmark with the same partition count,
concurrency, test inventory and runner class after the controlled-clock export fix (#115).
Compare actual partition wall times and the server stage, not only predicted sums. A single
pair is observational evidence, not a promised speedup.

Baseline: successful main CI run [35072732163](https://github.com/mcunille/Workbench/actions/runs/35072732163),
revision `c893643e3d5ab4a9bed75eaae2fbef736539f816` (after #115), two partitions/concurrency two,
four reported processors. Partition wall times: **492.573 s / 433.985 s**; server stage:
**498.323 s**. These are the source measurements for the committed dataset.

The fixed dataset contains 866 rows. Replaying it over the old equal-count assignment predicts
401.660 s / 363.719 s; duration assignment predicts 382.690 s / 382.690 s (474 / 392 rows).
Adding one synthetic unseen fact exercises the 1 s fallback and predicts 383.190 s / 383.190 s.
These are scheduling estimates, not measured wall times.

Focused partition/import suites pass, including existing filter and exactly-once contracts.
Ten targeted manual mutation probes were killed: reversed sort, count-based placement, incorrect
fallback, omitted theory-row weight, case-insensitive identity, accepted failed gate, accepted
nonzero exit, accepted failed result, omitted per-partition coverage, and incorrect zero clamp.
An additional regression rejects partition-total overflow from corrupt extreme weights.
