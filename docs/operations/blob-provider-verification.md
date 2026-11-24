# Blob provider verification

[Return to the main entry](blob-and-service-providers.md).

## Verification evidence

### Retained provider acceptance requirements

The original provider design (issue #11, delivered in PR #23) required confinement under static links
and concurrent path substitution, fail-closed handling of unsupported filesystem semantics,
immutable create-only publication, tenant authorization before provider access, and SQL generation
fencing for consequential worker transitions. Keep these requirements when changing provider code;
an operator's shared-volume declaration or lexical path check alone does not establish them.

Identity abuse controls must retain independent, operation-specific budgets for recovery requests,
invitation creation, and recovery/invitation token consumption. Apply both account/token and
trusted-network budgets before expensive work, using the existing identity normalization rules.
Persist sensitive partitions as keyed hashes with one shared key across replicas; key rotation and
expired-partition cleanup remain explicit operational requirements. Unknown accounts must consume
equivalent budgets and receive non-enumerating responses. SQL limiter failure must deny the operation
with a stable service-unavailable response; an in-memory production fallback is not permitted.
These are retained acceptance requirements, not a claim of new runtime verification.

Verification must cover interruption/resume and rollback for filesystem/Azure migration in both
directions, SMTP TLS/certificate/authentication failures and successful delivery, protected outbox
expiry, multi-replica abuse controls, and sentinel secrets through the configured telemetry pipeline.
Provider SDK retries must remain bounded so they do not multiply durable queue retries without limit.
The original queue backoff target was at most five minutes; the later Graph-specific `Retry-After`
policy documented above permits up to one hour. Neither policy promises exactly-once email delivery.

The evidence below records narrower exercised scope, not a claim that every original requirement
has fresh coverage. Preserve unestablished cases as verification gaps until evidence or an approved
replacement resolves them. Hosted acceptance belongs to the [acceptance matrix](production-readiness.md).
Photo and document ingestion use the explicit validation policies above, not antivirus certification;
future untrusted-upload workflows still require a content-safety and malware-handling decision before
publication. Retention holds are internal deletion controls, not compliance certification or a promise
to erase provider versions and backups immediately.

### Recorded checks and limits

`BlobRecoveryTests` exercises real SQL BACKUP/RESTORE, snapshot copying, missing-object recovery blocking,
manifest validation and location migration. `AzureBlobStoreTests` exercises filesystem/Azurite copy in
both directions and resumable publication. SQL worker tests use independent connections and dedicated
roles. The full source and container gates remain required; actual Azure identity/RBAC and the selected
SMTP relay require deployment checks in the target environment.

Azurite regression coverage verifies rejection of public Azure destinations before copying and
successful private copies in both directions.

Historical focused Stryker 4.16 mutation testing of blob transfer, read integrity, and SMTP validation reached
92.16% (46 killed and one timed out among 51 valid in-scope mutants). Remaining gaps cover successful SMTP invitation delivery,
exact clock-boundary equality, pool-buffer return, and a redundant persistent-integrity-failure guard.
Compile-error mutants and out-of-scope mutations were excluded. SQL procedures, authorization, retention,
and lease rules have real-SQL regression coverage but were not mutation-tested. Broader mutation coverage
and a successful delivery through the chosen production SMTP relay remain separate verification work.
