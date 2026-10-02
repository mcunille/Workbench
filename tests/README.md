# Test ownership and cost

Choose the cheapest boundary that can establish the behavior. Similar assertions at HTTP and SQL
boundaries are sometimes necessary: rejecting a request and preventing a direct database bypass
are different contracts. A browser should exercise browser behavior and representative complete
workflows, rather than repeat every lower-layer input permutation.

| Contract | Exhaustive owner | Integration evidence to retain |
| --- | --- | --- |
| Normalization, date/length rules, encoding | Pure server/client tests | Representative HTTP binding and rendered field errors |
| Routing, JSON, status codes, headers, authentication, CSRF | HTTP API tests | Browser cookie/login/navigation wiring |
| Tenant isolation, effective grants, constraints, transactions, competing writes | Real SQL under the intended principal | HTTP authorization and selected conflict/retry UI |
| Recovery manifest compatibility | Pure manifest-validation matrix | Physical SQL/blob recovery for distinct content paths |
| Migration preservation and compatibility | Real prior-schema upgrades | HTTP readiness transitions and rollback guards |
| Export all-pages/scope/tenant boundaries | API with real SQL | Scope selection and a native download for each format |
| Client cancellation, stale completion, transport failures | State/transport/component tests | Representative browser wiring failure and retry |
| Geometry, focus, history, scrolling, file input, media preferences | Browser tests | Component semantics and state transitions |

## Database setup

Use `SqlServerFixture.CreateMigratedDatabaseAsync` when the test needs the current schema as a
precondition. It restores an unseeded template created from the current run's migration assembly
into a unique database, then regenerates the tenant proof key. Credentials, identities, and data
remain isolated. This is not a shared mutable database.
Prepare that shared template during fixture initialization so its migration/bootstrap cost does
not consume the first domain test's case budget. Fixture preparation remains part of process
timing; fresh migration drills still create their own databases and execute their own migrations.

Use an empty database and explicit migrations when applying/upgrading/rolling back the schema is
the subject. Keep fresh migration-to-provisioning evidence as well as effective-permission tests.
Permission matrices that only need an already established current schema may use isolated clones.
Do not replace real SQL security, transaction, concurrency, or recovery evidence with mocks.

`SupplierScenarioFixture` prepares immutable supplier histories through production commands in
the collection's disposable SQL container. Each class fixture declares only its required histories;
the container caches each history once and prepares them serially before case bodies. A separate
immutable base captures the common supplier/account setup once, without financial history. Each
history starts from an independent clone of that base. Measure all cold preparation in process
and gate wall time: selecting fewer classes reduces startup, while the full gate can still need
all histories in each process. Cases
restore unique databases, regenerate proof keys, and create fresh contained principals, user
security stamps and sessions. Backups contain no contained web credential or live session;
mutable command nodes are parsed separately for each case. Cloning does not preserve query plans.
The 30-second case budget includes restoration and the asserted operations. Report-only cases
read prepared genuine history; correction, corruption, competing writes and physical recovery
remain inside their owning case bodies. Do not move an asserted transition into shared setup.

For independent rejection matrices, reuse a valid context while checking financial state after
every rejected command and proving a valid command still succeeds afterward. Keep cases that
change configuration in their own context. Concurrent exact retries and identical inputs with
interchangeable request IDs need one serial order; retain both orders when the competing inputs
or resulting state differ.

Do not give SQL-free parsing or cookie-configuration tests a SQL collection fixture. Prefer local
fixtures to an unconditional class lifecycle when only some cases use the initialized application.
Photo processor tests use a SQL-free collection that disables parallel execution: native image
policy and capacity are process-wide, including HTTP callers. Template backups remain unseeded
and disposable inside the run's SQL container; each application receives fresh authority and
identity material. Sharing mutable databases would invalidate isolation and race evidence.

## Gate provenance and scheduling

