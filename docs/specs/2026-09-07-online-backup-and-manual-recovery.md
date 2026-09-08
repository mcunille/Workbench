# Online backup and SQL-authoritative manual recovery

**Status:** Implemented locally; hosted verification and activation pending.

## Decision and scope

Production backups must not pause web requests, uploads, or worker execution. SQL owns application
records and the meaning of blob references. Independently captured SQL and blob backups may disagree;
manual recovery reconciles those differences instead of requiring an exact paired checkpoint.

The operator explicitly accepts that some files may be unavailable after recovery. Missing files
must remain visible as unavailable, with a protected discrepancy report and tenant-scoped notice.
That acceptance does not permit silent data loss, fabricated SQL records, bypassing tenant isolation,
or treating an inaccessible provider as an empty store.

Automate backup collection, integrity checks, retention and alerts. Restore, sanitation, discrepancy
acceptance, cleanup of the recovered store, and production cutover remain operator-initiated. Do not
schedule restore drills or failover. New Azure resources and deployed changes require separate
approval, with their permissions and estimated cost presented first.

This specification defines the new Azure workflow. Existing offline commands and the tested
self-hosted backup path retain their current requirements until an explicit replacement is built.
Do not remove an offline confirmation from an existing command to simulate online support.

## Current implementation and the gap

- `StorageMaintenanceCommand` requires offline confirmation for every action. Its SQL export is
  paged without one consistent read transaction, and therefore is not an online SQL snapshot.
- `AzureBlobStore.ListAsync` lists current objects, not retained Azure versions. It also combines
  staged and published objects under revision identities. It is not a backup inventory API.
- Reconciliation reports `MissingOrCorrupt` and `Unreferenced` but neither removes objects nor
  records an accepted recovery discrepancy in SQL. Its broad `IOException` classification must
  not be reused to authorize destructive cleanup or mark an access failure as missing content.
- Recovery verification requires an exact manifest match and verified bytes for all entries before
  clearing the storage recovery gate. No accepted-loss state or tenant-facing recovery notice exists.

Those are implementation gaps, not evidence that the online procedure already works.

## Online backup collection

Retain Azure SQL managed point-in-time backups. Blob collection runs independently, using a separate
backup identity and destination. It must not acquire a production write freeze, deactivate a web
revision, disable a worker schedule, change SQL principals, or perform a restore.

The collector inventories committed, published blob versions in the installation namespace, including
retained versions of subsequently deleted blobs. Copy by immutable version identity; never resolve a
listed version to whatever happens to be current at download time. Capture source version/ETag,
installation, tenant/revision identity, byte length, SHA256, destination object identity, and capture
start/end times. Unsupported names and staged objects are reported separately and cannot create SQL
records during recovery. A non-atomic listing is explicitly a coverage interval, not a single instant.

Write copied objects create-only. Read back and checksum the destination before publishing a completed
catalog. Retries may reuse verified objects but cannot overwrite conflicting content. A concurrent
upload after inventory can appear in the next collection; a concurrent deletion should be served from
the enumerated retained version. If that version is unavailable, record the coverage gap and alert.
Do not advance the last fully integrity-checked collection on partial results or provider errors.

The protected catalog includes format version, backup ID, source bindings, SQL resource/database
identity, observed PITR retention, image digest, schema version, installation UUID, object inventory,
coverage gaps, and recovery-key version dependencies. Catalog timestamps are not proof of SQL
restorability. Record SQL coverage and blob coverage separately; do not call them an exact pair.

Start with daily collection and a configurable UTC schedule. Retention must cover the SQL recovery
window and a collection/retry margin. Retain an older object while any retained catalog needs it;
do not base expiration only on its original upload time. Extending SQL retention requires extending
blob/catalog/key retention together. Missing the daily collection alerts at 26 hours (24-hour cadence plus a two-hour runtime and log-ingestion margin); elapsed time
alone does not mark an incomplete collection successful.

