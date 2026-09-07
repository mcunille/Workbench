# Online Azure backups and manual file reconciliation

This workflow implements the [accepted SQL-authoritative policy](../specs/2026-09-07-online-backup-and-manual-recovery.md).
Capture leaves production web, uploads and workers running. Recovery commands operate only on an
isolated restored database/store with writers stopped. Existing self-hosted/offline snapshot commands
remain supported and retain their confirmations.

## Deploy and verify backup collection

Use a release containing `AddOnlineRecovery` and the backup command. Compile `infra/azure/backup.bicep`
and run `infra/azure/test-online-backup.ps1 -TemplateFile <compiled-template>`. The template references
existing production resources and creates a separate GRS account, one private endpoint/DNS association,
a managed-identity Container Apps job, scoped roles and two alerts. It does not redeploy web/worker/SQL.
Create a reviewed parameter file containing the parameter values declared at the top of that template.
Use the exact deployed image digest and schema, and retain that image for recovery.

Before applying it, obtain approval for the resources, permissions and incremental cost. Account for
the endpoint's hourly charge, daily full blob-version copies, checks of retained copies, replication
traffic, execution and monitoring. This first version intentionally does not deduplicate across runs:
each catalog owns its copies, which makes independent retention and deletion safe. Cost grows with
retained source versions and capture frequency. Measure bytes and execution time; do not assume the
subscription budget stops spending.

On the first deployment set `enableSchedule=false` and `initializeProtection=true`. Set
`initializeProtection=false` on every subsequent deployment so the template does not attempt to
update a locked policy. The job starts Manual. It receives source Blob Data Reader,
control-plane Reader for SQL retention and the two storage accounts, and a custom destination blob
read/write role without deletion. No SQL login, mail identity, production write access, secret-read
permission, or workload-control permission is provisioned for the collector. Web and worker identities
receive no destination permission. The destination writer role is **not** inherently append-only;
locked storage immutability enforces non-replacement.

The template creates a 37-day container immutability policy. Locking that policy is a separate,
explicitly approved Azure operation; it cannot subsequently be shortened or removed while protected
data remains. Review `az storage container immutability-policy show` and then use the documented
`lock` operation with its current ETag. Do not turn on protected append writes. Capture refuses an
unlocked/insufficient policy. The lifecycle rule removes run-scoped copies only after 45 days, later
than the longest supported catalog lifetime of 37 days plus the bounded capture duration.

Run the job manually from the private environment after role propagation. Its command is:

```text
dotnet /opt/workbench/database/Workbench.Database.dll backup capture
```

The Bicep template supplies `Backup__*` environment settings, including installation, source/destination
bindings, original SQL resource ID, release digest/schema and recovery key version identifiers.
It uses the job's system identity. There is no connection string or secret in those settings.
The collector reads actual SQL retention and source versioning/soft-delete configuration. Source
retention must cover SQL retention plus two days. Catalog lifetime is that same interval (maximum 37
days). The job fails on missing protection or inconsistent bindings rather than silently reducing
coverage. Key identifiers refer to recovery material already exported and verified independently;
the collector cannot verify Bitwarden or export secrets. Keep those dependencies current on rotation.

A valid run checks retained unexpired catalog objects, inventories exact published source versions,
copies them create-only, reads back hashes and publishes a protected catalog. Each version copy is
bounded to 25 MiB, matching the application's current maximum. Larger future content requires an
explicit collector update. Staged/unrecognized objects are counted separately. Enumeration is a
coverage interval, not a snapshot of SQL. An upload occurring later is eligible for the next run.
An enumerated version that disappears yields `Incomplete`; access errors, timeouts and integrity
failures yield failure. Neither advances successful freshness.

Check all of:

- execution Succeeded and `OnlineBackupStatus` with `Outcome=IntegrityChecked`;
- the protected catalog and verified sample bytes through the private endpoint;
- the collector cannot delete production/destination data or overwrite a protected backup blob;
- current web readiness, a real write and scheduled worker execution remain operational during capture;
- the backup failure and 24-hour missing-success alerts deliver to the operations action group.

Then approve activation and deploy the same template with `enableSchedule=true`. The default schedule
is daily at 03:00 UTC; configure it explicitly for the installation. No restore is scheduled. Monitor
source protection/retention drift, catalog/object failures and stale collection. An execution failure
metric catches failures that cannot emit a final log. The log rule intentionally retains failure
evidence for its 24-hour window; inspect later successful executions before closing an incident.

## Manual recovery

1. Select the SQL recovery point and available catalog interval. Restore into a new isolated database
   using the [SQL recovery guard and sanitation procedure](database-backup-restore.md). Keep target
   web, workers, email and public routes disabled. Apply forward migrations using the release's
   migrator. Existing sessions must be invalidated by sanitation, independently of file-loss acceptance.
