# Database migrations

Database migrations are an explicit, human-controlled deployment operation. A web replica never
migrates its database. Use the migrator identity defined in the authoritative
[database-principal matrix](database-principals.md); keep setup and operator authority out of web configuration.
That matrix also separates the worker and storage-maintenance roles. The worker's cross-tenant
queue status permission returns aggregate counts and age only; it does not grant general tenant-data
browsing. Storage manifest/recovery authority belongs to protected maintenance tooling.

## Deployment procedure

GEM-02 adds `20261001000000_AddSharedGemReference` after the merged
`20260928071548_AddSupplierOpenItems` baseline. The single additive migration creates the
shared `Gemology` schema, attribution/locality/retirement integrity, restricted read grants,
and the matching readiness marker. It preserves existing tenant records and installs no pilot
catalog content or runtime catalog writers. Its Down operation throws 50020 to preserve stable
reference identities and provenance; use forward correction or protected restore with matching
binaries. Verification evidence is recorded only after the current-source migration drills pass.

1. Identify the immutable application revision and its expected migration in the matrix below.
2. Confirm a current, restorable backup and the application's schema compatibility window.
3. Stop or drain incompatible writers when the migration design requires it.
4. Supply the migrator connection through an access-controlled temporary connection file.
5. Run the published database tool as a one-shot job:

   ```powershell
   Workbench.Database migrate --connection-file <path> --expected-database <name>
   ```

6. Remove the connection file and secret from the job environment.
7. Run database permission probes and confirm `/health/ready` succeeds with the web principal.
8. Release only the application revision proven compatible with that schema.

