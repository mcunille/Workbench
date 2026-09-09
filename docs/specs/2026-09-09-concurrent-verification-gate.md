# Concurrent final verification gate

**Status:** Implemented

## Approved scope

The gate retains every server, migration, concurrency, recovery, client, browser,
release-output, deployment and container check while reducing serialized orchestration.
This extends the [focused iteration design](2026-09-08-local-test-iteration.md).
It does not convert fixtures, tune caches, cancel superseded CI runs, or increase browser workers.

## Isolation and completeness

`test-server-partitions.ps1` discovers the current built test inventory and assigns
whole methods, including every theory case, to 2–4 nonempty partitions. Largest groups
are assigned first to the partition with the fewest test cases, with deterministic ties.
This initially balances case counts; it does not assume equal test durations. Per-partition
TRX timings make runtime imbalance visible and permit evidence-driven tuning later.
Each partition runs in an independent process with sequential xUnit collections and
one disposable SQL fixture/container. Global connection-pool clearing and native image
policy therefore cannot interfere across partitions. Existing fixture/security setup
and migration paths remain unchanged.

Discovery and execution are a fail-closed contract: duplicate discovery, unsupported
inventory, empty partitions, missing or extra results, skipped/non-passing tests, failed
processes and missing completion receipts all fail verification. Current-run result
folders prevent old TRX files from satisfying a new run. Adding a test assembly requires
explicit runner support instead of silently omitting it.

## Scheduling and resources

The client lint/test process starts after dependency setup. After one shared Release
build, server partitions start while the parent checks generated API drift and builds
the client. Browser verification and published-output probes run concurrently after
publication, using read-only shared release artifacts. Browser tests remain one worker.
Shared .NET and client output writes are serialized. The parent joins every started
stage, propagates failures, and records stage and gate wall-clock times even on failure.
Default local settings are two partitions and two active server processes; use
`-ServerPartitions` (2–4) and `-ServerConcurrency` (1–4) to bound local SQL/resource pressure.
Peak SQL containers are active server processes plus the browser fixture. Container
smoke is a separate gate and CI job; it builds its distinct Linux runtime configuration.

CI retains the existing independent setup, deployment, source/release, and hardened
container jobs. `Required verification gate` runs with `always()` and explicitly requires
all four named job results to be `success`. Failure, cancellation, skipped or missing
jobs cannot pass. This aggregate is the single check to require in branch protection;
its workflow definition does not itself change repository protection settings.

## Build and artifact provenance

The gate uses one compatible Release solution build (`UseAppHost=false`, `BuildClient=false`)
to generate OpenAPI and tests. It builds the client once, then publishes the server and
database tool once with `--no-build --no-restore`. A run-specific manifest binds the
repository root, run ID, Release configuration, tracked/nonignored source hashes, and
published file hashes. A pre-build receipt detects source changes while building; generated
API files are omitted only from that receipt, checked for drift, and included in the final
manifest. Consumers validate the current run/source/configuration and output content;
file existence alone cannot authorize reuse. Database setup invokes the published DLL.
Standalone browser/publish scripts create fresh artifacts when no gate manifest is supplied.

Gate artifacts live under ignored `artifacts/verification/<run-id>/` with `gate.json`,
`stages.json`, release outputs and per-partition evidence. They are local diagnostics,
not credentials or release promotion artifacts. Consumers do not delete shared outputs.
Local cancellation does not constitute success; CI's aggregate rejects cancellation.

## Verification and measurement

Focused orchestration tests cover real child-process failures and completeness; partition
and artifact tests cover inventory/result matching and provenance. Targeted manual mutants
check that critical fail-closed branches are observed. Actual full-suite/browser runs
validate discovery names and release compatibility beyond command shims.

The prior CI observation was 16m58s for source/release at `930d606`, including 545 server
tests in 11m47s, 133 client tests in 10.78s, and 50 browser tests around 2.4m. Runner variance
was substantial; 7–10 minutes remains a hypothesis. The prior 24% improvement measured
warm fixture preparation only, not whole-suite time. Compare full gate and stage timings
on the same host/configuration, distinguish dependency/cold starts from warm runs, and
report extra concurrent SQL processes alongside wall time.

### Initial local run

On the Windows/Docker host (16 Docker CPUs, 32 GB; other services retained), the first
three-partition/two-process run took 687.57 seconds (11m28s). Partition executions were
343.82, 351.47, and 268.77 seconds for 182, 182, and 181 passing cases. The gate correctly
failed because native Windows discovery decoding corrupted Unicode theory display names;
TRX names remained correct. Explicit UTF-8 discovery decoding is required by the final
contract. Client checks took 58.26 seconds, browser setup/tests 178.40 seconds, and published
probes 4.79 seconds. These stages overlap, so their durations do not add to wall time.

