# Test efficiency measurements

This record accompanies the [efficiency design](2026-09-16-test-suite-efficiency.md).
The ten approved changes have individual before/after measurements below.

## Method

Server cohorts run three times before and after each change in .NET SDK 10.0.401 Linux containers
on the same Windows/Docker host. Each invocation starts its own test process and SQL fixture when
required. Builds are verified separately and excluded from these cohort timings. Source receipts,
TRX files, and process measurements are retained in ignored `artifacts/test-optimization/`.

The host exposes 16 CPUs and approximately 15.3 GiB to Docker. Unrelated checkout services remain
running; these local measurements include shared-host variation. Newly built Windows test assemblies
were blocked by Application Control, so measurements switched to Linux without changing security
settings. Windows and Linux process times are not treated as a controlled comparison.

Each row compares adjacent implementation states. Cohorts overlap and savings must not be added.
The full gate overlaps browser, server, and client work, so faster individual tests do not imply
the same reduction in gate wall time.

Browser comparisons run the complete suite twice per adjacent state in a fixed Linux runner,
including publication, disposable SQL startup, and test cleanup. Current server/database builds
and the client build are prepared before the first timed invocation. The unchanged browser pair
passed all 101 cases in 215.72s and 201.47s (midpoint 208.60s); summed case durations were 177.27s
and 179.08s. The difference between those stable case totals and variable process times is why
small full-stage deltas are not treated as conclusive speedups. Login-rate-limit attribution
requires status counters; wall-time differences alone do not establish that cause.

## Per-change results

Times are seconds; ranges include all recorded repetitions.

| # | Change and measured cohort | Before median (range) | After median (range) | Interpretation |
| --- | --- | --- | --- | --- |
| 1 | Physical recovery plus manifest validation | 71.75 (71.01–71.89) | 24.24 (22.63–24.94) | 66.2% lower; 11 repeated physical drills become two physical paths plus 25 pure validation cases. |
| 2 | Current-schema setup: telemetry, principal provisioning, durable sessions | 93.85 (89.26–95.44) | 59.49 (58.83–61.10) | 36.6% lower; all 48 cases retained, including fresh provisioning and migration rollback. |
| 3 | Readiness setup and exact duplicate removal | 54.63 (51.40–56.87) | 45.08 (43.37–47.58) | 17.5% lower; 35 cases become 34, with all six prior-schema HTTP transitions retained. |
| 4 | Readiness effective-authority matrix moved to its real SQL service | 63.16 (62.62–64.65) | 48.21 (47.62–48.58) | 23.7% lower; all 44 mutation statements preserved exactly, plus 11 HTTP/schema cases. |
| 5 | Three SQL-free cookie/session contracts | 11.56 (11.44–11.77) | 2.58 (2.57–2.59) | 77.7% lower for this focused invocation. The full suite still starts SQL for other tests. |
| 6 | Optional evidence capture and shared export layout matrix; complete browser suite | 208.60 (201.47–215.72) | 192.04 (191.47–192.61) | 7.9% lower process time; summed case duration fell from 178.17s to 169.91s. One real-PNG capture contract added; geometry/navigation assertions retained. |
| 7 | Remove recovery's redundant login/API-only DELETE; complete browser suite | 192.04 (191.47–192.61) | 159.64 (158.62–160.66) | 16.9% lower process time; all 102 cases retained. The recovery case became much faster, but another authentication case slowed, so its individual delta overstates full-suite savings. |
| 8 | Reusable small images for unrelated workflows; complete browser suite | 159.64 (158.62–160.66) | 154.25 (152.79–155.71) | 3.4% lower process time. Dedicated camera-image processing/retry cases remain unchanged; other journeys still upload real images. |
| 9 | Reduce duplicate pagination/export setup and reuse CSRF within a setup loop; complete browser suite | 154.25 (152.79–155.71) | 151.30 (150.85–151.75) | Modest observed 1.9% reduction. All 102 cases retained, including live desktop pagination, controlled phone/archive traversal, and native CSV/ZIP downloads. |
| 10 | Isolate projects/owners, one live worker plus one intercepted UI worker; complete browser suite | 151.30 (150.85–151.75) | 145.78 (145.77–145.79) | 3.6% lower process time. All 102 scenarios are preserved exactly once: 95 live and seven intercepted. Session ownership and fail-closed routing are checked independently of timing. |

Full-gate results are recorded separately because stage timings overlap.

The server filters use `FullyQualifiedName~` with `|` between these names. New class names in a
before filter match no cases until the corresponding move; the original class remains selected.

