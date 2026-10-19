# Distributed server verification

Status: specification and implementation plan approved; native implementation in
progress. Full-gate performance and coverage acceptance remain pending.

## Outcome and retained work

Reduce the comparable, successful canonical full CI gate by at least 300 seconds,
with no more than 2% change in code coverage under identical instrumentation.
Preserve every justified verification boundary and every discovered test case.
The owner is the requesting maintainer; the full-gate and coverage criteria come
from the original test-audit request. Faster failed runs do not satisfy it.

This design builds on fixture commits `586788f` and `fca470a`. Credential-free
recognition, payment and correction input templates remain; each case retains
fresh databases, credentials, proof keys, security stamps, sessions and maps.
Asserted financial transitions, migration paths and separate payment/correction
permissions remain real. No tests have been deleted. The scheduling change does
not expand fixture reuse or change production behavior.

Existing verification remains authoritative until this design is implemented and
measured successfully. Single-host defaults remain two partitions/two workers.

## Evidence and first principles

Apply the [five-step algorithm](../development-workflow.md#first-principles-the-five-step-algorithm)
in order:

1. Question requirements: real SQL authorization, financial transitions, tenant
   isolation, migrations, recovery and release checks have distinct regression
   claims. Keep them. Single-machine execution is an implementation choice, not
   a correctness requirement. Test deadlines remain unchanged.
2. Delete unnecessary work: the retained fixture changes remove repeated
   immutable setup. The broad audit found no justified deletion worth five
   minutes from the critical path. Further speculative test removal is outside
   this design. Do not add a second full server run to routine CI.
3. Simplify: discover once and produce one deterministic partition plan. Build
   once and transfer validated outputs. Reuse existing inventory/result contracts
   and keep one strict aggregate rather than independent scheduling algorithms.
4. Accelerate: run four partitions on two hosts, with at most two server-test
   processes per host. Keep client/browser/published workloads off those hosts.
   Measure the entire gate, including the new transfer and queue overhead.
5. Automate last: adopt distributed execution in required CI only after contract
   checks, complete successful gates and the performance/coverage criteria pass.

The original-baseline fixture comparison did not establish a five-minute gain.
The [successful scheduling probe](https://github.com/mcunille/Workbench/actions/runs/38205135338)
ran the same candidate with two then four workers on one host: 2392.778 seconds
versus 2004.874 seconds, a 387.904-second saving. Both full gates passed.
The [reverse confirmation](https://github.com/mcunille/Workbench/actions/runs/38211134897)
failed with four workers: 42 server failures, one client deadline and one browser
deadline. The original two-worker baseline passed in 2976.183 seconds. Its
348.562-second difference from the failed candidate is excluded from acceptance.

Confirmation sampling recorded CPU busy at 86.59% versus 57.24%, and Linux CPU
some-pressure at 35.76% versus 10.06%, for four versus two workers. Minimum
available memory was 4.13 versus 7.71 GiB. These observations support investigating
CPU contention; they do not prove every failure's cause or memory exhaustion.
Four workers on one standard host are rejected as a delivery setting.

Full hosted before/after coverage for `35adcd5` and `fca470a` was identical in all
measured counts and denominators: server lines/branches and client
lines/branches/functions/statements. Server instrumentation loaded
`Workbench.Server`; it did not establish Database CLI coverage. This evidence
remains valid for the fixture patch; final integrated source needs a comparable
coverage check for the delivered change.

Alternatives considered:

| Approach | Decision |
| --- | --- |
| Retain two workers on one host | Safe fallback; has not met the requested improvement. |
| Four workers on one standard host | Reject after failed confirmation; do not retry until green. |
| Four partitions on two standard hosts | Selected; separates server load while preserving strict coverage, with transfer, queue and runner-minute costs to measure. |

Larger paid runners, more than two server hosts, authentication-template work,
deadline changes and timing-data retuning are outside scope.

## Job topology and responsibilities

Use the current pinned Linux toolchain and standard runner class throughout.
There are two dedicated server hosts, in addition to the existing source/release
host and independent setup/deployment/smoke jobs.

1. **Source preparation** checks out the immutable event
   revision, runs orchestration contracts, locked dependency installation,
   formatting, documentation checks, the complete client stage, Release build,
   OpenAPI generation/drift detection, client typecheck/build and test discovery.
   It publishes the transport bundle and partition plan, then completes. Client
   checks belong to preparation. All three consumer jobs depend directly on
   preparation; a separate dispatcher job is unnecessary.
2. **Release verification** consumes validated release artifacts on its own host
   and runs the existing browser and published-output checks using a validated
   local manifest. Browser worker limits and safe failure diagnostics remain
   unchanged. Separating this job from preparation lets server execution start
   without waiting for browser/published completion.
3. **Server host A** executes global partitions 1 and 2 concurrently; **server
   host B** executes partitions 3 and 4 concurrently. Host pairing uses the
   existing duration estimates to balance the sum of each host's assigned work
   deterministically. Partition IDs are stable within the plan, not independently
   recomputed on each host. Each test process owns its SQL fixture/container.
   No browser, publish probe, client build or application build runs on these
   hosts while server cases execute.
4. **Verification aggregate** validates preparation, release verification and
   both server jobs, then validates their evidence. The existing required gate
   also requires local setup, deployment and hardened-container smoke jobs.
   Preserve other existing CI checks, including design-tooling contracts.

The preparation/release split means browser/published artifacts must cross hosts
too. Use the transport contract below for their published server/database trees
and built client assets. After validation, create a local manifest bound to the
consumer's paths and the verified transport receipt. Never accept an arbitrary
caller-provided replacement for the producer receipt. Browser and published
checks stay on the same release-verification host with their existing scheduling.

## Portable build and partition contract

Keep the current single-host `verification-artifacts.ps1` version-1 path-bound
contract intact. Add a separately versioned transport envelope for distributed
execution; do not turn off source, configuration, output or current-run checks.

The producer records:

- Repository identity, immutable checkout SHA (including PR merge SHA when that
  is the event revision), workflow identity, GitHub run ID and run attempt, plus
  a fresh gate invocation ID. All consumers compare against their trusted job
  context and the preparation job's declared output, not only the bundle itself.
- Pinned SDK/Node/npm versions, OS/architecture, Release build configuration,
  source inventory hash before generation and after validated generation, and
  content hashes for every transported file.
- The complete solution test-project inventory, discovered case identities,
  discovery output, duration-data hash and deterministic four-partition plan.
- Explicit allowlisted relative roots: each required test assembly's complete
  Release runtime output, published server/database outputs and built client
  assets needed by release verification. Include copied test content and runtime
  dependencies. Exclude credentials, SQL backups, caches and unrelated artifacts.

Paths must be relative and remain inside the fresh extraction root after
normalization. Reject rooted paths, traversal, duplicate paths, symlinks and
unexpected files. Bind file hashes to their paths with ordinal comparison and
canonical separators. Reject missing files and hash mismatches. Artifact names
include run attempt and invocation; downloads select exact producer artifact
identities from this workflow run. Partial reruns must rerun preparation and all
dependent verification jobs together; earlier-attempt evidence is rejected.

Consumers check out exactly the producer revision and validate current tracked
source, generated contracts and the transported source hashes before executing.
They may install the pinned runtime/test tooling but must not rebuild or silently
restore missing output into the bundle. Execute tests through the built assembly
with VSTest, retaining the existing runsettings and TRX format; characterize
assembly execution against the existing project command before adoption. Complete
runtime output and the matching source checkout supply test data and source-path
dependencies. Any unsupported relocation fails explicitly rather than falling
back to unverified binaries. No MSBuild `obj` tree is transported or trusted.

Use the existing solution test-project inventory check during preparation so a
new test project cannot silently disappear. Unknown assembly/discovery formats,
empty selections and unknown plan versions fail closed. The producer owns global
discovery and scheduling; consumers accept only their two assigned plan IDs.

## Exact-once aggregation and failure handling

Each partition writes a receipt bound to the transport digest, plan digest,
revision, run/attempt/invocation, partition ID, assigned case identities, exit
status, TRX digest and elapsed time. Use the existing partition result validator
for case outcomes. Retain full theory-case identities using ordinal,
case-sensitive comparisons: the existing suite contains identities differing
only in case. Method-level filters must preserve all assigned theory cases.

The aggregate requires exactly four distinct expected partition receipts. Their
case sets must be disjoint and their union must equal global discovery exactly.
Every discovered case must have one passing outcome. Missing, duplicated,
unexpected, skipped, failed or aborted cases fail the gate, as do nonzero process
exit codes, missing evidence, changed binaries, replayed attempts, or cancelled
and skipped jobs. Identical duplicate evidence is still a duplicate and fails.

Upload non-sensitive receipts, inventories, logs, timings and TRX under
`always()` without treating successful upload as successful execution. Avoid
matrix fail-fast so sibling evidence can finish. Join owned processes and clean
up only resources created by their own job. No automatic test retries, deadline
increases or success inferred from another host's results.

The required aggregate uses `always()` and explicit expected job IDs; it must
reject missing dependencies as well as non-success states. Keep its externally
required check name stable. Branch-protection configuration remains maintainer
controlled and is not changed by this work.

## Local interface and affected boundaries

`scripts/verify.ps1` remains the canonical local full gate, with default 2/2
execution. Extract shared preparation, release verification and evidence
aggregation operations so local and distributed modes call the same checks.
Distributed mode exposes explicit prepare, assigned-partition, release and
aggregate commands; these are incomplete phases, never advertised as a full
gate individually. It must not introduce a generic skip-server success mode.

Expected implementation scope: `verify.ps1`, `test-server-partitions.ps1`,
`server-partition-contract.ps1`, `verification-artifacts.ps1`,
`verification-stages.ps1`, small focused orchestration entry points, CI wiring,
owning BuildTests, and CONTRIBUTING/test guidance. Production code, schema,
application test assertions and test deadlines remain unchanged. Preserve the
fixture patch in the same delivery branch.

## Verification and acceptance

Characterize the local gate before extraction. Use focused Gherkin-commented
contract tests with real temporary files/process results for the transport,
partition and aggregate boundaries. Cover wrong revision/run/attempt/configuration,
tampered/missing files, unsafe paths, stale source, unknown assemblies, case-only
identity differences, duplicate/missing/failed cases, absent receipts and
failed/cancelled/skipped/missing jobs. Each test owns a distinct observable
rejection or execution contract; avoid source-string replicas of implementation.
Demonstrate meaningful mutations are detected, especially removal of provenance,
exact-once and required-job checks. Report tooling limits explicitly.

Before delivery, run affected build-orchestration tests, the current-source local
canonical full verifier and hardened-container smoke. Run complete distributed
CI with every original stage and independent job present. A synthetic manifest
test or focused SQL cohort does not replace a full gate.

Freeze the original baseline `35adcd568a4c846b54d7cd996c8c0d575ed93b98` and final
candidate revision for an isolated measurement workflow. Baseline uses the
original canonical two-worker gate; candidate uses the distributed full gate.
Match toolchain, runner class, dependency/cache policy and SQL image. Record all
source changes and the expected inventory delta (two retained isolation cases,
plus any separately identified integration changes). Do not use summed case
durations or a failed baseline/candidate as a timing pair.

Record the full verification graph's elapsed time from preparation job becoming
eligible to the strict aggregate completing, including runner queues, setup,
build, upload/download and aggregation. Also record event-to-required-gate time,
each job's runner minutes and critical-path stages. Compare the baseline over the
equivalent graph interval, including its preparation and aggregation. Independent
setup/deployment/smoke checks must pass; include any dominating job in the reported
full CI duration. Existing inner `gate.json` timing is diagnostic only for this
architecture, because it excludes cross-job overhead.

Use two predeclared measurement rounds with baseline/candidate launch order
reversed. Both complete pairs must pass and each must save at least 300 seconds
over its original baseline on the full graph measure. Publish all attempts,
including failures. A failed round blocks adoption pending diagnosis; do not
silently replace it with repeated trials until green. Report aggregate runner
minutes and any increase explicitly; the user supplied no runner-cost ceiling.

Run before/after coverage with identical instrumentation, filters and suite scope
on the frozen revisions. Each reported line/branch/function/statement metric
must change by at most two percentage points and at most 2% relative to baseline
(the stricter interpretation satisfies either reading). Retain raw counts and
denominators and disclose modules not loaded. Coverage instrumentation is separate
from canonical timing; instrumented runs are not timing acceptance evidence.

After integration with the PR base, repeat relevant full verification on that
current source. Distinguish integration results from immutable original-baseline
performance measurements. If acceptance passes, commit/push the scoped work and
open a ready-for-review PR with complete timing, coverage, reliability and cost
evidence. Do not merge. If acceptance fails, retain the fixture gains and safe
2/2 CI setting and report the unmet target.

## Review handoff

This specification defines the proposed architecture, not an implemented or
measured speed improvement. Written-spec approval permits writing the ignored
implementation plan. Plan review and execution-method selection precede code
changes. Historical probes and local evidence remain under ignored
`artifacts/test-audit/`; their successful and failed outcomes are summarized above
so the durable design does not depend on those local files remaining available.
