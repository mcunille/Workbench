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

Do not give SQL-free parsing or cookie-configuration tests a SQL collection fixture. Prefer local
fixtures to an unconditional class lifecycle when only some cases use the initialized application.

## Browser setup

Keep large images and large datasets only where their size is part of the tested behavior. Use
small representative images for navigation or archival workflows, and deterministic intercepted
pages for viewport-specific pagination rendering. Keep at least one real server pagination journey
and real image preparation/upload coverage.

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

Record a before/after cohort for each optimization, with repetitions on the same host and comparable
cache/resource conditions. Include process wall time: xUnit case durations do not fully reflect
fixture startup, setup, or disposal. Keep build time separate when using verified current outputs.
Record full-gate timing as well as individual stages, which overlap and must not be added together.

Map every removed or moved case to its remaining coverage owner. Preserve complete discovered
inventory checks and report intentional count changes. Use focused mutation probes for meaningful
security, isolation, validation, and state-transition assertions; report their actual scope rather
than implying a whole-suite mutation score.

See [Contributing](../CONTRIBUTING.md) for required verification commands and the
[efficiency design](../docs/specs/2026-09-16-test-suite-efficiency.md) for this change's boundaries.