| # | Filter names |
| --- | --- |
| 1 | `BlobRecoveryTests`, `BlobManifestValidationTests` |
| 2 | `WorkQueueTelemetryTests`, `PasswordPrincipalProvisioningTests`, `SessionAuthenticationTests` |
| 3 | `DatabaseReadinessTests`, `DatabaseSchemaReadinessTests` |
| 4 | `DatabaseReadinessTests`, `DatabaseSchemaReadinessTests`, `InventoryReadinessTests`, `ReadinessAuthorityTests` |
| 5 | `CookieTicketContainsOnlyOpaqueTokenAndFormatVersion`, `DevelopmentCookieIsHttpOnlySameSiteAndNotSliding`, `ResolveTreatsMalformedTokenAsUnauthenticated` |

Each server state is built in Release before invoking `dotnet test` with `--no-build --no-restore`
and the selected filter. That timing is valid only for the verified current build. Browser
comparisons invoke the complete `npm test --prefix tests/Workbench.BrowserTests` command, with
the same prepared client/server outputs, pinned tools, and one live worker.

## Server coverage and mutation evidence

The expected inventory is 925 server cases: 910 original cases, nine fewer repeated physical
recovery drills, 25 new pure manifest cases, and one exact readiness duplicate removed. No
permission, transaction, concurrency, historical-schema transition, or recovery content path
was removed. The six prior-schema transitions still assert HTTP 503, live HTTP 200, and ready
HTTP 200 after migration. All 44 permission/procedure mutations now use the actual readiness
service and restricted web principal, with a healthy precondition before each mutation.

Cookie principal/options checks moved to `SessionCookieConfigurationTests`; malformed-token
rejection moved to `SessionTokenValidationTests`. Durable session and data-protection persistence
remain SQL-backed. A current-source build and all 28 pure/moved cases passed with no Docker socket
and an unusable Docker host, confirming that these selected cases do not start SQL.

Nine manual mutation probes were detected for their intended reasons:

| Deliberate mutation | Evidence |
| --- | --- |
| Remove a supported schema label | Its acceptance case fails. |
| Remove the manifest format guard | The invalid-format case fails. |
| Remove database binding | Both database mismatch/case checks fail. |
| Remove installation binding | The installation mismatch check fails. |
| Remove ordered entry equality | Six content/order checks fail. |
| Bypass the maintenance command's validation call | Both physical recovery tests fail on the wrong exception. |
| Reuse the template's proof key | The two-application isolation check detects equal keys. |
| Change readiness conjunction to disjunction | All 20 inventory-authority cases detect an incorrect healthy result. |
| Invert malformed-token early rejection | The SQL-free case attempts to construct its deliberately invalid connection and fails. |

Each mutation was restored byte-for-byte and checked with SHA256 before continuing. No automated
mutation runner is configured in the repository; these manual probes provide focused evidence,
not an automated run or a whole-suite mutation score. Removing the separate manifest count check
alone is equivalent for these finite lists because ordered equality also rejects different
lengths; the original guard remains unchanged.

## Browser coverage

Changes 6–9 retain the existing 101 browser cases and add one real-PNG capture contract. Their
combined standalone process midpoint fell from 208.60s to 151.30s (27.5%). This is a browser-stage
comparison, not a CI critical-path reduction. The CSV test retains the shared export width/theme
matrix; ZIP retains narrow-screen long-label geometry, keyboard selection, history, reload, and
native download. The inventory suite retains reduced-transparency coverage.

The camera-sized image remains in dedicated photo preparation/retry cases. Other workflows use
a reusable 320×240 real image through the normal file-input path. The source-level bulk collection
setup shrinks from 420 requests across two viewports to 106 for the retained live journey; the
phone journey uses controlled responses. The archive traversal removes 204 bulk setup requests.
These counts describe the fixture loops, excluding authentication and navigation traffic; they
are not network-counter measurements. CSV uses three representative records instead of another
large paging fixture. `InventorySearchTests`, `ItemRestorationTests`, and `ItemExportEndpointTests`
retain real query/cursor, archive, all-pages export, and tenant-boundary coverage.

Two manual application mutations verify the retained pagination assertions. Replacing page append
with replacement fails both phone and desktop journeys (three rows instead of 53); omitting the
submitted query fails all three search cases. Each probe rebuilt the client. The original source
was restored byte-for-byte after each, verified with SHA256, rebuilt, and the restored search
cohort passed all three cases. These deliberate failures are excluded from timing comparisons.
The screenshot helper also had a red-first real-browser contract: a no-op implementation failed
the explicit-capture assertion; the implemented default-off/explicit-on behavior passed every
post-change full run.

The isolation change preserves all 102 scenarios exactly once across its two projects. Red-first
checks detected an undeclared API call passing unchecked, two real captures exceeding the last
available diagnostic slot, and a shared identity where separate test owners were required. After
implementation, the real authentication cohort passes all six cases, including bidirectional
foreign-item 404 responses and continued usability of the cached live session after auth-owner
revocation. The guard contract also verifies that its teardown failure retains safe diagnostics.

