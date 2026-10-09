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

Migrations become durable when their pull request is merged into `main`. Never rewrite merged
migrations. Before merge, consolidate development-only migrations into one migration per coherent
release change, even if an earlier revision was run in a local, retained, or shared preview.
Running unmerged code does not establish a supported upgrade baseline or justify a corrective
migration solely to preserve that preview's migration sequence.

Verify fresh-database creation and upgrade from the merged PR base. Separate migrations within a
PR still need a concrete staged deployment, backfill, or release compatibility boundary. Historical
preview transitions documented below are already merged history, not exceptions for new PRs.

Use disposable databases to verify unmerged changes. If preview data needs to be kept, handle its
recovery or recreation as a separate local operation; it does not add a release compatibility
obligation. This policy does not authorize deleting databases, discarding data, or automatically
resetting migration-history rows.

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

An earlier unmerged BK-06 preview installation is not another supported migration baseline. If it
needs preservation while installer SQL changes, use a reviewed forward refresh with before/after
evidence; rerunning an already-recorded migration does not reinstall its procedures. Never delete
financial history or reset migration markers as routine preview refresh.

## Authoring and validating a migration

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

Rows are ordered by application. The predecessor column names the preceding row's full migration
ID by its unique suffix; the first migration starts from an empty database. Each release must have
its required schema and permissions before readiness succeeds; liveness remains independent.
This inventory describes checked-in migration behavior, not permission to execute Down in production.