This run had newly restored worktree outputs and installed npm dependencies, with existing
machine-level package/container caches; it is not a fully cold-machine run. The two-stage
server wave motivates a default of two partitions/two processes. A subsequent warm run
checks that choice and the decoding fix. The initial failed gate is not green verification.

### Corrected warm run

The same host then passed the complete gate in **597.56 seconds (9m58s)** with two
partitions/two active processes. Exact discovery/TRX validation covered all 545 server
cases; all 133 client and 50 browser tests passed. Published-output probes also passed.

| Measurement | Initial 3 partitions / 2 active | Corrected warm 2 / 2 |
| --- | ---: | ---: |
| Full gate | 687.57 s (failed completeness decoding) | 597.56 s (passed) |
| Prerequisites through publication | 88.00 s | 61.01 s |
| Client lint + tests | 58.26 s | 20.52 s |
| Server discovery + processes | 619.91 s | 563.87 s |
| Individual server processes | 343.82 / 351.47 / 268.77 s | 442.21 / 554.06 s |
| Browser setup + tests | 178.40 s | 183.69 s |
| Published probes | 4.79 s | 5.94 s |

Both runs used already installed npm dependencies and cached Docker images, with fresh
SQL containers and fresh gate publications. Only the second had warm worktree build outputs.
The 90-second wall-time difference includes both warm startup and partition-count changes;
it does not isolate either cause or establish a before/after serial-gate improvement.
The prior CI runner is also not a controlled comparison. One local run reached the 7–10
minute hypothesis range; neither CI time nor repeat-run speed is guaranteed.

The two partitions contain 273/272 cases but took 442/554 seconds: balancing case counts
leaves observable duration variation. Defaults avoid a guaranteed second wave and retain
bounded resource use. Peak gate SQL concurrency is three containers (two server fixtures
plus one browser fixture), compared with one at a time in the previous sequential gate;
each test process additionally has its own Testcontainers cleanup helper. Extra fixture
setup and resource contention are costs, not free parallel speedup.

Focused PowerShell contracts passed for partition inventory/failures, real native Unicode
decoding, stage/CI aggregation, artifact provenance, focused entry points, and restore
boundaries. Nineteen targeted manual mutation probes were killed (nine partition/discovery,
five provenance, three stage/aggregate, two native process streams); one initial identity-check survivor prompted a
same-count substituted-name assertion. These are scoped manual probes, not an automated
whole-repository mutation score. Immutable-patch internal reviews found no actionable issues.
Raw local evidence lives under ignored `artifacts/gate-optimization/` and
`artifacts/verification/`; CI uploads its timing, discovery, TRX and partition logs.

The retained hardened SQL/container/Compose smoke gate passed as non-root UID 1654,
including read-only runtime, private listener, TLS, Secure-cookie login, app replacement,
and worker queue telemetry. Standalone fresh publication passed without a manifest;
a standalone `auth.spec.ts` browser run passed all six authentication scenarios and
cleaned its disposable SQL/files. The real SQL restore marker/missing-proof/success
contract checks also passed. These additional checks were run after the timed full gates.

### Native child process streams

The initial Linux CI run passed all server/client/browser cases but correctly failed the
published stage and required aggregate: a `Start-Process` descendant inherited PowerShell's
background-job stdout transport and wrote application JSON directly into its protocol.
That failed run took 393.37 seconds for the gate and is not green verification.

Both published-server and health-probe stdout/stderr now redirect to their own temporary
log directory outside shared publish outputs. Failure diagnostics are emitted through
PowerShell's managed output; process logs are cleaned after shutdown. A focused test uses
the production launch parameter objects with a real native emitter inside a background job.
It reproduced the exact transport failure on Linux before the fix and passes on Linux and
Windows afterward. Removing stdout/stderr redirection is caught by targeted mutations.
A fresh actual standalone publish also passed inside the corrected background-job boundary.

Discovery removes exactly VSTest's four-space prefix without trimming test identity or
filtering custom display names out of the inventory. Leading/trailing/whitespace-only
names therefore reach validation and fail if unsupported, rather than silently reducing
coverage. Native-emitter regressions pass on Windows and Linux; strict fresh discovery
also matches the complete 545-case passing TRX inventory. Unexpected nonempty output
after the discovery header fails closed.
