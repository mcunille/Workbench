# Blob backup and recovery

[Return to the main entry](blob-and-service-providers.md).

## Offline reconciliation, paired backup, and restore

This section defines strict paired recovery. For online Azure backups, including native-restored
bytes, use [manual SQL-authoritative recovery](online-backup-recovery.md#manual-recovery) when exact
pairing is unavailable. Its isolated target, sanitation, report review and digest acceptance are
required before missing-file dispositions can complete the storage gate; a failed `storage verify`
does not authorize that outcome automatically.

Use the separate `workbench_storage_maintenance` identity from the [database-principal matrix](database-principals.md) for the narrow manifest, relocation,
recovery-verification, and deletion-replay procedures. It must not be an ordinary web or worker user.
Keep connection files, configuration, manifests, reports, SQL backups, and blob snapshots in an
access-controlled encrypted location outside Git. Unix output files use mode 0600; Windows output
directories must have an appropriate inherited ACL.

Stop and drain **every** web replica and worker before maintenance. The required confirmation is an
operator assertion; the command cannot discover external replicas. Keep services stopped on any error.
Use a JSON configuration file shaped as follows, substituting installation-specific values:

```json
{
  "Storage": {
    "Provider": "FileSystem", "Root": "/var/lib/workbench/blobs",
    "DurableVolume": true, "InstallationId": "<installation UUID>"
  },
  "Target": {
    "Storage": {
      "Provider": "FileSystem", "Root": "/backup/workbench/blobs",
      "DurableVolume": true, "InstallationId": "<same installation UUID>"
    }
  }
}
```

Either side can instead use the [Azure provider settings](blob-and-service-providers.md#deployment-configuration).
Environment variables override JSON;
use a dedicated maintenance process environment and verify selected bindings before proceeding.

```powershell
dotnet Workbench.Database.dll storage reconcile --connection-file <maintenance-connection-file> `
  --expected-database <name> --offline-confirmation "OFFLINE <name>" `
  --config-file <configuration-file> --output-file <new-report-file>
```

Reconciliation compares all available SQL revisions, including retained replacements and deletions
within their grace period, with checksummed provider content. Missing/corrupt bytes and unreferenced
published or staged objects produce a protected report and nonzero exit. Unknown objects are report-only.
Do not delete them based on name or age alone: preserve a report/snapshot, investigate pending SQL
operations, wait at least the seven-day grace, and recheck references offline before separately authorized
cleanup. A provider outage must not be interpreted as evidence that an object is unreferenced.

For backup, first take a SQL `COPY_ONLY` backup with checksum using the
[database runbook](database-backup-restore.md), while writers remain stopped. Then:

```powershell
dotnet Workbench.Database.dll storage snapshot --connection-file <maintenance-connection-file> `
  --expected-database <name> --offline-confirmation "OFFLINE <name>" `
  --config-file <configuration-file> --output-file <new-manifest-file>
```

The command copies each immutable revision to `Target`, verifies every length/digest, and writes a
manifest containing backup UUID, installation UUID, database name, schema version, provider alias,
tenant, revision, length, and digest. Pair that exact manifest with the SQL backup and provider snapshot
in one backup catalog entry; timestamps alone do not pair independently captured stores. `storage manifest`
verifies and exports the same identity list without copying when an operator-managed snapshot exists.
Output files must be new; copying is create-only and may resume after interruption. Incomplete backups
must not enter the successful-backup catalog. Resume service only after all checks pass.

Restore SQL using the database runbook and run mandatory `restore sanitize`, which cancels identity
outbox messages, invalidates sessions/keys, and resets outstanding deletion leases. Restore the snapshot
to the original provider binding (same mount/container/installation), preserving exact `.b` object names.
For Azure, select the precise snapshot/version represented by the manifest. Do not use an approximate
timestamp or silently switch the configured location. Then run:

```powershell
dotnet Workbench.Database.dll storage verify --connection-file <maintenance-connection-file> `
  --expected-database <name> --offline-confirmation "OFFLINE <name>" `
  --config-file <configuration-file> --manifest-file <paired-manifest-file>
```

Verification compares the full manifest with SQL and streams every referenced blob through SHA-256.
Only success of this strict verification clears blob recovery pending through this command. Missing bytes, changed metadata, a wrong installation/database,
or incompatible schema keep recovery blocked. Rebuild protected keys, check tenant isolation and login,
and inspect readiness before the human-operated cutover. Restore drills use disposable SQL databases;
never overwrite a production database as part of verification.

## Provider migration and rollback

With all writers/workers stopped, save a paired backup and configure `Storage` as the current provider
and `Target` as the intended provider. Run `storage migrate` with the same connection, database,
offline-confirmation and config-file arguments. Migration refuses unresolved pending revisions; reconcile
them against the original store before cutover. A privileged offline repair may mark a pending operation
failed only after establishing that it has no retained content and any abandoned bytes have been handled
under the retention/cleanup procedure. Failed and purged revisions retain provenance; financially
protected revisions still require surviving bytes to be verified and copied unless legitimately
disposed. Other failed/purged revisions require no further provider deletion. Do not mark an
ambiguous publication failed merely to bypass this gate.
The copy checks destination readiness, including Azure container privacy, before writing bytes.
It verifies the source, copies immutable bytes with
create-only semantics, verifies all destinations, and transactionally changes SQL aliases with digest
and previous-alias checks. A failed copy leaves SQL references unchanged. A failed/ambiguous SQL commit
requires reading the authoritative manifest before selecting a configuration. Preserve both stores.

After success, deploy the target binding to all processes, reconcile, and run readiness/tenant workflow
checks before resuming. Keep the source for at least seven days and through a successful paired backup
and restore drill. To roll back, stop all writers again and run the same migration with source/target
reversed; new revisions created after cutover must also be copied back. Never roll back by changing
configuration alone or restoring only SQL. Permanent source cleanup requires separate operator approval.