| Migration ID | Predecessor | Compatibility and retained-data obligations | Down behavior |
| --- | --- | --- | --- |
| `20260904061204_InitialSchema` | Empty database | Initial tenant, identity, and security-audit tables; not a current runtime baseline. | Drops base tables; no destructive-data guard. |
| `20260904061246_EstablishSecurityBoundaries` | `InitialSchema` | Shipped identity baseline: RLS, roles, identity/admin commands, proof key, readiness and restore state. | Removes security controls and state; no destructive-data guard. Not a supported live rollback. |
| `20260905222755_AddBlobAndOperationalProviders` | `EstablishSecurityBoundaries` | Consolidated provider release; retains identity baseline and adds storage metadata, queue and restricted worker/maintenance roles. Paired SQL/blob recovery required. | Always blocked to preserve blob/operational metadata. |
| `20260906031109_AddDeploymentQueueTelemetry` | `AddBlobAndOperationalProviders` | Adds aggregate worker telemetry and deployment readiness. Apply before matching web/worker activation. | Removes additive procedures and restores provider readiness marker without deleting durable data; verify old binary compatibility. |
| `20260906092000_DeferInvitationIdentityClaim` | `AddDeploymentQueueTelemetry` | Stop old web replicas first. Releases pending/cancelled credentialless global login claims, retaining users, roles, tokens, delivery work and accepted password identities (including disabled accounts). Matching web requires invitation-claim EXECUTE. | Always blocked: restoring old claims could collide with identities accepted since migration. |
| `20260907054000_AddProviderRetryDelay` | `DeferInvitationIdentityClaim` | Apply before matching web/worker release. Adds bounded delay to `Operations.RetryWork`; pending work, leases, five-attempt limit and terminal cleanup remain intact. | Always blocked; forward correction or verified restore. |
| `20260907060000_AddCollectionNotebook` | `AddProviderRetryDelay` | Adds tenant-owned items, constraints/RLS and SELECT/INSERT; advances readiness and backup schema boundary. Existing tenant/identity data retained. | Always blocked to preserve collection records. |
| `20260907082353_AddItemPhotographs` | `AddCollectionNotebook` | Adds current-photo pointer, photo/history tables and `SetItemPhoto`; items retain text and start without photos. Advances readiness, grants and paired-backup schema. | Always blocked to preserve photos and operation history. |
| `20260907194500_AddItemDetailEditing` | `AddItemPhotographs` | Adds `UpdateItemDetails` and immutable first-edit creation snapshots; runtime snapshot access is SELECT-only. Requires new marker/EXECUTE; retain existing items/photos. | Always blocked to preserve edits and creation evidence; tenant-filtered emptiness cannot authorize deletion. |
| `20260907224158_AddItemArchiving` | `AddItemDetailEditing` | Adds archive state/index and `ArchiveItem`; existing rows remain active. Detail/photo commands require active state; direct UPDATE/DELETE stays denied. Retains photos and replay evidence. | Always blocked to preserve archive state. |
| `20260907225320_AddOnlineRecovery` | `AddItemArchiving` | Adds missing-file dispositions, recovery reports/acceptance and readiness; retain guarded sanitation and tenant isolation. | Always blocked; forward correction or isolated recovery. |
| `20260908010000_AddItemRestoration` | `AddOnlineRecovery` | Adds `RestoreItem` EXECUTE and readiness marker without changing item table; retains identity, creation snapshots, photos, replay evidence and recovery state. | Supported one-step schema/binary rollback after draining restoration writers: removes only restore procedure and restores online-recovery marker. Does not reverse restored items; predecessor's own Down remains blocked. |
| `20260909034719_AddAcquisitionContext` | `AddItemRestoration` | Acquisition context, tenant-qualified links, immutable replay evidence and restricted create/update commands. Readiness, provisioning and backup markers advance. Retain active/archived items, photos and links. | Always blocked to preserve acquisition/replay evidence; forward correction or paired recovery. |
| `20260910071000_AddSharedAcquisitions` | `AddAcquisitionContext` | acquisition browsing index and restricted `ChangeAcquisitionLink` EXECUTE. Readiness, provisioning and backup markers advance. Item-first then ordered acquisition locks check versions for atomic connection corrections. Retain identities, archived links and immutable creation evidence; removing the final link retains the acquisition. Verify fresh schema and upgrade from H9, including old creation replays after correction. | Always blocked; shared relationships require forward correction or guarded recovery. |
| `20260911184933_AddAcquisitionDocuments` | `AddSharedAcquisitions` | Tenant-owned document metadata and immutable request evidence, restricted preparation/finalization commands, pending capacity reservations and existing attachment retention. Readiness and backup markers advance. Verify fresh creation, H10 upgrade preserving item/photo/shared-acquisition data, document replay, and paired SQL/blob recovery with explicit missing-file disposition. | Always blocked; preserve document and request evidence through forward correction or paired recovery. |
| `20260912030844_AddDraftSupplierOrders` | `AddAcquisitionDocuments` | Consolidated tenant-owned draft orders and compact request receipts, RLS, restricted create/update/delete commands, final source-link validation, cleared deletion tombstones, tenant-qualified actor references and checked document replacement. Readiness, provisioning and backup markers advance. Fresh creation and H11 upgrade preserve identity, holdings, acquisitions and documents; draft saves create no inventory or financial records. | Always blocked; retain drafts and retry receipts through forward correction or guarded recovery. |
| `20260912064156_AddSupplierIdentityAndPurchaseReferences` | `AddDraftSupplierOrders` | Tenant-owned suppliers, compact supplier receipts, purchase counters, contact snapshots and per-order platforms. Backfills permanent numbers for active drafts without inventing suppliers; retains tombstones and V1 receipts. Adds restricted V2 draft/supplier commands and legacy replay-only save behavior; advances readiness and backup markers. Verify fresh creation and PO-01 upgrade preserving content, references and exact replay evidence. | Always blocked; permanent identities and request evidence require forward correction or guarded recovery. |
| `20260917010000_AddSupplierBasedDraftPricing` | `AddSupplierIdentityAndPurchaseReferences` | Consolidated PO-03: content schemas 1/2/3, fingerprints 1/2/3/4, restricted V3 compatibility and V4 supplier-pricing commands. Preserves legacy content and receipts; advances readiness and backup markers. | Always blocked; pricing content and request evidence require forward correction or guarded recovery. |
| `20260917015000_PrepareRetainedBetaFinancialUpgrade` | `AddSupplierBasedDraftPricing` | No-op on the main release lineage. On retained beta-only databases, temporarily reconstructs the internal SQL prerequisites for the immutable pending PO-05 migrations, without altering drafts, receipts or applied history. All application writers must be stopped until the final integration migration completes. | Always blocked; complete the forward transition. |
| `20260917020000_AddDraftFinancialAdjustments` | `PrepareRetainedBetaFinancialUpgrade` | PO-05 content schema 4 and in-place V4 discount/charge validation. Preserves prior content and receipt fingerprints; protects currency and confirmed corrections; advances readiness/backup markers. | Always blocked (50020); preserve financial draft inputs and replay evidence with a forward correction or guarded recovery. |
| `20260917030000_ProtectConfirmedSupplierChargeCorrections` | `AddDraftFinancialAdjustments` | Forward correction to the locked V4 save guard: supplier ID/name snapshot changes require new notes for retained confirmed supplier charges. Preserves content, receipts and third-party payees; advances readiness/backup markers. Verify fresh creation and upgrade from the financial schema with receipt replay. | Always blocked (50020); retain correction protection through a forward migration or guarded recovery. |
| `20260917080000_ConsolidateBetaDraftCommands` | `ProtectConfirmedSupplierChargeCorrections` | Retires parallel purchasing writers, installs the single beta write implementation and restricted receipt lookup, preserving content and receipt bytes. Advances readiness and backup markers. Stop prior application instances before migration, then deploy the matching frontend/server together. | Always blocked; retired commands require forward correction or guarded recovery. |
| `20260918010000_RemoveHistoricalDraftReplay` | `ConsolidateBetaDraftCommands` | Removes the development-only receipt replay procedure and its grants while retaining draft and receipt rows. Advances readiness and backup markers; current beta retries still use the current write commands. | Always blocked; use forward correction or guarded recovery. |
| `20260918020000_IntegrateBetaDraftFinancialAdjustments` | `RemoveHistoricalDraftReplay` | Installs PO-05 validation and confirmed supplier correction protection on the single beta writer, removes all temporary V3/V4 prerequisites, and advances readiness/backup markers. Retains schema 1/2/3/4 content and exact receipt bytes. | Always blocked; use forward correction or guarded recovery. |
| `20260918060000_AddPurchaseOrderCommitment` | `IntegrateBetaDraftFinancialAdjustments` | Consolidated PO-04: adds Draft/Ordered state, order date, immutable snapshots and actor-bound receipts, RLS and restricted commands. Includes retained-line projection, trimmed amendment reasons, supplier GUID normalization and decoded-content no-op detection. Preserves existing drafts, references, row versions and receipts without inferring commitments. Protects draft writers after receipt replay; advances readiness and backup markers. Stop older writers and deploy the matching beta application. | Always blocked; preserve agreed contents and history through forward correction or guarded recovery. |
| `20260918061646_AddPurchaseOrderDocuments` | `AddPurchaseOrderCommitment` | Adds PO-owned private document metadata and durable request evidence, tenant-qualified ownership, RLS and restricted prepare/finalize procedures. Reserves up to 20 current or pending files per ordered PO, serializes parent versions and uses the existing publication, reconciliation and seven-day retention lifecycle. Preserves acquisition documents and ordered-content revisions. Advances readiness and backup markers; deploy the matching application. | Always blocked; retain metadata and command evidence through forward correction or guarded paired recovery. |
| `20260918063409_HardenPurchaseOrderDocumentAuthority` | `AddPurchaseOrderDocuments` | Revalidates enabled tenant and active actor authority for document reservations, binds request replay to the original actor, and rechecks authority during finalization. A suspension during publication produces a terminal conflict and retains published bytes for cleanup. Kept as a separate forward migration because the predecessor was already applied to the retained local preview; its applied history is immutable. Advances readiness and backup markers. | Always blocked; retain authority controls through forward correction or guarded paired recovery. |
| `20260921041331_MakeSupplierProfilesCustom` | `HardenPurchaseOrderDocumentAuthority` | Adds the optional JSON collection of custom platform/handle pairs and updates the restricted supplier writer in one release migration. Preserves existing supplier values, row versions and receipts; advances readiness and backup markers. Verify fresh creation, PR-base upgrade, legacy replay and restricted-writer validation. Deploy the matching API/client. | Always blocked; preserve handles and request evidence through forward correction or guarded recovery. |
| `20260921051843_AddAccountingFoundation` | `MakeSupplierProfilesCustom` | Adds tenant accounting configuration, general accounts, revisions, receipts and two explicitly assigned accounting roles. Restricted commands validate current actor authority; runtime Identity role/claim writes are denied. Retains existing data without granting accounting access or creating balances. Advances readiness and backup markers; stop older writers and deploy the matching application. Verify fresh creation and upgrade from the predecessor. | Always blocked (50020); preserve configuration and authorization history through forward correction or guarded recovery. |
| `20260923010000_AddAtomicJournal` | `AddAccountingFoundation` | Adds immutable journal/source/receipt records, first-posting policy freeze, exact SQL posting validation and used-account archive protection. No runtime posting grant or production source adapter. Preserves BK-01 data and replay bytes; advances readiness and backup markers. Verify fresh creation and upgrade from the merged BK-01 schema. Stop older writers and deploy the matching application. | Always blocked (50020); preserve financial history through forward correction or guarded recovery. |
| `20260925044758_AddAccountingPeriodControls` | `AddAtomicJournal` | Adds tenant-scoped monthly periods, immutable closures/receipts and atomic correction groups/receipts in one BK-03 migration. Backfills existing posted months as open; preserves all BK-02 journals, source snapshots and receipt bytes. Adds closed-month posting guards and restricted close/correction kernels without production write adapters. Advances readiness and backup markers. Stop incompatible writers; verify fresh creation and upgrade with retained BK-02 history before deploying the matching application. | Always blocked (50020); preserve closure and correction history through forward correction or guarded recovery. |
| `20260926210900_AddPurchaseRecognition` | `AddAccountingPeriodControls` | Adds immutable classified purchase recognition, matching and group correction evidence; restricted commands, receipt-accrual mapping and PO supplier-history guards. Preserves BK-03 financial and replay bytes. Advances readiness; only matching binaries are compatible. No production source adapter, financial source permission or public write route. Verify fresh creation and merged BK-03 upgrade using disposable databases. | Always blocked (50020); preserve financial evidence through forward correction or guarded recovery. |
| `20260928034802_AddSupplierBills` | `AddPurchaseRecognition` | Adds immutable supplier bill revisions/reviews, source posting links, receipts and bounded queries. Installs an internal bill-to-recognition adapter and generic correction ownership guards. Preserves prior financial and replay bytes. Advances readiness; use matching binaries. No runtime bill mutation grants, production permission assignments or public write routes. Verify fresh creation and upgrade from merged BK-04. | Always blocked (50020); preserve source and financial evidence through forward migration or guarded recovery. |
| `20260928071548_AddSupplierOpenItems` | `AddSupplierBills` | Adds immutable supplier open items, payments, applications/inverses, control attribution, groups and receipts; derives supported stored invoice evidence and installs internal commands and authorized report reads. Preserves source/journal/replay history; unknown evidence stays unresolved. Advances readiness and backup schema; use matching binaries. Fresh creation and actual merged BK-05 upgrade/guarded SQL recovery have targeted disposable evidence; release gates remain separate. | Always blocked (50020); forward repair or guarded recovery, preserving financial history. |
| `20261001000000_AddSharedGemReference` | `AddSupplierOpenItems` | Adds shared gem entries, aliases and field/locality provenance without tenant ownership or seed content. Preserves existing tenant records and versions; grants web SELECT only on the four catalog tables and denies runtime mutations. Advances readiness and backup schema; requires matching binaries. Fresh creation and merged-base upgrade are covered by disposable SQL tests. | Always blocked (50020); forward repair or protected recovery preserves reference identities and provenance. |
| `20261001072507_AddServiceAdminIdentity` | `AddSharedGemReference` | Adds tenant-free service-admin accounts and hashed sessions, narrow web authentication procedures and operator-only maintenance. Extends mandatory transactional sanitation to delete admin sessions and advance every account security version before clearing the restore marker. Fails on unsupported sanitation predecessors; preserves tenant and financial evidence. Advances readiness and backup schema. Verify fresh creation, merged-base upgrade, restricted restore after later disablement/revocation, cached/fresh cookie and raw-token denial. | Always blocked (50020); retain accounts and session authority through forward correction or guarded recovery. |
| `20261002192523_InstallGemReferencePilot` | `AddServiceAdminIdentity` | Installs the frozen reviewed four-gem pilot and nineteen citations for missing stable IDs; retains published/retired entries and their complete provenance. Identity conflicts reject the transaction. Advances readiness and backup schema. Verify exact fresh installation, rerun after publication, merged-base upgrade, conflict atomicity, and source-to-seed equality. | Always blocked (50020); use forward correction or protected recovery to preserve published content. |
| `20261002192901_AddGemReferenceCuration` | `InstallGemReferencePilot` | Adds service-admin drafts, atomic shared publication/retirement, durable actor-bound outcomes, and publication audit through six restricted web commands. Preserves published provenance and tenant/admin identity; direct table writes remain denied. Advances readiness and backup schema; verify fresh creation, merged GEM-03/GEM-04 upgrade, rejected batches, lock-time revocation, exact retries, and transactional audit failure. | Always blocked (50020); forward correction or guarded recovery preserves drafts and publication evidence. |
| `20261003214043_AddTenantGemReference` | `AddGemReferenceCuration` | Adds tenant entries and sparse overrides with RLS, two explicit web reads and three restricted write commands. Preserves shared publication, provenance, identities and tenant data; retained empty override rows prevent token ABA. Advances readiness and backup schema. Verify fresh creation, merged GEM-05 upgrade, actual-principal scope/authority, composite versions and reset retention. | Always blocked (50020); forward correction or guarded recovery preserves tenant additions and choices. |
| `20261008010000_AddFinancialEvidenceRetention` | `AddTenantGemReference` | Adds authentic immutable financial evidence, frozen retention, independent/effective holds, authorized disposal and recovery inventory/manifest guards. Legacy posted links are indefinite; malformed typed ownership aborts upgrade. Preserves hashes, removed/purged state and all prior migrations. Advances readiness and backup schema; use matching binaries. | Always blocked (50020); forward correction or guarded paired recovery preserves financial evidence. |

