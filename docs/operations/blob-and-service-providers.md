# Blob storage and operational services

The web application, database tool, and explicit worker share the same release. Blob APIs are internal
application services for generated content; there is no general upload endpoint. Item photographs
use the bounded workflow below. Other user uploads require their own type allowlist and malware
policy before publication.

## Item photograph ingestion

Saved items accept one optional photograph. The browser allows JPEG, PNG, and WebP sources up to
20 MiB and 40 megapixels (16,384 pixels per axis), prepares orientation/color and resizes locally,
then shows a preview. The original never uploads as a fallback. Browsers that cannot prepare the
file show an actionable error. HEIC/HEIF, RAW, animated, and multi-picture images require conversion.

The API independently limits the prepared file to 4 MiB and 2,048 pixels per axis. Multipart
overhead is bounded to an additional 64 KiB, including requests without Content-Length. The
server reconstructs a WebP detail image and a thumbnail within 384 pixels; the combined encoded
output is bounded to 10 MiB. It removes EXIF/GPS, input ICC, XMP, comments, and embedded previews
after orientation/sRGB conversion. No original bytes or client filename are persisted.

Magick.NET Q8 is pinned in the release; bundled licenses ship in
`third-party-notices/Magick.NET.txt`. Only JPEG/PNG/WebP coders/modules are enabled, and delegates,
external paths, filters, and disk pixel-cache spill are disabled. One processor job per web
process uses at most two native threads. The 256 MiB native pixel-cache allocation budget is
not a hard process-RSS limit; codec allocations and the .NET process add memory. Processing has
cooperative ten-second progress cancellation and elapsed-time checks, while upload/storage uses
a two-minute cancellation deadline. Native code cannot be forcibly interrupted in-process.
These are bounded raster-reconstruction controls, not antivirus certification or process isolation.

Replacement first creates durable pending revisions and publishes immutable provider bytes. A
single SQL transaction then makes both images current and retires the previous pair. Removal
clears the item pointer and retires both attachments in the same transaction. The saved item is
never removed. Tenant-scoped request IDs, input digests, and item versions support exact retry;
competing commands return 409 instead of overwriting another session's result.

Unconfirmed outcomes return 503 and the client retains the exact operation for retry. The
server serializes identical retries across replicas and verifies staged/published bytes against
the regenerated output digest before finalization. A completely published concurrency loser is
retired under the normal grace period. Partial writes, a stale pending retry after a later item
change, or a different encoder output after a release change may require offline reconciliation.
Provider failures never authorize deletion of ambiguous bytes. Preserve pending revisions and
use the offline procedure below; do not mark them failed merely to unblock migration.

Logical replacement/removal hides earlier URLs immediately, while seven-day retention and holds
still apply. Include both retained variants in paired SQL/blob backups. Recovery remains blocked
if either required image is absent or has a mismatched digest. Do not advertise immediate physical
erasure or preservation of the original photo. Browser color conversion varies across devices;
these images support identification, not gemological color measurement.

## Deployment configuration

All profiles except explicit `Development` require durable storage, the web SQL credential, a trusted
proxy address, the tenant-context proof key, and the shared data-protection certificate. Use environment
variables (`__` replaces `:`) or mounted configuration/secret files. Do not put secrets in command lines.

| Setting | Filesystem | Azure |
| --- | --- | --- |
| `Storage:Provider` | `FileSystem` | `Azure` |
| `Storage:Root` | Existing absolute local path | Unused |
| `Storage:DurableVolume` | `true` outside Development | Unused |
| `Storage:ContainerUri` | Unused | Private HTTPS container URI, without SAS/query |
| `Storage:InstallationId` | Stable nonempty installation UUID, required before startup or uploads | Stable installation UUID |
| `Deployment:Replicas` | Defaults to 1 | Positive replica count |

The filesystem root must belong exclusively to the installation. Protect it and its ancestors from
untrusted writers. Windows roots must be local drive paths; UNC roots are refused. Reparse points and
symlinks in the path are refused. Linux uses descriptor-relative operations and no-follow opens.
Multiple filesystem replicas require `Storage:SharedVolume=true` and
`Storage:AtomicSharedVolume=true`; these assert an operator-tested shared mount with the required atomic
rename and locking semantics. They do not make independent disks shared. Keep mount paths identical
across replicas. Compose supplies a persistent named volume owned by runtime UID 1654.