Application rollback is safe only within a verified schema compatibility window. An older binary
may reject a newer readiness marker. Do not improvise a down migration against live data; use a
reviewed forward correction or the [restore and sanitation procedure](database-backup-restore.md).
Blob-bearing restores must complete either the [strict paired SQL/blob workflow](blob-and-service-providers.md#offline-reconciliation-paired-backup-and-restore)
or [reviewed SQL-authoritative recovery](online-backup-recovery.md#manual-recovery), including its explicit
missing-file acceptance when needed. Both preserve the SQL restore guard and mandatory sanitation.
Development verification does not authorize production migration, rollback, or cutover.

## Local one-time setup

### Pilot catalog distribution and installation

GEM-04 adds `20261002192523_InstallGemReferencePilot` after the merged
`20261001072507_AddServiceAdminIdentity` baseline. The canonical reviewed input is
[`data/gem-reference/sample.json`](../../data/gem-reference/sample.json): Diamond, Sapphire,
Emerald, Ruby, and nineteen source assertions, reviewed on 2026-10-02. No aliases or locality
claims are asserted. The absence of optional fields is preserved; installation adds no content.

The server migration assembly embeds the frozen
[`InstallGemReferencePilot.sql`](../../src/Workbench.Server/Persistence/Migrations/InstallGemReferencePilot.sql)
distribution, including deterministic entry/source IDs and identity keys. Published database
tools and runtime images carry this assembly, so a checkout, separate content file, and internet
access are unnecessary at installation. The source-to-seed SQL test compares every value and
citation against the committed package. Historical seed SQL must not be regenerated when the
sample or current validation rules later change.

Run the ordinary migrator deployment procedure above, or `./scripts/dev-up.ps1` for an isolated
developer preview. On a fresh database the migration installs the exact pilot. On upgrade it
inserts only missing pilot IDs, preserving each retained entry's fields, sources, aliases,
locality, retirement, and rowversion. Sources install only for newly inserted entries. A
conflicting active identity under another ID fails with 50020; do not delete content or reset
migration history to force installation. Inspect the conflict and arrange a reviewed forward
correction before retrying. A source-ID collision also fails the transaction under the primary
key constraint.

All inserts, the readiness update, and migration history commit together. Ordinary reruns do
not replay the seed, recreate removed entries, or replace later published edits. Future content
changes belong to service-admin curation, not deployment seeding. Down is guarded with 50020
to protect published identities and provenance; use forward correction or protected recovery.

Follow the [canonical setup guide](../setup.md) for generated credentials, SQL containment,
bootstrap, routine migrations, and existing-database precautions. See
[principal provisioning and secret delivery](database-principals.md#provisioning-and-secret-delivery)
for password/Entra identities and the tenant proof key.

For unmerged schema changes and preview-data preservation, follow
[authoring and validating a migration](#authoring-and-validating-a-migration). Historical preview
transitions documented below are already merged history, not exceptions for new PRs.

The accounting foundation starts with `20260921051843_AddAccountingFoundation`, after
`20260921041331_MakeSupplierProfilesCustom`. This single accounting migration advances readiness from the supplier-profile marker. The foundation preserves existing users and POs, creates
unassigned accounting-role definitions, and protects accounting and role/claim writes. Fresh creation
and upgrade from the preceding schema are required verification paths. Down is blocked with error
50020 to preserve configuration, role-assignment, revision, and receipt evidence. Use a reviewed forward
correction or protected restore; the prior application's readiness marker does not accept this schema.

BK-04 ships one additive migration, `20260926210900_AddPurchaseRecognition`, from the merged
`20260925044758_AddAccountingPeriodControls` baseline. It installs recognition tables and restricted
posting/correction commands, updates receipt-accrual mapping validation and supplier-history guards,
and advances readiness. It preserves existing BK-03 source, journal, configuration, correction,
period-closure and receipt bytes; original request retries continue to return their original results.
Fresh creation installs no synthetic adapters or financial source permissions. Runtime direct writes
and command execution remain denied. Current binaries reject the predecessor, and the predecessor
marker rejects the current schema. Down fails with 50020: use a reviewed forward correction or
protected restore. Disposable migration tests establish these contracts; retained preview upgrades,
production migration, full verification and cutover require their separate workflow and authority.

## Supplier open-item migration and recovery

BK-06 has one forward migration, `20260928071548_AddSupplierOpenItems`, after merged BK-05
`20260928034802_AddSupplierBills`. It installs supplier item/payment/application history, exact
control attribution, internal commands, three narrow web report-function SELECT grants and the
current readiness marker. No financial mutation EXECUTE grants or production permission assignments
are introduced. Use matching binaries; the prior schema is not ready for the current application.
Down is blocked with 50020 to preserve financial evidence.

Derivation reads supported stored BK-04/BK-05 invoice evidence and corrections, retaining original
posting/recorded times and source, journal, bill, correction and receipt bytes. Existing request
retries retain their original result. Unknown or inconsistent controls remain unresolved; no guessed
bill, opening balance or available capacity is manufactured. Re-derivation can reconstruct missing
recognition attribution only after full ownership proof, including original and reversal source
identity. Conflicting/detached rows prevent repair of that group. It does not rebuild arbitrary future
source adapters or amend financial events, and runtime users cannot invoke a repair endpoint.

Disposable SQL tests have exercised fresh installation, upgrade with existing bills/recognition
corrections from the actual merged BK-05 schema, guarded SQL backup/restore, exact replay, tenant
isolation, attribution reconstruction and blocked Down. Recovery retains financial-group sequence,
applications/inverses, principal authority and explicit missing-document disposition. Restore stays
RESTRICTED_USER until the independently verified pending marker, sanitation and applicable file
recovery acceptance finish. This evidence is not a live Azure or paired blob-copy disaster drill and
does not establish BK-07 physical retention holds. Full release verification, separate hardened
container smoke and retained-preview/browser inspection remain pending for BK-06.

An earlier unmerged BK-06 preview installation is not another supported migration baseline. If it
needs preservation while installer SQL changes, use a reviewed forward refresh with before/after
evidence; rerunning an already-recorded migration does not reinstall its procedures. Never delete
financial history or reset migration markers as routine preview refresh.

### Financial evidence migration and recovery

`20261008010000_AddFinancialEvidenceRetention` is the sole BK-07 release migration after merged
`20261003214043_AddTenantGemReference`. It installs tenant-qualified immutable evidence records,
independent/effective holds, source capture, disposal and current recovery guards without rewriting
merged migrations or financial hashes. Deploy matching binaries with older writers/workers stopped;
the compatibility hold protects old cleanup readers but does not authorize mixed-version operation.

Backfill authentic posted bill/payment/recognition evidence, including zero-value and reversed
sources, with indefinite protection. Draft/review-only records are excluded. Legacy descriptive
recognition strings remain unresolved and cannot invent source mutation authority. Malformed or
contradictory typed source identity aborts the migration transaction with source/owner/tenant
diagnostics; correct the source through a reviewed procedure before retrying. Removed/purged
metadata is preserved and surviving bytes require recovery verification; no document is reactivated.

Disposable tests cover fresh creation, actual merged-base upgrade, preserved financial/replay bytes,
invalid-source rollback and blocked Down. Recovery inventory includes links, deadlines, receipts,
holds and removal state; acceptance requires its exact current fingerprint. Destructive Down remains
blocked with 50020. Use forward correction or guarded paired recovery. A preview that applied an
earlier unmerged installer needs a separately reviewed data-preserving refresh; never reset migration
history or delete retained data. Full gate and preview outcomes belong in the dated BK-07 evidence.

### Supplier credit and refund migration and recovery

`20261019010000_AddSupplierCreditsAndRefunds` follows the BK-07 evidence migration. Use the matching
binary and stop incompatible writers during installation. The migration creates the typed source
records and owner procedures and adds report-only grants under tenant RLS. Existing stored sources,
journals, receipts and holds retain their bytes. No synthetic credit or refund is derived from a
document or legacy journal. Restore the matching SQL and blob pair through the guarded recovery
procedure. Down rejects with 50020 before dropping financial history.

## Authoring and validating a migration

Before opening or updating a pull request, consolidate development-only migrations introduced by
the current change into one migration per coherent release change. Preserve dependency ordering,
custom SQL, security controls, data transformations, and rollback guards; update the final
designer/model snapshot, migration-history assertions, and schema-version references, then verify
fresh-database creation and upgrade from the merged PR base schema. Keep separate migrations only
when a staged deployment, backfill, or release compatibility boundary requires them, and explain
that reason in the PR.

Migrations become durable when their pull request is merged into `main`. Never rewrite merged
migrations. Applying an unmerged migration to a local, retained, or shared preview does not prevent
consolidation or establish a supported upgrade baseline. It does not justify a corrective migration
solely to preserve that preview's migration sequence.

Use disposable databases to verify unmerged changes. If preview data needs to be kept, handle its
recovery or recreation as a separate local operation; it does not add a release compatibility
obligation. This policy does not authorize deleting databases, discarding data, or automatically
resetting migration-history rows.

Keep migrations deterministic and reversible where SQL Server permits. Review generated SQL and
permission changes, especially RLS predicates, grants, denials, migration history, security tables,
and readiness procedures. Verify fresh creation and upgrade from the immediate supported predecessor
with retained tenant/identity data and affected collection records, photos, pending/completed photo
operations, and immutable replay evidence. Collection additions require no item backfill.

Run all four drills against disposable real SQL Server databases:

```powershell
./scripts/verify-migrations.ps1 -Scenario Clean
./scripts/verify-migrations.ps1 -Scenario Upgrade
./scripts/verify-migrations.ps1 -Scenario ReversibleRollback
./scripts/verify-migrations.ps1 -Scenario RestoreRollback
./scripts/verify-database-permissions.ps1
```

The full `./scripts/verify.ps1` gate runs all migration drill tests once in the unfiltered Release
server suite and retains outcomes/timings in `artifacts/test-results/*.trx`. Focused scenario reruns
retain console logs in `artifacts/migrations/`. Clean applies every migration to an empty database.
For the initial release, Upgrade starts at `InitialSchema`; later releases use the previous supported
release. The historical ReversibleRollback scenario now verifies that blob metadata refuses
destructive Down. RestoreRollback validates restored-schema recovery and mandatory sanitation.
Permission probes exercise actual restricted principals; see the
[principal reference](database-principals.md#source-and-verification).

## Migration compatibility matrix

[Read this section](database-migration-compatibility.md#migration-compatibility-matrix).

### Retained PO-04 development preview reconciliation

[Read this section](database-migration-retained-previews.md#retained-po-04-development-preview-reconciliation).

### Maintaining the current schema contract

Generate application migrations with explicit `--context WorkbenchDbContext`, for example
`dotnet ef migrations add <Name> --project src/Workbench.Server --context WorkbenchDbContext`.
The web host also has a tenant-free `DataProtectionKeyDbContext` for the existing key table; it
does not own application migrations. Service-admin command storage remains SQL-owned, so the
application designer and snapshot do not map its accounts or sessions into tenant EF authority.

`Persistence/CurrentSchema.cs` declares the ordered release migration history; its final entry
is the current application boundary used by readiness and newly emitted blob manifests.
Append an ordinary migration there when adding its EF migration. `CurrentSchemaTests` compares
the complete contract with the compiled EF inventory, independently of applied database history.
Keeping the manifest explicit makes an accidental migration omission, rename or addition a
discrepancy requiring review; deriving the expectation from EF would hide that discrepancy.
The SQL migration must still advance the readiness procedure's marker explicitly. Historical
migration SQL and upgrade fixture IDs remain immutable.

Current-history integration assertions use `MigrationHistoryAssertions` to compare every ID in
order, including explicitly named retained preview migrations. Dedicated migration tests check
readiness against the shared current marker; recovery tests check the emitted backup marker.
Acquisition and financial tests assert their own preservation and domain behavior without
repeating a SQL-definition marker check. They need no literal or total-count update.
Fixed upgrade tests, such as supplier migration consolidation, migrate to their named historical
boundary so their independent one-migration assertion survives later releases.

Backup manifest compatibility automatically includes known releases in `CurrentSchema.Migrations`
from `20260907194500_AddItemDetailEditing`, the first supported boundary, onward.
`StorageMaintenanceCommand` also retains six fixed exceptions for retired development markers
whose backups remain supported. Ordinary migrations need no additional backup allowlist or
literal test entry: `BlobManifestValidationTests` enumerates the known supported releases and
independently checks the fixed boundary, retired markers, unsupported versions, and exact-pair
bindings. A marker must be known, not merely fall within the supported timestamp range.
Revisit this compatibility policy when changing the manifest format or storage recovery behavior.
Schema acceptance does not prove backup integrity; manifest bindings, retained entries, and blob
bytes must still pass recovery verification. Guarded restore and downgrade requirements are unchanged.