The gate discovers the current built inventory and assigns whole classes, including every theory
row, to independent processes with sequential collections and disposable SQL fixtures. Missing,
extra, duplicate, skipped or failed results and missing completion receipts fail the gate.
Discovery preserves exact UTF-8 test identities; trimming or filtering unsupported names must
not silently reduce coverage. Shared build-output writes are serialized; concurrent consumers
use read-only release artifacts.

Run-specific build receipts and manifests bind repository root, run ID, Release configuration,
current tracked/nonignored source hashes and published hashes. Generated API files are excluded
only from the pre-build receipt, checked for drift and included in the final manifest. Existence
alone cannot authorize reuse; standalone browser/publish runs create fresh artifacts. Consumers
must not delete shared outputs. Native child stdout/stderr use separate temporary logs so they
cannot corrupt PowerShell background-job transport; cleanup follows process shutdown.

### Refresh server timing data

The reviewed `scripts/server-test-durations.json` dataset predicts class cost by summing historical
case durations. Assignment takes longest classes first into the lowest predicted total, with
ordinal class-name and partition-ID ties. All methods sharing a class fixture stay in one process;
fixture preparation remains outside case deadlines. New rows receive one second each; obsolete rows do not
affect assignment. This fallback permits new tests but cannot predict their actual cost. Invalid
datasets fail closed. Inventory artifacts retain predictions/fallback counts and dataset provenance.

Refresh after substantial test changes or observed imbalance, outside a running gate. Use only a
completed successful CI **push to main** in this repository. Verify event, branch, conclusion and
full SHA with `gh run view` before downloading `verification-evidence`; do not use PR artifacts.
The importer validates gate success, inventory, exit receipts, exact passing TRX coverage and finite
nonnegative durations, prohibits XML DTDs, and never executes artifacts. It cannot authenticate
local downloaded files: provenance parameters describe evidence the operator verified.

```powershell
./scripts/update-server-test-durations.ps1 -EvidenceDirectory <gate-directory> `
    -SourceRunUrl https://github.com/mcunille/Workbench/actions/runs/<run-id> `
    -SourceRevision <full-commit-sha> -OutputPath scripts/server-test-durations.json
