# Two-runner CI verification

Status: approved design. Hosted acceptance measurements are tracked in PR #212.

## Outcome and evidence

Reduce passing end-to-end CI by at least ten minutes against a comparable
passing main baseline, while losing at most 5% relative measured coverage.
Retain the approved test consolidations and all remaining financial guards.
Each four-CPU hosted runner may execute at most two SQL test workers.

The passing `3d53e20` experiment compared the same GitHub merge revision,
`c29d021562085b78ef07213b3697b74e83218225`, in both paths. The full control
finished in 58m28s; its server stage took 53m03s. The two-runner server probe
finished in 29m48s including setup. All 2,068 server cases passed in both
paths, with exact inventory agreement. The probe used 56m41s of combined
runner time, averaged approximately 47–48% busy CPU per runner, and retained
at least 10.2 GiB available memory. Those measurements establish feasibility,
not an integrated end-to-end saving. Container samples include Testcontainers
cleanup helpers as well as SQL containers; four sampled containers do not
mean four SQL workers.

The earlier 5% coverage acceptance belongs to the historical `8fe565a`
baseline. Current coverage must be compared afresh after main's dependency
and collector updates. Do not add independent best-run savings together.

## First-principles decision

The requirement is complete, credible verification within the time and
coverage limits; a single machine is an implementation choice. Earlier
single-runner concurrency trials did not provide reliable acceptance.
Further pruning remains possible, but must independently justify each
removed contract. The measured two-runner layout offers enough margin to
meet the goal while preserving the current test inventory.

Delete duplicate server execution from the source/release job and retire the
throwaway probe workflow and copied runner. Reuse the existing partition
contract rather than keep a second scheduler. Only then accelerate execution
by distributing the four whole-class cohorts across two runners. Automate
the final aggregate completeness check once those ownership rules are clear.

## Job graph

The existing source/release job keeps locked restores, formatting, build,
API-contract generation/drift checks, documentation/tooling contracts,
client tests, browser tests and published-output checks. It stops executing
server integration tests. Its successful receipt explicitly describes a
source/release subset, never a complete verification gate.

Two independent server jobs start alongside it. Each performs a fresh locked
Release build from its checked-out revision, discovers the complete server
inventory, and uses the same trusted timing dataset to construct four
whole-class cohorts. Host one executes cohorts 1 and 2; host two executes
cohorts 3 and 4. Each host runs at most two workers. Independent databases,
fresh authentication, case assertions and existing case deadlines remain.
There is no cross-runner build-artifact dependency: the probe already
measured the small repeated build cost, and independent builds avoid adding
artifact provenance and download delays to this first integration.

The existing required verification job retains its required-check name. It
depends on the source/release job, both server jobs, and the other currently
required jobs. A missing, skipped, cancelled or failed job fails the gate.
Keep matrix fail-fast disabled so evidence from the other host survives.

## Verification module and interface

Use `scripts/server-partition-contract.ps1` as the owner of scheduling and
exact coverage validation. Extend `scripts/test-server-partitions.ps1` to
accept an explicit subset of partition IDs, retaining full execution by
default. Reject duplicate, unknown or empty selections. Partial execution
must emit a partial receipt and must not report complete inventory coverage.
Retain solution-wide test-project discovery: a new unsupported test assembly
must fail rather than disappear from the gate.

Add a named stage-group selection to `scripts/verify.ps1`: `All` remains the
default and runs client, server, browser and published stages. `SourceRelease`
runs client, browser and published stages after the normal prerequisites.
Its receipt records that group and its exact required stages. Do not add a
general success override or a flag that silently treats absent tests as
passing. Existing local invocation stays complete with two server workers.

The final aggregate validator consumes the source/release receipt and both
server receipts and artifacts. It requires:

- Successful results for every currently required job and stage.
- The same actual checkout SHA and checked-out source identity across jobs;
  record the PR head separately because the merge checkout can include newer
  main changes. Reject stale or mixed source artifacts.
- The same trusted duration-dataset hash and complete discovered inventory
  on both hosts, including case-sensitive theory identities.
- Exactly the four expected, nonempty, distinct cohort assignments, with
  every discovered test assigned exactly once and whole classes co-located.
- A zero native process exit and an exact passing TRX result for every
  assigned case, with no absent, duplicate, extra, skipped or failed rows.
- Current unique invocation directories and the expected supported solution
  test-project inventory. Old artifacts cannot satisfy a missing result.

Keep receipt and aggregation logic inside this verification module. Expose
only the stage group, cohort selection and evidence locations needed by the
workflow. Do not introduce general scheduling configuration or production
test seams for this change.

## Verification and acceptance

Use focused TDD at the orchestration interface for partial selection and
aggregate admission: valid whole coverage succeeds; missing host/cohort,
overlap, stale source, mismatched inventories/dataset, unsupported assembly,
native failure and non-passing TRX fail. Extend the current primary contract
tests instead of duplicating their existing per-TRX checks at another layer.
Use Gherkin comments and scoped mutations of meaningful admission guards.

Run affected PowerShell contract suites, action-pin checks, formatting,
documentation checks, full passing CI and hardened container smoke as
required. Application behavior is unchanged; no preview deployment is
needed. A test failure must be investigated, not hidden by a looser assertion,
deadline or successful subset receipt.

Require fresh matched client V8 and Server/Database collector evidence at
the same tool versions and production probe identities. Merge native
coverage reports with verified denominators and exact case inventories;
coverage loss is `(baseline-covered - candidate-covered) / baseline-covered`
for equal-denominator metrics. Report SQL-engine coverage limits separately.

Compare complete passing main and candidate runs, including queue, setup,
build and required-gate completion. Repeat the integrated candidate at an
unchanged source revision to expose variance; investigate contradictory
results rather than select the fastest sample. Record CPU, available memory,
container memory, SQL worker limits and combined runner minutes. An isolated
server-stage improvement alone does not complete the ten-minute goal.

## Delivery and recovery

Remove throwaway experiment code after recording its evidence. Update the
maintained CI/test guidance and PR description around the final job graph,
acceptance results and limits. Keep raw logs and coverage artifacts ignored
or in hosted artifacts. Commit and push the verified change to PR #212 for
review; merging remains separately authorized.

If the new graph fails acceptance, restore server execution inside the
source/release job and remove its distributed replacements together, leaving
the required gate complete. Preserve previously approved test optimizations.
Do not leave both complete server paths enabled as the permanent arrangement.