Azure uses system-assigned managed identity. Pre-create a private container, disable anonymous access,
and scope Storage Blob Data Contributor to the required container. No connection string or SAS is
accepted by production provider configuration. Object names include the installation and tenant UUIDs.
Azure RBAC, managed identity, and network policy require a deployment smoke check against the actual
account; Azurite verifies storage semantics but cannot verify those controls.

Provider aliases are SHA-256 bindings of provider kind, normalized location, and installation setting.
Changing any binding requires an explicit migration. Keep `InstallationId` formatting stable. Do not
change a root, container, or installation setting and expect old references to follow it automatically.

Uploads are bounded to 25 MiB and two minutes. The SHA-256 and length describe exact stored bytes.
Filesystem uploads write `.c`, flush, rename to staged `.a`, then publish create-only `.b`. On Linux,
each rename and physical deletion synchronizes the pinned directory before acknowledging completion;
sync failures propagate, and publication/deletion retries synchronize even when the rename/unlink
already happened. The filesystem must support directory `fsync` and atomic no-replace rename.
Use a native Linux named volume for Linux containers; Windows host bind mounts do not support
the required `renameat2(RENAME_NOREPLACE)` operation. Azure commits
an exclusive block list before publication; uncommitted blocks expire under Azure's lifecycle. Streaming
downloads verify identity at EOF: callers must observe successful completion before accepting a file.
No filename supplied by a client enters a physical path. Replacements retain earlier immutable bytes.

Logical deletion hides the attachment immediately and schedules physical removal after seven days.
A hold prevents deletion. The worker rechecks retention under a SQL lock, deletes idempotently, and
records purged revisions while retaining ownership, provenance, digest, and audit history. Interrupted
uploads remain recoverable pending operations; reconciliation reports their unreferenced bytes.

## Identity delivery and worker

Hosted scheduled jobs use `dotnet Workbench.Server.dll --worker --drain`. `Worker:MaxItems` defaults
to 100 and `Worker:MaxDurationSeconds` to 45; the run stops at an empty eligible queue, its item/time
bound, or cancellation. The existing `--once` remains a single-item operation. Continuous workers
emit aggregate queue status at startup processing and at least once per minute between work items;
drain jobs emit it after the batch. The `WorkQueueStatus` event contains only pending count and oldest
due age, including zeros for an empty queue. SQL grants the aggregate procedure only to workers and
does not expose cross-tenant rows. Delayed future work is counted but does not acquire premature age.

Public recovery and invitations default to disabled outside Development. Enable each separately with
`Identity:PublicRecoveryEnabled` and `Identity:PublicInvitationEnabled` only after configuring
`Identity:DeliveryProvider=Smtp` or `Graph`, the shared SQL limiter, and a running worker. SMTP settings are
`Smtp:Host`, `Port`, `Security` (`StartTls` or `SslOnConnect`), `Username`, `PasswordFile` (or deployment
secret `Password`), `Sender`, and canonical HTTPS `PublicOrigin`. Opportunistic TLS, invalid certificates,
plaintext authentication, and origins with paths/query/userinfo are refused. Use the operating system
trust store for an organizational CA. Never disable certificate validation.

For Graph, configure `Graph:MailboxId` with the shared mailbox object ID, `Graph:ManagedIdentityClientId` with the dedicated mail identity client ID, and `Graph:PublicOrigin` matching the application HTTPS `PublicOrigin`. Attach the mail identity only to the worker and grant Exchange application RBAC `Application Mail.Send` only for that mailbox. Do not grant tenant-wide Graph mail application permissions. The web process validates settings and queues messages without acquiring a mail token. Verify a real send and an out-of-scope mailbox denial before enabling public operations. Graph configuration validation is not a live authorization check.

Requests commit the identity operation and a purpose-bound encrypted outbox payload in the same SQL
transaction. The delivery worker decrypts the persisted payload during normal execution; web and worker
share protection-key authority, so this is process separation, not cryptographic isolation. It rechecks expiry, account state,
security version, recipient, purpose, and token hash before sending. Tokens are carried in URL fragments,
excluded from ordinary request URLs. Payloads are erased after completion, terminal failure, or restore
sanitation. Development's explicitly non-delivering memory sink is not a production delivery provider.

Provision a separate contained SQL user in `workbench_worker` using a protected administrative session.
Do not add it to web, operator, migrator, or owner roles. Its only cross-tenant action is the bounded
claim procedure, which returns references without protected payloads. Supply its connection as
`ConnectionStrings:Worker` or `WORKBENCH_WORKER_CONNECTION`, plus the same proof key, certificate,
storage binding, and selected delivery-provider configuration. Graph additionally requires the worker mail identity. Do not pass the web or migration credential.