2. Provision a **different** recovered blob store; never reuse production or the backup archive.
   Configure the existing narrow SQL storage-maintenance identity and separate read permission for
   the archive plus write/delete permission only for the recovered store. Use an operator-controlled
   host with private network access. Archive materialization uses its system managed identity.
3. Download chosen catalog JSON files from the private backup container to a protected local directory.
   Use the Azure SDK/CLI with Entra login on that host; no SAS or storage keys are needed. A catalog is
   not a SQL backup. Do not edit its bindings or hashes. Expired catalogs are rejected by this command.
4. Create the configuration below outside Git, using the installation's original UUID. `Storage` is
   the recovered target, not the original source. Provide the original SQL resource ID from the backup
   catalog; it will differ from the isolated SQL connection. A filesystem target can use the existing
   `FileSystem` provider settings instead. `Recovery:Source:Storage` must reproduce the original configuration exactly, including UUID spelling; its alias is verified against SQL. The destination must be a separate container or a nonoverlapping filesystem root. Original storage is never read or written by this validation.

```json
{
  "Storage": {
    "Provider": "Azure",
    "ContainerUri": "https://RECOVERED.blob.core.windows.net/workbench",
    "InstallationId": "ORIGINAL-INSTALLATION-UUID"
  },
  "Recovery": {
    "Source": { "Storage": {
      "Provider": "Azure",
      "ContainerUri": "https://ORIGINAL.blob.core.windows.net/workbench",
      "InstallationId": "EXACT-ORIGINAL-CONFIGURATION-UUID-TEXT"
    } },
    "ArchiveContainer": "https://BACKUP.blob.core.windows.net/backups",
    "OriginalSqlResourceId": "/subscriptions/SUB/resourceGroups/RG/providers/Microsoft.Sql/servers/ORIGINAL/databases/Workbench"
  }
}
```

5. Run the plan command. It materializes matching backup versions by SQL tenant/revision identity,
   source binding, length and digest, then writes a new report. Omit `--catalog-directory` only if the
   recovered bytes have already been materialized and verified through another manual procedure.

```powershell
dotnet Workbench.Database.dll storage recovery-plan `
  --connection-file <isolated-maintenance-connection-file> --expected-database Workbench `
  --config-file <isolated-recovery-config.json> --offline-confirmation "OFFLINE Workbench" `
  --catalog-directory <protected-catalog-directory> --report-file <new-report.json>
```

Pending SQL revisions block the new workflow. Resolve them under the existing offline reconciliation
procedure before continuing; do not guess whether an interrupted upload committed. The new commands
require a sanitized restore generation and the pending blob-recovery gate. An empty restore with no
pending blob gate continues through the existing empty-manifest verification path.

6. Review `Missing` and `Orphans` in the report. It binds the installation, server/database, restore
   generation, target alias and complete SQL revision inventory. Missing/corrupt files retain their
   SQL records. Every SQL revision identity protects its object from orphan deletion, including
   retained/deletion-grace history. Unknown files never create database records.
7. Explicitly accept the report digest printed by planning. This applies manual cleanup **only to the
   recovered store**, persists the unavailable-file dispositions, relocates available SQL revisions
   to that target and completes its storage gate. It does not start services or cut over traffic.

```powershell
dotnet Workbench.Database.dll storage recovery-apply `
  --connection-file <isolated-maintenance-connection-file> --expected-database Workbench `
  --config-file <isolated-recovery-config.json> --offline-confirmation "OFFLINE Workbench" `
  --report-file <reviewed-report.json> --accept-report-sha256 <printed-SHA256>
```

Changed SQL inventory, target bytes or report contents require a new report and explicit acceptance.
Provider failures block completion and must not be reclassified as missing content. Interrupted
cleanup leaves recovery blocked; prepare a fresh report if its orphan set changed. Retrying an already
committed report returns its completed state without performing further changes.

8. Validate isolated readiness, authentication, a recovered file and an unavailable-file notice.
   Authorized users see a persistent explanation on affected photographs and may replace/remove them;
   downloads return HTTP 410 with code `file_unavailable_after_recovery`. Tenant authorization still
   applies before that response. Keep the protected report as the cross-tenant operator record.
   Validate restored-session rejection separately. Obtain approval before production cutover.

For later byte repair, use another guarded isolated recovery/reconciliation with an exact matching
backup version, or let the user create a replacement through the normal immutable revision workflow.
Do not edit published digests or delete SQL records to bypass the recovery gate. Older runtime images
do not understand missing-file dispositions; rolling back requires a separately validated compatible
release or isolated recovery, not dropping this migration.

## Evidence limits

Local tests and compiled templates are not hosted backup evidence. Record first manual collection,
role-denial checks, alert delivery, and manual recovery results for the actual deployed image.
Distinguish integrity-checked collection from a manually restore-verified outcome with or without
missing files. Geographic replication remains asynchronous; this workflow accepts and reports
inconsistency rather than claiming an exact cross-region pair or zero data loss.