All ten Node harness contracts pass. The expanded CI contract command took 3.474s versus 3.489s
for the original four contracts in single local samples. Treat that as no demonstrated cost
change, not a speedup; these checks run outside the timed browser stage.

Alternating full-suite runs used one UI worker, two, one, then two, while live concurrency stayed
at one. Two UI workers took 146.799s and 147.654s (midpoint 147.227s), compared with 145.792s and
145.769s (midpoint 145.780s) for one. The default therefore remains one UI worker. The final
standalone browser midpoint is 30.1% below the original 208.60s, including the added contracts.
These two-sample comparisons are observed effects, not statistical confidence intervals.
The samples above precede trailing blank-line cleanup and the phone-fixture ordering correction
described below. Final verification and its timings use the corrected source.

Two additional real harness mutations were detected: allowing undeclared traffic through still
failed the backend-hit assertion (one instead of zero), and weakening exclusive directory
reservation failed the concurrent-capture assertion (two instead of one). Both files were
restored byte-for-byte and SHA256-checked; all ten restored contracts passed again in 3.663s.

## Final verification discoveries

Two concurrent full-gate attempts exposed an undeclared inventory request in the phone search
fixture (141ms and 155ms failures). Installing its interception after login allowed the initial
live collection request to start before synthetic response ownership. The fixture now registers
before navigation. A lower-priority sentinel in the existing phone case, synchronized with the
initial list response, fails deterministically with the old order and passes with the fix. All
three corrected search cases pass; unknown inventory requests still fail closed. The corrected
full browser suite passes all 102 cases in 148.05s (130.08s summed case duration), a single sample
29.0% below the original two-run midpoint. No retries,
timeouts, or product rate limits were relaxed. These failed gates are excluded from speedup claims.

The second attempt also failed one unchanged export-interruption case: its expected deadline
collection was empty after the test observed a blocked SQL request. The helper accepts any lock
wait in the test database rather than correlating it with the export query. The exact triggering
SQL row was not retained. This test and its production path are unchanged; all 925 server cases
passed in the first candidate gate. The isolated failure remains a known synchronization limit,
not evidence of a production timeout regression or a successful gate.

## Same-host full-gate comparison

The untouched base revision passed the complete Linux gate in 474.80s. Its prerequisite phase
took 139.94s, server stage 412.13s, browser stage 253.59s, client stage 58.37s, and published-output
checks 44.27s. Server partitions took 400.0s and 406.5s, with all 910 cases passing exactly once.
Stages overlap; their durations are not additive.

This successful comparison uses native Linux temporary storage and a numeric Docker-host IPv4
address. Earlier attempts with incompatible temporary-storage or hostname settings are excluded.
The unchanged Azure emulator test passed independently after the hostname correction, before
rerunning the full baseline. No application or test change was needed for that correction.

The corrected implementation at `46b187f` passes the full gate in **389.27s**, compared with
**474.80s** for the untouched base: **85.53s / 18.0% lower**. This is one successful full run per
revision, not a statistical estimate. Both use the same Linux runner, host, resource policy, and
`verify.ps1 -SkipDependencyInstall` command. The additional ignored diagnostic reporter retains
only allowlisted failure categories and reports no failures in the corrected run.

| Stage | Base | Corrected implementation |
| --- | ---: | ---: |
| Gate wall time | 474.80s | 389.27s |
| Prerequisites | 139.94s | 126.91s |
| Server | 412.13s | 330.61s |
| Browser | 253.59s | 182.53s |
| Client | 58.37s | 54.68s |
| Published output | 44.27s | 42.70s |

All 925 server cases pass exactly once across partitions of 454 and 471 cases (322.3s and 325.1s).
All 102 browser cases and 329 client cases pass. The earlier unchanged export-interruption
failure did not recur; its synchronization limitation above remains documented. Formatting,
lint, typechecking, generated API drift, Release build, and published-output checks also pass.
The required hardened-container smoke check also passes on the same implementation in 76.95s,
including its SQL-backed runtime and Compose/session-persistence checks. Independent review of
the complete base-to-implementation range found no actionable issues.

## Exact-base CI baseline

[CI run 35134950678](https://github.com/mcunille/Workbench/actions/runs/35134950678) verified
base revision `7ccc216c17c756995b86b1b52f5a8725fd1f478a` successfully on a four-CPU runner.
The source/release job took 730s, including setup. The measured verification gate took 630.38s;
its server stage took 503.27s and browser stage took 321.27s. Server partition times were
497.33s and 496.14s. The client stage took 117.94s and published-output probes took 5.35s.
These overlapping stages are not additive. A final CI run will be reported separately from local
experiments, because hosted runners and caches vary.
