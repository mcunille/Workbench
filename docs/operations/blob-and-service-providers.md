# Blob storage and operational services

The web application, database tool, and explicit worker share the same release. Blob APIs are internal
application services for generated content; there is no general upload endpoint. Item photographs
and acquisition documents use the bounded workflows below; user-facing behavior is described in the [collection guide](../collection.md). Other user uploads require their own type allowlist and malware
policy before publication.

## Item photograph ingestion

[Read this section](blob-provider-ingestion.md#item-photograph-ingestion).

## Acquisition document validation

[Read this section](blob-provider-ingestion.md#acquisition-document-validation).

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

### Financial evidence retention

The same inventory, hold, disposal and paired-recovery rules apply to BK-08 credit notes, incoming
refunds and outgoing refund repayments. A source correction can inherit retained documents under
its own typed owner. Settlement or reversal preserves the existing links and their frozen deadlines.

BK-07 acquires authoritative source/revision links and effective `Held` protection in the posting
transaction. Independent holds remain separate and cannot be cleared by disposal. Ordinary removal
rejects linked documents, including expired links; preparation and completion both revalidate.
Generic deletion, work admission and completion also enforce the SQL predicate, protecting older
workers that still read only `Held`. Cleanup holds the attachment lock through provider deletion and
never acquires a PO/accounting lock afterward.

Each link freezes its policy and later-of-posting/recorded anchor. Unset policy and legacy posted
links are indefinite. Finite expiry only permits reasoned disposal with document-management and
accounting-configuration authority, current versions, complete expired link membership and no
independent hold or pending recovery. The immutable receipt removes live visibility and starts at
least seven days of grace. Cleanup rechecks effective/independent holds, deadlines and exact receipt
membership; successful authorization is not a physical-erasure claim.

Paired manifests include protected removed/purged revisions. Recovery inspection and relocation
verify their exact digest/length; missing or corrupt bytes require a bound accepted disposition and
remain unavailable without erasing financial links. Every SQL revision identity protects against
orphan cleanup. Disposed bytes are never resurrected as active evidence. Recovery fingerprints bind
links, policy, additions/disposal membership, holds and removal state, so old or stale reports cannot
accept changed protection. Sanitation sets recovery pending even when only protected purged content
remains. Follow [backup/restore](database-backup-restore.md) before resuming workers.

Financial retention protects authoritative content; backup sets retain their separate recovery
window. Older backups may contain disposed bytes until expiry. Online capture compares provider
copies with mandatory SQL inventory and reports missing protected identities as incomplete; an
accepted missing-file disposition cannot establish successful backup freshness. Local evidence does
not attest hosted Azure identity/network access or WORM behavior.

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

The encrypted outbox is an intentional exception to hash-only token storage: delivery needs the
recipient and original single-use token. Treat SQL backups and the shared decryption keys as a
combined delivery capability and protect their confidentiality together. An expired or revoked
operation must not be sent on retry; the worker completes ineligible work without delivery and
clears its payload. A terminal failure clears ciphertext too. Duplicate-delivery limits below
remain applicable even when encryption and single-use consumption work correctly.

Provision the separate worker identity following the [database-principal matrix](database-principals.md).
Do not add it to web, operator, migrator, or owner roles. Its cross-tenant claim action is the bounded
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
in `Operations.WorkItems`; inspect it through an authorized tenant SQL session or a protected database-owner
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
The worker separately emits the closed `WorkQueueStatus` aggregate event described above; those
counts and ages are not arbitrary logger fields or tenant-row output.
Detailed tenant-sensitive evidence belongs in authorized SQL audit records. Restrict proxy/SMTP/cloud
service logs separately; application redaction cannot control external infrastructure logging.

## Offline reconciliation, paired backup, and restore

[Read this section](blob-provider-recovery.md#offline-reconciliation-paired-backup-and-restore).

## Provider migration and rollback

[Read this section](blob-provider-recovery.md#provider-migration-and-rollback).

## Verification evidence

[Read this section](blob-provider-verification.md#verification-evidence).

### Retained provider acceptance requirements

[Read this section](blob-provider-verification.md#retained-provider-acceptance-requirements).

### Recorded checks and limits

[Read this section](blob-provider-verification.md#recorded-checks-and-limits).

## Collection package preparation

Collection ZIP export reads only detail revisions selected with current item records in one tenant-scoped
serializable transaction. Removed or replaced photos retain their immutable bytes for seven days;
this is a correctness dependency for the maximum two-minute package preparation after database locks
are released. Do not shorten retention without revisiting the export contract. Recovery-unavailable,
missing, corrupt or mismatched-provider revisions fail the whole package. Existing offline provider
migration and restore requirements still apply. Repeated failures should be investigated through the
protected reconciliation/recovery procedure above, not by dropping photos from the export.

Packages use bounded process memory; no export jobs or temporary disk artifacts are persisted.
At most two CSV/ZIP preparations run per application instance. Each ZIP can retain up to 128 MiB
of archive data, a final response copy, up to 32 MiB CSV and 16 MiB manifest, plus bounded projection,
serializer and transfer buffers. Budget memory for concurrent preparation and in-flight response
copies; the preparation slots do not limit slow HTTP deliveries. Ordinary host admission and resource
controls remain necessary. The browser retains a Blob up to 128 MiB until it expires or is cleared.
Cancellation and failure dispose streams and release references/capacity; process termination leaves
no temporary export file. Neither managed memory disposal nor object URL revocation promises secure
erasure of previously delivered data. See the [package format](../collection-export.md).

Bounded in-memory preparation avoids persisted artifacts, export leases and job-recovery state.
Streaming an archive before verification would lose the all-or-nothing preparation outcome;
holding SQL locks through provider reads would block writes for the full preparation deadline.
Larger asynchronous exports need a separately designed authorization, expiry and recovery contract.
