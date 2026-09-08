# Faster local test iteration

**Status:** Implemented

## Scope and invariants

Provide explicit focused test selection, remove the photo processor's unnecessary SQL
dependency, and reduce repeated application-fixture preparation. Full `verify.ps1` and
container smoke remain delivery requirements. This change does not enable SQL/browser
parallelism, consolidate release builds, or replace real SQL with mocks.

`scripts/test-focused.ps1` selects server filters and client/browser files explicitly. It
builds current source, propagates failures, and offers explicit locked dependency installation.
Browser selection retains disposable SQL, session cleanup, and a fresh client build.
See [CONTRIBUTING.md](../../CONTRIBUTING.md#focused-local-iteration) for commands.

Photo processor tests use a separate collection without a SQL fixture. That collection
disables parallel execution because native image policy and processing capacity are
process-wide, including when HTTP tests invoke the processor.

## Application database preparation

The SQL fixture lazily creates a schema-only backup using this run's current migration
assembly. Ordinary `AuthTestApplication` instances restore it directly into uniquely named
databases and physical files, without first creating an empty database. Each receives a
fresh tenant proof key, contained web credential, seeded users, and security stamps.
Only synthetic password hashes are cached in the test process; the real password hasher
and authentication path remain unchanged. The template contains no seeded identities.

The backup lives inside the fixture's disposable SQL container and never survives the
run. Each application drops its own database; the fixture removes the container and
backup. Failed prior-schema preparation and failed restore/rekey attempts clean up their
database. No development or shared database is used.

Explicit prior-schema requests bypass the template and migrate an empty database to the
requested version. Raw `CreateDatabaseAsync` is unchanged: migration, permission, and
recovery tests that own their setup continue to exercise their explicit migration paths.

## Measurement and tradeoffs

On the local Windows/Docker host on 2026-09-08, a temporary diagnostic test measured
creation, migration, principal provisioning, and seeding separately. It also compared
template restoration and ordinary preparation on the same disposable SQL instance.
Measurements exclude container startup, application-host startup, and database teardown.

| Preparation | Samples (milliseconds) | Median |
| --- | --- | --- |
| Warm restored application, cached seed hashes | 1194, 1105, 1055, 976 | 1080 |
| Warm create + migrate + provision + seed, also cached hashes | 1891, 1486, 1277, 1345 | 1416 |

This small same-run sample measured about 24% less warm preparation time for the restore
path even after both paths received the hash-cache benefit. Earlier uncached warm seed
samples were 507, 358, and 325 ms; cached seed samples were 71, 49, 55, and 63 ms. These are
local observations, not a guaranteed full-suite speedup or a controlled cross-run comparison.

The first optimized preparation took 5421 ms including lazy template initialization and
cold migration/seeding costs. A one-test SQL run therefore does not receive the amortized
benefit; focused selection and SQL-free photo tests provide separate iteration savings.
The diagnostic harness and logs remain in ignored local `artifacts/test-optimization/`,
outside the normal suite so benchmarking does not add recurring verification overhead.

## Verification requirements

- Command boundary tests cover explicit selection, argument forwarding, current builds,
  dependency opt-in, and failures; CI runs these alongside existing orchestration checks.
- The photo collection contract forbids SQL fixture startup and concurrent native processing.
- Application-fixture characterization rereads persisted data after another application's
  mutation, verifies real passwords, distinct security stamps/proof keys, complete migration
  history, and rejection of another database's contained credential.
- Prior-schema readiness and migration drills remain part of the unfiltered server gate.
- Scoped manual mutations must detect shared proof keys and leaked failed databases;
  restored source must be rebuilt before final verification.
