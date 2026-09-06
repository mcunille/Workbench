# Portable blob storage and operational providers

**Status:** Implemented

Tracks [issue #11](https://github.com/mcunille/Workbench/issues/11), following the
implemented data and identity phase in issue #10. This design was accepted on 2026-09-05.
The implementation and deployment prerequisites are documented in the
[provider runbook](../operations/blob-and-service-providers.md). Live Azure identity/RBAC and
the selected SMTP relay still require deployment verification.

## Purpose and scope

Provide portable binary storage, reliable identity message delivery, shared abuse
controls, safe telemetry, and durable background execution within the existing
modular application. SQL Server remains authoritative. Azure Blob Storage and a
dedicated filesystem volume are the initial binary providers. The same image and
application contracts serve hosted and self-hosted installations.

Domain attachment screens, inventory and purchasing relationships, accounting
retention policies, external OIDC, S3 adapters, and Azure deployment infrastructure
are outside this phase. Blob commands are internal application services exercised
through integration tests; this phase does not expose a general-purpose public
file upload API. Existing recovery and invitation flows receive production delivery.

## Baseline before this phase

- `Program.cs` registers SQL tenancy, durable sessions, provider placeholders, and
  dependency readiness. Production login already uses `SqlSensitiveRequestRateLimiter`.
- `IIdentityMessageDelivery` has development and disabled implementations.
  `IdentityOperationService` currently writes an operation and then calls delivery;
  those effects are not atomic or durably retried.
- Recovery has an account limiter but needs trusted-network and token-consumption
  abuse controls. Availability must also honor the explicit recovery/invitation flags.
- `WorkbenchDbContext`, tenant interceptors, and SQL filter/block predicates enforce
  tenant ownership. New tenant tables must participate in all those layers.
- No blob adapters, durable worker queue, or paired SQL/blob recovery tooling exist.

## Recommended approach and alternatives

Extend the modular server with narrow application contracts and infrastructure
adapters, using SQL for coordination and a worker mode of the same release image.
This reuses the existing operational dependencies and permits multiple replicas.

An external queue plus Redis limiter would introduce two more services without a
demonstrated capacity requirement. Provider-native workflows would duplicate
application semantics across Azure and self-hosting. Both are deferred; measured
capacity problems may justify adapters later without changing SQL authority.

## Blob authority, identity, and lifecycle

An attachment has a stable random identifier, tenant, current revision, concurrency
version, lifecycle timestamps, and deletion/retention state. Each immutable revision
records its own identifier, tenant, server-generated storage identifier, provider
alias, byte length, SHA-256 digest, inspected media type, creation time, actor,
source category, and optional tenant-consistent predecessor. A display filename is
bounded untrusted metadata and never becomes a key or operational log field.

Revision identity is independent of digest. Identical bytes are not deduplicated
across tenants. SHA-256 covers exactly the stored bytes and does not establish
authenticity or malware safety. Claimed provenance is distinguished from the
server-observed actor, source, time, length, and digest. Completed revision content
and provenance cannot be rewritten through ordinary SQL application permissions.

Application commands require authoritative tenant context and actor/permission
checks before provider access. Unknown and foreign attachment identifiers have the
same not-found result. Tenant identity never comes from a filename, request body,
provider metadata, or an arbitrary worker payload. Composite SQL keys/foreign keys,
RLS, and application guards enforce the same ownership relationships.

The lifecycle is:

1. Authorize the command and commit a pending revision and durable operation ID in
   SQL before writing bytes. Retried commands use a tenant-scoped idempotency key.
2. Stream bounded content into an exclusively created staging object, computing
   length and digest. Reject excess size and content-policy failures; never buffer
   an unbounded upload. Initial default maximum is 25 MiB.
3. Inspect an allowlisted format and require an explicit content-safety decision.
   Production untrusted ingestion remains disabled until a workflow supplies its
   malware policy and scanner. There is no permissive production scanner fallback.
4. Publish an immutable object without overwriting an existing object. Commit the
   validated revision and current-revision pointer using a SQL concurrency check.
   A stale replacement cannot displace a newer revision.
5. Failures leave a recoverable pending/failed operation. SQL readiness state alone
   authorizes reads; provider existence never promotes an attachment to available.

Replacement always creates a new revision. Deletion immediately hides the
attachment and transactionally enqueues cleanup. Physical removal is deferred until
the retention deadline and absence of a hold are rechecked. Default deletion grace
is seven days; retained prior revisions are not automatically erased on replacement.
An internal hold prevents physical deletion, but this is not a compliance-certified
retention system. Provider versions and backups have separately documented expiry;
application deletion does not promise immediate erasure from those copies.

Downloads reauthorize SQL metadata and open only the recorded available revision.
The initial application service streams bytes without public buckets or signed URL
issuance. Missing or corrupt content becomes an integrity failure, never an empty
successful download. Reconciliation checks digests; a streaming read cannot claim
full verification before the complete content has been read. Future HTTP consumers
must use attachment disposition, `nosniff`, bounded filenames, and appropriate
content policies before exposing untrusted bytes.

## Provider contract and filesystem confinement

`IBlobStore` provides create-only staging, immutable publication, open-read,
stat/verification, idempotent deletion, and paged inventory operations with typed
opaque identifiers, cancellation, and normalized error categories. Tenant and object
identifiers are separate validated values. Azure SDK objects, ETags, paths, credentials,
and keys remain inside infrastructure. Listing is maintenance-only, not user authority.

Azure uses a private configured container, generated installation/tenant prefixes,
conditional creation, and immutable publication. Managed identity is preferred in
hosted production. Local emulator credentials are restricted to the test/development
profile. Provider aliases are persisted so configuration changes cannot silently
reinterpret an existing revision's location.

Filesystem storage uses a dedicated absolute durable root and fixed-format generated
path segments. Reject arbitrary paths, separators, traversal, rooted identifiers,
alternate streams, and ambiguous casing. Validate the root and every existing ancestor
and descendant; reject symlinks and Windows reparse points, including a linked root.
Use exclusive creation, same-volume publication, and OS-supported no-follow/handle
validation to prevent check/open substitution where supported. Unsupported platforms
must fail closed rather than quietly fall back to lexical containment alone.

The root and its ancestors must be protected from modification by untrusted local
users; the provider is not a sandbox against an administrator controlling its host.
Tests cover static links and concurrent substitution. Deployment validation rejects
ephemeral container storage and rejects multiple filesystem replicas unless they
share the same durable volume with the required atomic operation semantics.

## Durable work and cross-store recovery

SQL work records contain a versioned allowlisted job kind, operation/reference IDs,
tenant where applicable, state, availability time, attempt count, lease owner,
lease expiry, and a monotonically increasing lease generation. No arbitrary serialized
code, SQL, endpoint URLs, credentials, or unrestricted tenant impersonation is accepted.

Enqueue and business changes share a SQL transaction. A narrowly granted claim
procedure yields a bounded job reference; tenant work then opens a separate proven
tenant context and verifies the referenced durable operation. The worker principal
has only claim/completion procedures and required tenant-scoped execution rights,
never migration or general RLS-bypass authority. Lease ownership/generation must
match on renewal, completion, and any consequential SQL state transition.

Processing is at least once. Blob effects use immutable operation identifiers and
create-only/idempotent operations; SQL completion is fenced. SMTP can produce a
duplicate after an ambiguous acknowledgement and cannot provide exactly-once delivery.
Expired or revoked identity operations must not be sent on retry. Duplicate messages
carry the same single-use token and do not create a new authorization operation.

Retry only transient failures, up to five attempts with exponential backoff and
jitter, capped at five minutes. Authentication/configuration, ownership, integrity,
and invalid-input failures do not retry automatically. Exhausted jobs become dead
letters with safe reason codes and explicit audited replay. Provider SDK retries
are bounded to avoid multiplying queue retries. Shutdown stops claims and allows a
bounded drain; crashes recover by lease expiry.

The image provides an explicit worker mode. It can run continuously or through an
external scheduled trigger; an idle scale-to-zero web service is not a scheduler.
Readiness/operations expose worker lease progress and queue age without payloads.

## SMTP and identity integration

Preserve provider-neutral identity messages and add an authenticated SMTP adapter.
Production requires implicit TLS or mandatory STARTTLS, normal certificate and host
validation, configured credentials, and a canonical HTTPS public origin. Never
downgrade encryption or disable certificate checks. Test sinks are explicit local
providers and cannot satisfy production validation.

Create the identity operation and message outbox entry atomically. The database stores
only a hash for token validation; the outbox necessarily retains the deliverable
token and recipient as an encrypted, purpose-bound payload using shared protected
keys. Decryption is restricted to delivery code. Clear ciphertext after delivery,
expiry, or terminal failure and retain only safe diagnostic identifiers. Include
outbox/key confidentiality in backup and restore procedures.

Build links from the canonical origin, with tokens in URL fragments where the current
client can consume them without request-query logging. Validate recipient/sender
addresses and use structured message APIs to prevent header injection. Do not log
SMTP protocol exchanges. Honour recovery and invitation enablement separately at
startup and at request/service boundaries.

Keep SQL-backed limits and extend them to recovery, invitation creation, and token
consumption. Apply independent operation-specific account/token and trusted-network
budgets before expensive work. Normalize addresses using the existing identity
rules. Use keyed hashes for persisted sensitive partitions, with one shared key
across replicas; rotation and expired partition cleanup are explicit operations.
Unknown accounts consume equivalent budgets and retain non-enumerating responses.
Database limiter failure denies the operation with a stable service-unavailable
response; no in-memory production fallback is allowed.

## Configuration, health, and telemetry

Typed options validate provider choice, root/container, size limits, retention,
SMTP security, canonical origin, shared keys, replica topology, and retry bounds.
Secrets come from deployment environment or mounted files and never appear in
validation messages. Validation must cover every non-development deployment profile.

Liveness remains process-only. Readiness requires current SQL schema/security grants,
the selected blob provider, and the protected keys required for enabled services.
Use bounded non-destructive probes. SMTP configuration is validated at startup;
bounded connection/TLS/authentication checks gate readiness when delivery is enabled,
without sending probe mail. Queue age and dead letters are separate operational
signals; a dependency outage must not trigger a destructive liveness restart loop.

Telemetry uses a central allowlist of event IDs, outcome codes, durations, counts,
release version, and generated correlation IDs. Tenant/actor identifiers belong in
authorized SQL audit records; operational correlation uses pseudonymous identifiers
only when needed. Exclude raw paths, URLs/queries, filenames, recipient addresses,
tokens, headers, connection strings, blob bodies, SQL parameters, and arbitrary
exception messages. Suppress or sanitize framework and dependency logs/traces before
export, not only application call sites. Test actual configured log/export pipelines
with sentinel secrets. Sampling does not affect durable security audit records.

## Reconciliation, backup, restore, and provider migration

Reconciliation compares SQL references and paged provider inventory within one
configured installation namespace. It reports missing/corrupt available objects,
stale pending uploads, and unreferenced objects. Automatic cleanup operates only on
known failed/deleted SQL operations after their leases and grace periods expire.
Unknown objects are report-only; operator-approved cleanup requires a saved manifest
and a fresh reference check. Never delete outside the installation/tenant namespace.

For the initial paired backup procedure, enter maintenance mode, stop/drain workers
and blob mutations across all replicas, and create a SQL backup plus a blob snapshot
or copy and digest manifest from that stable state. Record installation, schema,
provider alias, revision, tenant, length, digest, and backup identity in a protected
manifest. Resume only after consistency checks. Azure version recovery and local
copy/snapshot instructions must identify exact versions, not rely on wall-clock
timestamps to imply a cross-store snapshot.

Restore remains offline until SQL session/operation invalidation, outbox cancellation,
lease reset, key availability, tenant checks, and all referenced blob digest checks
succeed. Restored pending messages must never send old authorization links. Missing
or mismatched content blocks completion of the recovery drill and is reported safely.

Provider migration is an explicit maintenance operation: copy immutable revisions,
verify every length/digest, then transactionally update SQL locations with concurrency
checks. Keep the source intact through a documented rollback window. A configuration
switch alone is not migration. Exercise filesystem-to-Azure-emulator and reverse
copy paths, interruption/resume, and rollback before declaring portability complete.

## Schema compatibility and implementation sequence

Add versioned migrations for attachment/revision/operation metadata, queue/outbox,
RLS predicates, immutable-column protections, indexes, and narrow worker procedures.
Update database permission/readiness checks. Never run migrations in web startup.
Use forward repair for production; down migrations that discard retained metadata
or queued work must be refused unless an explicit destructive recovery procedure
has been authorized. Test upgrade from the current schema and restore to a separate
database with paired binaries; do not use rollback as an implicit content deletion.

After design acceptance, execute test-first increments:

1. SQL ownership, immutable revisions, lifecycle transitions, and permission tests.
2. Shared blob contract tests for filesystem and Azure/Azurite, including adversarial
   keys, links, concurrency, cancellations, interruption, and digest failures.
3. Atomic enqueue, lease fencing, retries, crash recovery, and duplicate execution
   against real SQL using independent worker instances.
4. SMTP TLS/certificate/authentication failures, encrypted outbox lifecycle, recovery
   and invitation flag enforcement, and multi-replica abuse controls.
5. Telemetry sentinel tests, configuration/readiness failure tests, recovery and
   provider migration drills, and updated operational runbooks.

## Verification and acceptance gate

Use Gherkin comments in tests. Confirm focused behavioral tests fail for the intended
missing behavior, then implement and run current-source verification. Use affected
mutation tests for authorization, lifecycle, retention, leases, and rate limits;
report unavailable tooling and justify equivalent survivors.

Run `scripts/verify.ps1` and `scripts/smoke-container.ps1`, extending their gates for
the new providers and worker. Exercise recovery/invitation through the running
application and record local URLs. Azure emulator tests do not establish managed
identity or live Azure behavior: record live-provider verification separately, and
do not describe untested hosted configuration as production-ready.

Review the accepted design for security before implementation and review the final
immutable implementation diff before delivery. Resolve all reportable findings;
an incomplete scan is not a clean review. Deliver scoped commits and a ready-for-review
PR after verification, leaving merge and production operations to explicit approval.

## Residual risks and approval

This design deliberately adds an internal blob primitive without a public upload
workflow, retains old revisions, and uses a SQL queue with a separate worker mode.
These are architectural/product choices requiring acceptance. SMTP duplicates,
protected outbox token retention, OS-specific filesystem guarantees, maintenance
windows for consistent backup, and live Azure verification are explicit constraints.
If implementation cannot meet a stated invariant, revise this design and obtain
approval for the changed boundary rather than silently weakening it.