./tests/Workbench.BuildTests/ServerDurations.Tests.ps1
./tests/Workbench.BuildTests/ServerPartitions.Tests.ps1
```

Review the data and provenance diff together. A controlled `-TimingDataPath` comparison must retain
partition count, concurrency, inventory and runner class. Predicted balance excludes fixture startup
and runner pressure and does not guarantee critical-path savings. Retain resource costs alongside
wall time; more concurrent SQL fixtures are not free capacity.

## Browser setup

Keep large images and large datasets only where their size is part of the tested behavior. Use
small representative images for navigation or archival workflows, and deterministic intercepted
pages for viewport-specific pagination rendering. Keep at least one real server pagination journey
and real image preparation/upload coverage.

Install synthetic response routes before the first navigation that can request their data.
Waiting for a page heading does not establish that its live data and thumbnail requests have
finished; replacing response ownership afterwards can introduce timing-dependent failures.

Evidence screenshots are opt-in; geometry and accessibility assertions must run regardless of
capture settings. Safe failure diagnostics remain automatic. Keep destructive authentication
scenarios separate from reusable sessions and never weaken production rate limits for test speed.

The `live` project has one worker. Its ordinary journeys reuse primary/secondary sessions in
the live tenant; dedicated authentication tests use a separate tenant and uncached sessions.
The `intercepted` project owns synthetic page responses and rejects undeclared API requests.
Do not use `route.continue()`, `route.fetch()`, or direct API requests in that project to bypass
its boundary. Mixed journeys that need real uploads, downloads, or persistence remain live.

The retained owners for reduced browser setup are `InventorySearchTests` for real query/cursor
semantics, `ItemRestorationTests` for archive filtering and tenant boundaries, and
`ItemExportEndpointTests` for all-page export and archive scope. Browser tests retain one live
collection pagination journey, synthetic viewport/archive traversal, and a native download for
each export format. `photos.spec.ts` retains camera-sized image preparation; unrelated workflows
still upload real, smaller images.

## Measuring changes

Default local and CI gate tests have a maximum 30-second case budget. Server
facts and individual theory rows use the shared budgeted xUnit attributes;
keep test methods async so xUnit can enforce their timeout. Pure bodies yield
once before executing. Collection scheduling is serial inside each server
partition, with independent processes retaining gate concurrency. Playwright
uses a 30-second test timeout without longer case/file overrides; Vitest retains
its shorter default. Node infrastructure checks use `--test-timeout=30000`.
Discovery checks detect omitted server budgets and longer browser declarations;
the browser reporter also fails runs containing runtime timeout extensions.

The case budget does not include shared SQL/container/server startup. xUnit
reports a timeout failure but does not cooperatively cancel the test's pending
work; disposable fixture cleanup remains owned by the runner. Narrated media
recording scripts use separate configs and are outside the default test gate.

Record a before/after cohort for each optimization, with repetitions on the same host and comparable
cache/resource conditions. Include process wall time: xUnit case durations do not fully reflect
fixture startup, setup, or disposal. Keep build time separate when using verified current outputs.
Record full-gate timing as well as individual stages, which overlap and must not be added together.

The CI server stage and local verification use two isolated processes by default. The unsuccessful
four-process experiment's SQL memory caps were removed with its resource-limit test: measured
headroom did not justify making those arbitrary values a fixture contract. CI retains 30-second
samples of host CPU counters, available memory, load and container
CPU/memory usage in
`verification-evidence/ci-resources/samples.jsonl`. These samples supplement full-gate and partition
timings; predicted duration balance alone does not establish a performance improvement.

Map every removed or moved case to its remaining coverage owner. Preserve complete discovered
inventory checks and report intentional count changes. Use focused mutation probes for meaningful
security, isolation, validation, and state-transition assertions; report their actual scope rather
than implying a whole-suite mutation score.
Keep required correctness coverage in the delivery gate; moving it to nightly-only CI would weaken
the delivery contract. Advance controlled time only after the production deadline is registered;
observing an unrelated SQL lock wait does not establish that registration or the intended query.

On 2026-09-16, a single successful same-host Linux gate pair measured 474.80s at `7ccc216` and
389.27s at `46b187f` after ten test-efficiency changes (18.0% lower). Builds, shared-host pressure,
cache state and overlapping stages limit interpretation; cohort savings are not additive. Earlier
duration-balancing observations reduced predicted skew without establishing a robust wall-time
speedup. These are dated observations, not current performance or coverage claims; Git retains the
original detailed experiments. New optimizations still require their own comparable measurements.

On 2026-10-02, the [four-process experiment](https://github.com/mcunille/Workbench/actions/runs/37048110385)
at `a65bb0e` took 55m11s end to end, versus 49m52s for the successful
[two-process main baseline](https://github.com/mcunille/Workbench/actions/runs/37030188879).
The experiment's server stage took 3056.41s and had ten timeout failures among 1829 cases;
its browser stage also failed. It did not establish a speedup and four-way concurrency on one
runner was reverted. The 99 valid resource samples reported at least 4.76 GiB available memory,
1.37 GiB peak usage per bounded SQL container, 94.39% peak host CPU utilization, 16.05% weighted
I/O wait, and peak one-minute load 11.77 on four CPUs. Memory headroom alone did not establish
safe concurrency. Different hosted machines and cache conditions limit direct timing comparisons.

A separate same-host payment-setup snapshot probe preserved independent mutation and authentication,
and did not grant correction authority. First-use setup changed from 1.873s to 3.385s, warm setup
from 1.202s to 0.561s, and the complete test case from approximately 9s to 11s. Concurrent coverage
runs limit that single pair; the small setup saving did not justify retaining another snapshot
lifecycle. The experimental implementation and its test were reverted, with raw evidence retained
in ignored `artifacts/snapshot-experiment/` for this checkout. No existing test case was removed.

The subsequent [test deletion and class-routing batch](../docs/specs/2026-10-02-ci-test-work-results.md)
retired 15 cases and consolidated 15 more while retaining their guard probes and
transferred keepers. Measured main at `ce903ea` had 1834 cases; the batch had 1804. Complete local verification,
container smoke, and fresh serial coverage passed; line and branch loss stayed well
below 5%. Its successful hosted run took 41m15s, versus 37m05s for a newer successful
main run. The ten-minute goal is not demonstrated. The record maps removed owners,
accepted example losses, mutation evidence, changed coverage denominators and run limits.
## Coverage comparisons

Use identical source scopes, instrumentation, and runner settings before and after a test reduction.
The client coverage provider is pinned to the Vitest version; its scope includes production TypeScript
under `src/`, excluding tests and test support. The server collector targets `Workbench.Server` and
`Workbench.Database` when those assemblies are loaded. The integration-test report currently contains
`Workbench.Server`, including its C# migration definitions; the separate database CLI process is not
instrumented by that collector. Reports stay in ignored `artifacts/` directories.

```powershell
npm run test:run --prefix src/Workbench.Client -- --coverage --maxWorkers=1
dotnet test tests/Workbench.Server.IntegrationTests/Workbench.Server.IntegrationTests.csproj `
  --configuration Release --settings tests/coverage.runsettings --collect 'XPlat Code Coverage' `
  --results-directory artifacts/coverage/server
