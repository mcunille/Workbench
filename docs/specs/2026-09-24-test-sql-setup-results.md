# SQL setup and coverage ownership measurements

This is the implementation evidence for issue [#160](https://github.com/mcunille/Workbench/issues/160),
following merged issue #159 at `ae6ab8d6ab374d3c3355e27426b9fad9c146af57`.
The [test ownership rules](../../tests/README.md) and
[efficiency design](2026-09-16-test-suite-efficiency.md) govern this bounded test-only change.
Production code, migrations, fixture implementation, concurrency policy, and timing weights are unchanged.

## Coverage owners

Current-source discovery changes from **1,222 to 1,221 server cases**. Six cases move and one duplicate
is removed. All other discovered identities remain unchanged. Raw discovery and the exact identity
delta are retained under ignored `artifacts/issue-160/`.

| Original owner | Final owner | Preserved regression detection |
| --- | --- | --- |
| `BootstrapTests.BootstrapRejectsPasswordsOutsideTheApplicationPolicy` (three rows) | `BootstrapValidationTests`, same method and rows | Bootstrap rejects each invalid password before SQL construction; exact policy exception message and `administratorPassword` parameter exclude connection-string errors. |
| `RecoveryTests.SessionTokensRequireTheExactEncodedEntropyLength` | `SessionTokenValidationTests`, same method | A base64url token encoding 33 bytes is rejected. No authenticated application lifecycle or SQL collection remains. |
| `SecurityAuditTests.AuditWriterRejectsSensitiveMetadataNames` | `SecurityAuditValidationTests`, same method | Recovery-token metadata is rejected by the audit writer using an unconnected context; the exception names `metadata`. |
| `AntiforgeryTests.AntiforgeryCookieIsHttpOnlyAndStrictSameSite` | `BetaApiContractTests`, same method | The actual antiforgery response emits its named cookie with HttpOnly and SameSite=Strict and a nonempty request token. |
| `AntiforgeryTests.LoginRequiresAntiforgery` | Existing `BetaApiContractTests.HeaderlessAuthenticationWritesStillRequireAntiforgery` | A well-formed tokenless login receives 400 with `application/problem+json`, status 400 and the specific antiforgery failure title. This is the only deleted case. |

The two HTTP owners use Development explicitly, clear inherited configuration sources, suppress the
web-connection environment fallback, and use ephemeral data protection. They retain the real route,
middleware and antiforgery implementation. Valid login still belongs to authenticated SQL-backed
journeys. `AntiforgeryTests.LogoutWithoutAntiforgeryDoesNotRevokeSession` still proves that rejected
logout leaves a real persisted session usable.

## Current-schema clone substitutions

All seven sites now use the existing `CreateMigratedDatabaseAsync()` contract. Each invocation
restores a unique unseeded database and regenerates its proof key before the existing credential and
seed setup. No mutable database is shared between test cases.

| Setup site | Why the schema is a precondition | Authority and isolation retained |
| --- | --- | --- |
| `CredentialVerificationTests.InitializeAsync` | Tests identity/password lookup, not migration application. | Per-case web credential, proof key and local user seeds. |
| `SessionRevocationTests.InitializeAsync` | Tests durable revocation between replicas. | One unique database per case; only that case's two replicas share its web credential and proof key. |
| `TenantIsolationTests.InitializeAsync` | Tests tenant read/write boundaries in an established schema. | Restricted web principal, per-database proof key, and both tenant seeds. |
| `SecurityAuditTests.WebPrincipalCanAppendButCannotUpdateOrDeleteAuditHistory` | Tests effective append-only audit permissions. | Restricted web credential, applied tenant proof and independently seeded audit rows. |
| `SensitiveRequestRateLimiterTests.SqlLimiterSharesAWindowAcrossApplicationReplicas` | Tests durable shared permit accounting. | Per-case web credential and proof key; both replicas target only this database. |
| `SensitiveRequestRateLimiterTests.WebPrincipalCannotOverrideLimiterTimeWindowOrPermitCount` | Tests restricted stored-procedure authority. | The same web-principal call with attacker-controlled parameters. |
| `SensitiveRequestRateLimiterTests.SqlLimiterOpportunisticallyRemovesExpiredPartitions` | Tests persisted expiry cleanup. | Admin-only expired-row setup, web-principal acquisition and SQL verification remain separate. |

Fresh bootstrap provisioning remains in `BootstrapCreatesExactlyOneTenantAndAdministrator`.
`DatabaseMigrationTests`, `PasswordPrincipalProvisioningTests`, `EntraPrincipalProvisioningTests`,
`BetaFinancialMigrationTests`, `SupplierProfileMigrationTests` and physical recovery owners retain
their distinct responsibilities. `AuthTestApplicationIsolationTests` owns independent data,
credentials, security stamps, current schemas and regenerated proof keys.

## Measurement method

Each cohort runs three times before and after on the same Windows host (TARDISCORE), using .NET SDK
10.0.401 and Docker Desktop's Linux engine. Dependency and SQL image caches are warm; processes run
sequentially with unrelated checkout services left unchanged. Each sample includes test-process
startup, SQL fixture startup when applicable, setup, execution and disposal. Builds are separate:
the baseline incremental build took 1.73 seconds and the changed build 3.83 seconds. No build or
other test workload runs concurrently with the timed samples.

Baseline runs use binaries freshly built and successfully tested at the stated base; source edits
prepared during measurements do not change those binaries. The after runs rebuild the changed
source. Assembly hashes, source receipts, command filters, logs, TRX and process timings remain in
ignored `artifacts/issue-160/before/` and `after/`. No failed or synthetic run supplies timing weights.

The validation filter selects the three moved method names (five expanded cases). The HTTP filter
selects both prior login owners and the cookie method (three cases before, two after). The clone
filter selects CredentialVerification, SessionRevocation, TenantIsolation, the persisted audit
method and SensitiveRequestRateLimiter (16 cases in both states). Exact filters are in the timing
JSON and `artifacts/issue-160/measure.ps1`.

## Results

Seconds below are fixture-inclusive process wall time; ranges include all three successful samples.

| Cohort | Cases before / after | Before median (range) | After median (range) | Interpretation |
| --- | --- | --- | --- | --- |
| Validation | 5 / 5 | 31.27 (27.12–32.34) | 2.42 (2.37–2.46) | 92.3% lower for this focused invocation; SQL startup disappears. |
| HTTP antiforgery | 3 / 2 | 25.93 (22.45–33.38) | 3.67 (3.30–4.02) | 85.8% lower; one duplicate disappears and neither remaining owner starts SQL. |
| Current-schema SQL | 16 / 16 | 99.56 (72.33–103.56) | 71.20 (68.34–77.64) | Observed median 28.5% lower; ranges overlap, so the magnitude is uncertain on this shared host. |

No cohort median regressed. Individual samples are not uniformly faster: the slowest clone after
sample (77.64s) exceeds the fastest before sample (72.33s). Three samples do not establish statistical
significance. These are focused-cohort results, not whole-suite savings; their durations must not be
added, and the full gate still starts SQL for its other owners. No comparable whole-suite baseline
was measured for this issue.

## No-infrastructure proof

All eight cases in BootstrapValidation, SessionTokenValidation, SecurityAuditValidation and the two
HTTP methods pass inside a disposable SDK container with `--network none`, no Docker socket and an
unusable Docker endpoint. A fresh-bootstrap SQL case fails with `DockerUnavailableException` in the
same environment, proving accidental collection startup cannot succeed. No other checkout's service
is stopped. This Linux proof is separate from the Windows timing comparison.

The proof uses the verified assemblies and relocates only generated MVC content-root and static-web-
asset manifests from Windows paths to their Linux mount paths. Two initial proof attempts failed on
those generated path assumptions before the HTTP assertions; their logs are retained and excluded
from passing evidence. The corrected run passes all eight cases. Raw commands, TRX, negative control
and relocation manifests are under `artifacts/issue-160/no-infrastructure/`.

## Scoped regression probes

| Temporary mutation | Observed detection |
| --- | --- |
| Bypass bootstrap's password-policy call | All three rows reject the connection-string exception because it lacks the expected `administratorPassword` parameter. |
| Bypass both encoded and decoded token-length guards | The 33-byte token is accepted and the moved `Assert.False` fails. This compound probe does not claim each redundant guard is independently necessary. |
| Skip `IAntiforgery.ValidateRequestAsync` in the real middleware | The retained HTTP owner receives 500 instead of the required CSRF-specific 400 as execution reaches unconfigured downstream services. |
| Leave the template proof key unchanged during clone setup | The existing two-application isolation owner fails on equal proof keys. |

Each source file was restored byte-for-byte and its SHA256 checked in a `finally` block. All ten
cases in the restored validation/HTTP/isolation cohort then passed from a fresh build. Raw mutation
and clean TRX evidence is under `artifacts/issue-160/probes/`. These are four scoped manual probes,
not an automated mutation-tool run or a whole-suite score. Unchanged isolation coverage continues
to exercise data, credential, tenant and security-stamp separation.

## Final verification

- The prescribed expanded `test-focused.ps1 -ServerFilter ... -InstallDependencies` run passes
  **140 cases**, including the original/replacement owners, application isolation, database and
  historical migration owners, password/Entra provisioning and physical recovery. The exact cohort
  is the OR of the affected class names plus AuthTestApplicationIsolation, DatabaseMigration,
  PasswordPrincipalProvisioning, EntraPrincipalProvisioning, BetaFinancialMigration,
  SupplierProfileMigration and BlobRecovery tests. `RecoveryTests` also selects FileRecovery.
- `scripts/verify.ps1` passes: **1,221 server cases exactly once**, partitioned into 584 and 637,
  **518 client tests** across 80 files, and **120 browser cases**. Formatting, API generation/drift,
  typechecking, build and published-release checks pass. The Release build has no warnings/errors.
- `scripts/smoke-container.ps1` passes the hardened SQL-backed runtime and local Compose checks,
  including secure-cookie login and durable sessions after app replacement. Public CA issuance
  and real SMTP delivery are outside this local smoke evidence.
- Independent read-only internal review found no actionable code or evidence-record issues.

The complete gate takes **1,467.56 seconds** on this Windows host. Its server stage takes 1,239.40s,
client stage 206.77s, browser stage 210.93s and published stage 5.21s. Stages overlap; these values are
not additive or a before/after full-gate comparison. Exact-once accounting passes without changing
the duration dataset or concurrency settings. Gate receipts and TRX are retained in ignored
`artifacts/verification/d0af2d783cb64314a26f3b764d047f13/`; focused and smoke logs are under
`artifacts/issue-160/`. No merge, deployment or retained-data deletion was performed.
