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

Use an empty database and explicit migrations when applying/upgrading/rolling back the schema is
the subject. Keep fresh migration-to-provisioning evidence as well as effective-permission tests.
Permission matrices that only need an already established current schema may use isolated clones.
Do not replace real SQL security, transaction, concurrency, or recovery evidence with mocks.

`SupplierScenarioFixture` prepares immutable supplier histories through production commands in
the collection's disposable SQL container. Its class-fixture initialization runs once per
container, before case bodies; measure that cold startup in process and gate wall time. Cases
restore unique databases, regenerate proof keys, and create fresh contained principals, user
security stamps and sessions. Backups contain no contained web credential or live session;
mutable command nodes are parsed separately for each case. Cloning does not preserve query plans.
The 30-second case budget includes restoration and the asserted operations. Report-only cases
read prepared genuine history; correction, corruption, competing writes and physical recovery
remain inside their owning case bodies. Do not move an asserted transition into shared setup.

Do not give SQL-free parsing or cookie-configuration tests a SQL collection fixture. Prefer local
fixtures to an unconditional class lifecycle when only some cases use the initialized application.

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

Map every removed or moved case to its remaining coverage owner. Preserve complete discovered
inventory checks and report intentional count changes. Use focused mutation probes for meaningful
security, isolation, validation, and state-transition assertions; report their actual scope rather
than implying a whole-suite mutation score.

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

See [Contributing](../CONTRIBUTING.md) for required verification commands and the
[efficiency design](../docs/specs/2026-09-16-test-suite-efficiency.md) for this change's boundaries.

The [SQL setup measurement record](../docs/specs/2026-09-24-test-sql-setup-results.md) maps the validation
and antiforgery owners moved out of SQL lifecycles, lists the seven additional current-schema clone
substitutions, and records their focused before/after measurements and regression probes.

The [browser ownership measurement record](../docs/specs/2026-09-25-test-browser-ownership-results.md)
maps responsive geometry to intercepted cases, identifies retained live retry/persistence/download
journeys, and records the reduced label/encoding matrices, discovery changes and fault probes.

The [case-budget measurement record](../docs/specs/2026-09-30-test-time-budget-results.md)
maps the cheaper closure and grouped SQL guards to their retained contracts and
records the 30-second gate policy, timing evidence and focused regression probes.