```powershell
dotnet Workbench.Server.dll --worker
# Explicit scheduler invocation processes at most one claim.
dotnet Workbench.Server.dll --worker --once
```

Run continuously as a supervised process or invoke `--once` with a durable scheduler. An HTTP service
scaled to zero does not run work. Claims have 120-second leases and generation fencing; each execution
has a 60-second deadline. Failed transient operations use exponential delay plus jitter, up to five
attempts. Graph throttling delays are persisted with the queue item: `Retry-After` is clamped to one hour and cannot shorten the existing backoff. Token-acquisition failures also use bounded retries; Graph authorization rejections are permanent. Permanent failures and exhausted attempts become dead letters. Queue state is authoritative
in `Operations.WorkItems`; inspect it through an authorized tenant SQL session or a protected operator
session. Alert on dead letters and oldest due work age. A successful `--once` process exit indicates a
completed iteration, not necessarily successful delivery; inspect the work outcome.

Email submission cannot atomically acknowledge delivery with SQL. A crash after provider acceptance can produce a
duplicate email, but both messages carry the same single-use operation. Never replay a dead identity
payload: request a fresh recovery or invitation. After correcting a deletion failure or releasing a
hold, a maintenance principal may execute `Storage.ReplayDeletion @Id=<work UUID>`. Replay is audited,
advances generation, and rechecks retention in the worker. Do not edit lease fields manually.

Liveness is process-only. Readiness checks SQL/security state, bounded blob accessibility and private
container policy, and SMTP TLS/authentication when SMTP is enabled; it sends no probe email. Central
console telemetry exports fixed category, event number, level, timestamp, trace ID, and failure boolean.
It excludes arbitrary messages, exceptions, scopes, addresses, tokens, paths, queries, and payloads.
Detailed tenant-sensitive evidence belongs in authorized SQL audit records. Restrict proxy/SMTP/cloud
service logs separately; application redaction cannot control external infrastructure logging.

## Offline reconciliation, paired backup, and restore

Provision a separate `workbench_storage_maintenance` principal for the narrow manifest, relocation,
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

Each side can instead contain the Azure configuration above. Environment variables override JSON;
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
Only success clears blob recovery pending. Missing bytes, changed metadata, a wrong installation/database,
or incompatible schema keep recovery blocked. Rebuild protected keys, check tenant isolation and login,
and inspect readiness before the human-operated cutover. Restore drills use disposable SQL databases;
never overwrite a production database as part of verification.

## Provider migration and rollback

With all writers/workers stopped, save a paired backup and configure `Storage` as the current provider
and `Target` as the intended provider. Run `storage migrate` with the same connection, database,
offline-confirmation and config-file arguments. Migration refuses unresolved pending revisions; reconcile
them against the original store before cutover. A privileged offline repair may mark a pending operation
failed only after establishing that it has no retained content and any abandoned bytes have been handled
under the retention/cleanup procedure. Failed and purged revisions retain provenance but require no
further provider deletion. Do not mark an ambiguous publication failed merely to bypass this gate.
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

## Verification evidence

`BlobRecoveryTests` exercises real SQL BACKUP/RESTORE, snapshot copying, missing-object recovery blocking,
manifest validation and location migration. `AzureBlobStoreTests` exercises filesystem/Azurite copy in
both directions and resumable publication. SQL worker tests use independent connections and dedicated
roles. The full source and container gates remain required; actual Azure identity/RBAC and the selected
SMTP relay require deployment checks in the target environment.

The implementation review covered 44 changed source items, followed by a focused review of destination
privacy, pending-migration refusal, and cleanup state handling. No reportable finding remained within
the supported deployment policy. A public Azure destination was reproduced with Azurite and is now
rejected before copying; the test also preserves successful private copies in both directions.

Focused Stryker 4.16 mutation testing of blob transfer, read integrity, and SMTP validation reached
92.16% (46 killed and one timed out among 51 valid in-scope mutants). Remaining gaps cover successful SMTP invitation delivery,
exact clock-boundary equality, pool-buffer return, and a redundant persistent-integrity-failure guard.
Compile-error mutants and out-of-scope mutations were excluded. SQL procedures, authorization, retention,
and lease rules have real-SQL regression coverage but were not mutation-tested. Broader mutation coverage
and a successful delivery through the chosen production SMTP relay remain separate verification work.
