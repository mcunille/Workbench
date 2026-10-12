# Gem reference release acceptance

This record maps the first reference-library release to its executable acceptance owners.
The supported workflows are described in the [tenant guide](gem-reference.md),
[shared curation guide](gem-reference-curation.md), and [architecture](ARCHITECTURE.md#shared-gem-reference-foundation).
The release scope is the [gemological reference specification](specs/2026-09-29-gemological-reference-library.md).

## Requirement reconciliation

Test paths below are relative to `tests/`; each suite exercises current application code.
SQL tests use disposable SQL Server databases; live browser tests use the protected APIs and
real SQL, while `.ui.spec.ts` cases own intercepted interaction and presentation failures.

| Requirement | Implementation and acceptance owner |
| --- | --- |
| GEM-01: reviewed starter content | `data/gem-reference/sample.json` contains Diamond, Sapphire, Emerald and Ruby with nineteen field-level assertions. `Workbench.Server.IntegrationTests/GemReferencePilotAcceptanceTests.cs` compares every installed value and source assertion with this independently maintained package. |
| GEM-02: shared model, search, validation and grants | `src/Workbench.Server/Gemology/` and `Persistence/GemReferenceModel.cs`; `GemReferenceInputTests`, `GemReferenceReadTests`, `GemReferenceDatabaseTests`, `GemReferenceCursorTests` and `GemReferenceMigrationTests`. |
| GEM-03: separate service-admin authority | `ServiceAdministration/` and narrow operator commands; `ServiceAdminOperatorCommandTests`, `ServiceAdminAuthEndpointTests`, `ServiceAdminIdentityDatabaseTests`, `ServiceAdminSessionTests` and `ServiceAdminRestoreTests`. |
| GEM-04: repeatable installation without overwriting curation | The frozen pilot migration; `GemReferencePilotAcceptanceTests` covers exact content on fresh creation and upgrade from the actual pre-pilot baseline, then real publication of a correction and retirement followed by installer/migration replay without content or version changes. `GemReferenceMigrationTests` also checks inventory preservation, readiness, grants and guarded rollback. |
| GEM-05: durable drafts, atomic publication and retirement | `GemReferenceDraftService`, `GemReferencePublicationService`, and command-owned SQL; `GemReferenceDraftTests`, `GemReferenceReviewTests`, `GemReferencePublicationTests`, `GemReferenceCurationEndpointTests` and `GemReferenceCurationMigrationTests`. |
| GEM-06: tenant additions, sparse overrides, effective values and sources | `GemReferenceTenantService`, `GemReferenceEffectiveProjection` and tenant SQL policies; `GemReferenceTenantWriteTests`, `GemReferenceTenantDatabaseTests`, `GemReferenceTenantEndpointTests`, `GemReferenceEffectiveReadTests`, `GemReferenceEffectiveProjectionTests` and `GemReferenceTenantMigrationTests`. |
| GEM-07: admin staging, review, publication and recovery UI | `Workbench.BrowserTests/gem-curation.spec.ts` exercises saving, reload, multi-entry publication, retirement, stale draft/base reconciliation, session recovery and cross-role denial; `gem-curation.ui.spec.ts` owns keyboard/narrow-screen and lost-response presentation. |
| GEM-08: tenant browsing, attribution and scientific limits | `Workbench.BrowserTests/gem-reference.spec.ts` exercises all four pilot entries and tenant isolation; `gem-reference.ui.spec.ts` owns keyboard, phone layouts, source navigation, retries and needs-review rendering. |
| GEM-09: tenant editing and reconciliation UI | `Workbench.BrowserTests/gem-reference-editing.spec.ts` exercises creation, concurrent editing, archive/restore, upstream classification/source correction, invalid-save rejection, field reset, whole-entry reset and another tenant; `gem-reference-editing.ui.spec.ts` owns retained validation and uncertain creation. |
| GEM-10: joined release journey | `Workbench.BrowserTests/gem-library-acceptance.spec.ts` creates a sourced admin draft, proves it is hidden before publication, reviews/publishes through the UI, browses in two tenants, customizes and resets a sourced field, and creates/reloads a private addition inaccessible to the second tenant. |
| Backup includes the complete reference domain | `ServiceAdminRestoreTests.GuardedRestoreCannotResurrectPreBackupAuthority` now populates shared aliases/locality/sources, a tenant addition with archived state, an override, an unpublished draft, a publication receipt and audit. A real checksum backup/restore and operator sanitation must preserve their complete ordered SQL snapshots, including IDs, JSON, dates and versions. |
| Restore cannot resurrect revoked authority | The same restore theory rolls back later disablement or revocation, verifies pending readiness, and rejects retained cookies with both cached and fresh key-ring hosts and the raw session token. It also checks eligibility for fresh sign-in; the companion failure test proves sanitation rollback remains guarded. |

## Source review

On 2026-10-11 the four sample entries were checked against their linked primary GIA pages:
[Diamond](https://www.gia.edu/diamond-description),
[Sapphire](https://www.gia.edu/sapphire-description),
[Emerald](https://www.gia.edu/emerald-description), and
[Ruby](https://www.gia.edu/ruby-description).
The pages support the recorded mineral/species/variety identities and concise descriptions:
carbon diamond, sapphire and ruby as corundum varieties, and emerald as green to bluish green
beryl. The sample deliberately supplies no mineral group, alias, locality or numerical property.
This acceptance review does not rewrite the frozen migration or the assertions' original
2026-10-02 review/access dates. Later editorial corrections use service-admin publication.

## Verification record

The acceptance pass uses the repository's full `./scripts/verify.ps1` and
`./scripts/smoke-container.ps1` gates; focused cases do not replace those checks.
The GEM-10 PR owns the final run evidence and external screenshots. Backups, credentials,
browser cookies and media remain outside Git.

On 2026-10-12, final current-source gate `d8a48293ad144efe8a2fa7c513c40b38` passed
with 2,001 server cases across four partitions, 633 client tests and 142 Playwright cases;
none failed or were skipped. Locked restore, formatting, lint, Release build, generated API
drift, client typecheck/build and published release-unit checks also passed. The gate took
2,178 seconds on the shared local host. Its ignored `artifacts/verification/` directory owns
the manifests, exact test inventory and partition results. The container smoke separately
passed internal TLS, secure-cookie login, durable sessions after app replacement, protected
forwarding headers, private listeners and worker telemetry. Public CA issuance and SMTP
delivery were not exercised. Only documentation status/evidence updates followed the gate;
application and test source remained unchanged.

Playwright also exercised the isolated preview at `http://localhost:32772` with separately
operator-provisioned service-admin and second-tenant accounts. It verified dedicated sign-in,
all four pilot entries and their GIA sources, hidden saved draft, admin review/publication,
tenant customization and field reset with restored source navigation, private addition/reload,
and direct cross-role/cross-tenant denial. Desktop (1440×1000) and phone (390×844) screenshots
were inspected; the phone detail had no horizontal overflow. There were no uncaught application
errors or unexpected console errors. Four expected browser network errors corresponded to
two deliberately inaccessible detail reads (404) and two wrong-authority API requests (401).
The Browser plugin was unavailable; regular Playwright Chromium drove this acceptance.

Two manual production-rule mutations were detected by the fifty effective-projection cases:
retaining displaced Workbench citations caused five failures, and suppressing needs-review
caused eight. Original source was restored byte-for-byte and all fifty cases then passed.
This is focused mutation evidence, not a whole-catalog mutation score. The new snapshot helper's
first implementation was also challenged: assertions for later tenant/draft data failed in all
three restore cases because a top-level SQL `FOR JSON` result was truncated by scalar reading.
A scalar subquery now returns complete JSON, and all six restore/pilot acceptance cases pass.

## Coverage limits

This is a four-mineral starting reference, not an exhaustive catalog. Structural cases such as
non-minerals, aliases and exceptional localities use explicitly synthetic acceptance data.
No specimen identification, inventory linking, numerical reference properties, bulk import,
historical rollback UI or production load benchmark is included. Browser acceptance uses
Chromium; intercepted phone/light/dark cases supplement live workflows without establishing
other browser engines. SQL restore drills use disposable local SQL Server; they do not prove
hosted Azure recovery or operational production cutover.