Azure shape: separate Container Apps capture and expiration jobs, one separate private backup storage
account with geographic redundancy, its private endpoint/DNS association, and monitoring through the
existing operations action group. Use the existing environment where feasible. Provision no permanent
VM and no scheduled SQL restore/copy database. Resource sizing, retention settings, access controls,
and incremental cost must be verified before deployment approval; the $100 budget is not a hard cap.

## Authority and backup isolation

Production web and worker identities have no access to the backup destination. The collector can
read the scoped source versions and create/read backup data, but cannot delete production blobs,
change SQL data, grant roles, restore databases, or send mail. Give it only the SQL control-plane
metadata reads necessary to record retention; do not give it SQL setup authority.

Retention cleanup is separately constrained to expired backup data and cannot modify retained
catalogs or production content. Select enforceable destination permissions/protection during
implementation; do not describe a broad data-contributor grant as append-only authority. Expiration uses a distinct archive-only read/delete identity and conditional blob deletion after the 45-day safety margin. The archive WORM policy independently prevents premature deletion. Lifecycle deletion is not supported for this immutable container; expiration remains disabled until hosted aged-object and preservation checks pass.

Keep cryptographic recovery material independently recoverable using the existing protected export
and Bitwarden procedure. A catalog stores version identifiers, not secret values. Key rotation must
preserve every dependency needed by retained recovery points. General telemetry contains counts,
backup IDs, timestamps and bounded failure categories; tenant/object details stay in protected reports.

## Manual recovery and reconciliation

1. Restore SQL into an isolated target under the existing restore guard. Sanitize sessions and other
   authentication artifacts. Keep all target writers, workers, delivery and public routes disabled.
   Online backup availability does not imply that a recovery target can accept writes during repair.
2. Use the restored SQL state to enumerate references. Include retained revisions, pending operations,
   replacements and deletion-grace records when deciding whether an object is unreferenced; a missing
   current attachment pointer alone is not sufficient evidence for deletion.
3. For each SQL revision requiring content, search retained backup versions and verify its expected
   length and digest. Match installation, tenant, revision and provider binding. Never substitute the
   newest blob merely because it exists, and never reconstruct application records from blob names.
4. Materialize verified content into a dedicated recovered store. Record provider relocation through
   the authorized maintenance path. Do not point recovery at writable production storage.
5. Classify each discrepancy as verified missing content, corrupt content, unreferenced content,
   unresolved pending operation, or indeterminate provider failure. Authorization, DNS, timeout and
   incomplete enumeration failures block completion; they do not prove absence.
6. Present a report tied to the restored database, recovery generation, installation and store binding.
   The operator explicitly accepts any unrecoverable missing/corrupt files. Recheck before applying
   the report; stale reports and changed references must fail without deleting data.
7. Remove confirmed unreferenced objects from the recovered application store. Preserve the protected
   report and retained backup objects under their own retention policy. Reconciliation must never
   delete from the original production store or the backup archive using this authority.
8. Persist missing-file dispositions and notices, verify all remaining references and cleanup results,
   then complete the storage recovery gate with the recorded outcome. Public cutover remains manual.

If the recovered store is populated solely from SQL references, unreferenced archive objects simply
remain outside it. No copy-then-delete cycle is required. Their absence from the selected SQL restore
does not authorize deleting them from backups that support other restore points.

## Accepted missing files and user experience

Preserve the attachment/item and authoritative revision metadata. Persist a separate, auditable
availability disposition linked to the recovery generation; do not reuse `Purged` or delete the SQL
record to make verification pass. Record the report identity, revision, reason and acceptance time.

After recovery, authorized users see an explicit unavailable-file placeholder on the affected item.
Provide a tenant-scoped recovery notice with the affected files and remediation instructions. Access
to details follows existing item/attachment permissions; cross-tenant reports remain operator-only.
The notice must be durable without relying on email delivery. Do not send messages during isolation.