Product behavior, user-visible concurrency/retry rules and the shipped feature inventory belong in
[collection documentation](../collection.md). Provider retry/backoff behavior belongs in
[identity delivery and worker operations](blob-and-service-providers.md#identity-delivery-and-worker).
The [migration source](../../src/Workbench.Server/Persistence/Migrations) is authoritative for SQL.


The current required migration is `20261008010000_AddFinancialEvidenceRetention`.

GEM-06 introduces one coherent migration after merged GEM-05. It creates tenant-owned entries and
sparse JSON choices, installs RLS and restricted commands, and advances readiness. Shared catalog,
curation and existing tenant data remain intact. Native SQL backups include tenant reference data;
restore sanitation remains responsible for invalidating restored authentication. Full reset retains
an empty versioned override row. Down rejects destructive loss with 50020; use forward correction
or protected restore and matching binaries. Fresh creation and upgrade from `AddGemReferenceCuration`
must verify model/history, tenant scope, actual-principal command admission and the rollback guard.

GEM-05 adds one coherent migration after the merged GEM-04 pilot baseline. It creates private
command-owned drafts, immutable publication outcomes, and append-oriented publication audit;
installs six restricted web commands; and advances readiness. Existing shared content,
rowversions, provenance, tenant records, and service-admin identities are retained. Native
SQL backups include curation storage, while existing restore sanitation continues to invalidate
all restored admin sessions. Down rejects destructive loss with error 50020. Use forward
correction or protected restore; an older application's readiness marker rejects this schema.
Verify fresh creation, upgrade from `InstallGemReferencePilot`, direct-principal allow/deny
checks, exact outcome replay, and transactional catalog/audit rollback against current source.

`MakeSupplierProfilesCustom` directly follows `HardenPurchaseOrderDocumentAuthority`.
It adds custom supplier reference pairs in one migration, preserving existing supplier data,
versions and receipts. The unmerged intermediate supplier migration was consolidated at the
owner's request; the matching local preview was explicitly reconciled after verifying its final
schema and retained data. Other development histories require a verified transition before
refresh. Shipped migrations remain unchanged. Down is blocked.
Stop all application writers before migration and deploy the matching beta API/client together
only after the entire pending migration set completes. The final schema has one purchasing writer
with PO-05 financial and confirmed supplier correction validation, no historical replay procedure,
and no V2/V3/V4 write procedures. Stored content schemas 1/2/3/4 and immutable receipts remain intact.

The preparatory migration deliberately sorts before the immutable PO-05 migrations. A retained
beta database already applied consolidation/removal and therefore lacks their V4 SQL prerequisite;
EF applies the newly pending preparatory migration without changing any existing history rows.
It reconstructs only the internal procedure prerequisites needed during this offline transition.
Fresh and main-line databases already have those prerequisites and require no preparation. The
final forward migration restores financial validation after the earlier beta consolidation and
removes the temporary procedures. Do not run an intermediate application or stop the deployment
at a preparatory schema. Readiness advances only to the completed integration boundary.
Development preview inspection recognizes these exact retained histories and their ordered
forward migration stages; it continues to reject unknown migrations or gaps in earlier history.

All four earlier PO-05 and beta migrations remain unchanged because they are base history or were
applied to retained previews. The two transition migrations serve different required ordering
boundaries and cannot be consolidated without either rewriting history or breaking retained-beta
upgrades. Verify fresh creation, main-line PO-05 upgrade, and an actual beta-only applied history,
including preserved drafts/receipts, current financial saves, retired procedure absence, and
blocked downgrade. See [API lifecycle](../api-lifecycle.md).

The PO-03 changes were consolidated into `AddSupplierBasedDraftPricing`, retaining the final migration ID and final model. It installs V3 compatibility commands before V4 commands and keeps the destructive-rollback guard. Retained previews that already applied both earlier migrations keep their existing history and data unchanged; the final ID is already applied, so no schema work is repeated. A preview that applied only the removed structured-line migration is not a supported upgrade baseline and needs a separately planned transition; never reset its history automatically.


### Retained PO-04 development preview reconciliation

PO-04's three development migrations (`20260918030000`, `20260918040000`, and
`20260918050000`) were consolidated before release with explicit owner approval.
They are not part of the release lineage. An already-applied retained preview must
not run the new additive migration over its existing tables or silently rewrite
its history. Back up with checksums, restore to a separate database, verify every
retained table, install and verify the final procedure definitions on that clone,
and reconcile only the clone's three development history entries to the final
PO-04 migration. Stop writers and recheck retained data before switching to the
verified clone. Keep the original database and backup for guarded recovery.

This exception applies only to the approved isolated development preview. It is
not an automatic upgrade path for shared or production databases. The final
schema retains all constraints, RLS predicates, restricted grants and rollback
guards. Backup manifest validation continues to recognize the retired development
markers so their retained backups remain usable for guarded recovery.

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