```

Compare line and branch percentages separately for each suite, and retain both the covered and total
counts. Coverage of C# SQL strings does not measure SQL engine branches; retain real database tests
and targeted fault probes for those contracts. Browser and standalone tooling tests provide separate
execution evidence and are not included in either code-coverage percentage. Count parameterized
rows as runnable cases; report script-level checks separately when no case-discovery runner exists.

The 2026-10-02 serial coverage runs passed 1828 baseline server cases and 1829 after adding the SQL
resource-limit test. The client runs each passed 512 cases. Common-source coverage did not decrease:

| Scope | Metric | Baseline covered/total | After covered/total |
| --- | --- | --- | --- |
| Client | Lines | 2747/3047 (90.15%) | 2747/3047 (90.15%) |
| Client | Branches | 3903/4720 (82.69%) | 3903/4720 (82.69%) |
| Server assembly | Lines | 92660/94223 (98.34%) | 92661/94223 (98.34%) |
| Server assembly | Branches | 5310/6779 (78.33%) | 5312/6779 (78.36%) |

These runs used identical collector settings and isolated output directories to prevent overlapping
collectors from modifying each other's binaries. The after report additionally listed a zero-hit
Database CLI assembly copied into its isolated output (405 lines, 378 branches); comparison excludes
that assembly to retain the baseline Server scope and matching denominators. This does not establish
coverage of the separately launched CLI. Raw reports and comparison calculations remain in ignored
`artifacts/coverage/` and `artifacts/ci-coverage-comparison.json`. These coverage results apply to the
resource-limit experiment, not to the reverted snapshot probe or the subsequent test deletion batch.

See [Contributing](../CONTRIBUTING.md) for required verification commands.

The [SQL setup measurement record](../docs/specs/2026-09-24-test-sql-setup-results.md) maps the validation
and antiforgery owners moved out of SQL lifecycles, lists the seven additional current-schema clone
substitutions, and records their focused before/after measurements and regression probes.

The [browser ownership measurement record](../docs/specs/2026-09-25-test-browser-ownership-results.md)
maps responsive geometry to intercepted cases, identifies retained live retry/persistence/download
journeys, and records the reduced label/encoding matrices, discovery changes and fault probes.

The [case-budget measurement record](../docs/specs/2026-09-30-test-time-budget-results.md)
maps the cheaper closure and grouped SQL guards to their retained contracts and
records the 30-second gate policy, timing evidence and focused regression probes.