Downloads of known unavailable content return a stable application error rather than corrupt bytes,
a misleading success, or raw provider exceptions. Define and test the API/UI representation before
releasing it. Missing-file acceptance does not disable authentication sanitation, SQL tenant controls,
provider connectivity checks, or integrity checking for files declared available.

Repair can later recover an exact matching version and clear its disposition after verification.
A user replacement follows the normal immutable revision workflow; it does not rewrite historical
checksums. Unresolved missing files stay visible until repaired or deliberately removed by the user.

## Outcomes, failure handling and compatibility

Distinguish `Captured`, `IntegrityChecked`, `Incomplete`, `RestoreVerified`, and
`RestoreVerifiedWithMissingFiles` in the operational record. Only a manual restore test can establish
either restore-verified outcome. A collector process exiting zero is insufficient backup evidence.

Alerts cover failed/incomplete captures, stale complete captures, retention/protection drift, and
unexpected loss of referenced catalog objects. Missing data during recovery produces an operator
report and tenant notices. No alert automatically initiates recovery or deletes data.

Schema changes use a forward migration: do not rewrite migrations applied to production. New runtime
versions must understand accepted missing-file dispositions before the recovery gate can be cleared
with them. An older runtime cannot safely roll back over that state without an explicitly verified
compatibility path. Preserve the old exact-pair verification workflow for backups created under it.

Azure geo-replication is asynchronous and geo-restore selects the latest replicated SQL backup. This
design reconciles that SQL state with available blob versions; it does not promise an exact geographic
pair, zero data loss, a proven RPO/RTO, or recovery from loss of every administrative identity.

## Alternatives

- Reject a daily write pause: it conflicts with the approved availability requirement.
- Reject deleting live unreferenced blobs during online capture: pending uploads and non-atomic
  inventories can create false orphan classifications.
- Reject exact-pair-only recovery as the sole workflow: the operator accepts explicitly reported
  file loss in exchange for online backup collection.
- Reject silently skipping missing bytes: users must know which content was lost.
- Reject automatic restore/failover: recovery is deliberately manual.

## Acceptance evidence required for implementation

- Concurrent create/replace/delete operations continue during collection; no web/worker pause or SQL
  mutation is issued. Capture handles version races, interruption, retry and digest mismatches.
- A SQL restore containing missing and corrupt files cannot clear recovery guards until every
  discrepancy is classified and accepted; access failures cannot be accepted as file loss.
- A blob-only object never creates a SQL row. Cleanup deletes only a verified orphan in the isolated
  recovered store, preserving pending/retained references, production data and archived versions.
- Restore-generation, installation, tenant, binding and stale-report mismatches fail closed. Repeated
  completion/cleanup is safe and audited. Tests include concurrent-change rejection.
- Authorized users can see missing-file notices and placeholders after recovery; unauthorized users
  cannot see their names or download content. Restored pre-recovery sessions remain invalid.
- Retention cannot delete bytes still referenced by a retained catalog. Incomplete captures do not
  refresh freshness. Real hosted alert delivery is tested separately from local unit checks.
- Use focused failing tests first, SQL integration recovery tests, storage emulator tests, API/UI
  checks, meaningful mutation checks, and repository verification gates for the implementation.
- Deploy only after explicit approval and validate the built release. Documentation/design acceptance
  alone does not satisfy any of these runtime or hosted gates.

## References

- [Current storage operations](../operations/blob-and-service-providers.md)
- [Current SQL restore and sanitation](../operations/database-backup-restore.md)
- [Current Azure operations](../operations/azure-deployment.md)
- [Azure SQL automatic backups](https://learn.microsoft.com/en-us/azure/azure-sql/database/automated-backups-overview?view=azuresql)
- [Azure SQL restore capabilities](https://learn.microsoft.com/en-us/azure/azure-sql/database/recovery-using-backups?view=azuresql)
