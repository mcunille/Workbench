# Database principals

This is the authoritative operational matrix for Workbench database identities. Role names below
are SQL roles; provision a distinct database user or managed identity for each workload. Application
tenant administrators are not SQL operators. Never combine workload roles or give a restricted
workload owner/migrator authority.

## Principal matrix

| Principal or role | Intended use and authority | Boundary and delivery |
| --- | --- | --- |
| Setup/database owner | One-time initialization and principal provisioning; protected administrative restore work and local development recovery-link generation. | No standing credential in web, workers, ordinary development agents or scheduled jobs. Backup/restore server authority is separate from the restricted operator role. |
| `workbench_migrator` | Explicit reviewed EF migration jobs. SQL grants database `CONTROL`, including authority to alter schema/security controls. | Deployment control-plane identity, not a tenant-data isolation boundary. Deliver only to a separately authorized one-shot migration job; never to web/operator/worker configuration. |
| `workbench_operator` | Execute `Administration.ProvisionTenant` and `Administration.SanitizeRestore` for bootstrap/additional tenants and restore sanitation. | No general tenant-data browsing. Direct Identity/Tenancy/Security data access is denied; development recovery-link generation and `MarkRestorePending` are denied. Keep in protected operator tooling only. |
| `workbench_web` | Tenant-scoped runtime reads/writes; narrow identity resolution, invitation claims, item/photo/acquisition commands and readiness procedures. Items allow direct SELECT/INSERT, with UPDATE/DELETE denied; creation snapshots and acquisition tables are SELECT-only with writes through restricted commands. | Web credential only in the web workload. No migration history/security-control changes or raw tenant proof-store access. Do not reuse for worker, migration or operator tools. |
| `workbench_worker` | Tenant-scoped reads of queue, storage metadata, identity operations/users and protection keys; tenant audit INSERT. Execute bounded `ClaimWork`, `LockWork`, `CompleteWork`, `RetryWork` and aggregate `ReadWorkQueueStatus`. | Separate worker credential/configuration. Cross-tenant claim returns references without protected payloads; aggregate status exposes counts/age without tenant rows. Tenant proof is required for subsequent tenant reads. Do not combine with web/operator/migrator/owner roles. |
| `workbench_storage_maintenance` | Execute `Storage.ExportManifest`, `AssertMigrationReady`, `RelocateRevision`, `CompleteRecoveryVerification`, `ReplayDeletion`, `ReadRecoveryInventory`, `AcceptFileRecovery`, and `ReadFileRecoveryCompletion`. | Protected maintenance/recovery tooling, never an ordinary web/worker user. Narrow procedures can handle cross-tenant manifests/recovery; this is not general tenant-data browsing. Offline or isolated-target requirements still apply to the selected runbook. |

Storage-provider and mail managed identities are additional cloud authorities. SQL role membership
does not grant Azure storage access or permission to send mail. See
[provider and worker configuration](blob-and-service-providers.md#deployment-configuration) and
[Azure deployment](azure-deployment.md).

## Provisioning and secret delivery

The [setup guide](../setup.md) owns local initialization and existing-database precautions.
`Workbench.Database principals provision` provisions exactly three distinct contained password users:
web, operator and migrator. It does **not** provision worker or maintenance users. Provision those
separately with a protected administrative session; the
[self-host helper](../../scripts/local-self-host/Provision-WorkerRoles.ps1) supplies the concrete
self-host procedure. It refuses existing target users; inspect rather than overwrite them.

For Azure SQL, `Workbench.Database principals provision-entra` requires a version 1 manifest with
exactly all five workload roles and distinct `name`, `principalId`, and `clientId` values per identity.
SQL application-user SIDs use client IDs; Azure RBAC/Exchange use principal IDs. Legacy `objectId`
manifests are unsupported. Provisioning must not rotate the proof of an initialized installation.
See the [Azure identity manifest procedure](azure-deployment.md).

Store production secrets in the deployment platform's secret facility. Deliver the migrator secret
only to its one-shot job, remove it on exit, audit access, and rotate independently of the web
credential. Never place connection strings/password files in Git, image layers, logs, command
history, prompts, or build artifacts. Rotate a principal if its secret may have crossed a boundary.

Tenant RLS additionally requires a distinct 32-byte proof key. Provisioning writes it to an
owner-only SQL table; web/operator direct access is explicitly denied. Deliver its matching value
separately to the workload, preferably through a read-only mounted secret file. A web connection
string alone cannot select an arbitrary tenant through `SESSION_CONTEXT`. Keep the proof separate
from every database password and never expose it in environment inspection. Rotate SQL/workload
copies together under drained traffic using an explicitly reviewed operation.

For non-development provisioning, pass the Base64-encoded 32-byte value only through
`--tenant-context-proof-key-file`, then remove the temporary file. Web replicas use
`WORKBENCH_TENANT_CONTEXT_PROOF_KEY_FILE` pointing to a read-only mount. Workers receive the same
proof key, shared protection certificate, storage binding and selected delivery configuration,
with their own SQL connection (`ConnectionStrings:Worker` or `WORKBENCH_WORKER_CONNECTION`).
Shared protection authority means web/worker separation is not cryptographic isolation.

Development recovery links return a raw credential-reset capability for an existing account. They
require the local one-time setup/owner connection, never production web/operator configuration,
and an explicitly named new output file. Remove that file immediately after use.

## Additional tenant provisioning

Only an installation operator may create another tenant. Supply the operator connection and new
administrator password in separate access-controlled files:

```powershell
Workbench.Database tenant create --connection-file <operator-path> --expected-database <name> `
  --tenant-name <tenant-name> --admin-email <email> --password-file <password-path>
```

Tenant administrators manage users inside their tenant after provisioning. For migration jobs use
[database migrations](database-migrations.md); for restore sanitation use
[database backup and restore](database-backup-restore.md). SQL operator authority alone does not
perform the administrative SQL restore or storage verification.

## Source and verification

The matrix is checked against [baseline grants](../../src/Workbench.Server/Persistence/Migrations/20260904061246_EstablishSecurityBoundaries.cs),
[password provisioning](../../src/Workbench.Server/Administration/PasswordPrincipalProvisioning.cs),
[Entra provisioning](../../src/Workbench.Server/Administration/EntraPrincipalProvisioning.cs),
[worker grants](../../src/Workbench.Server/Persistence/OperationalSchema.cs),
[aggregate queue grants](../../src/Workbench.Server/Persistence/Migrations/20260906031109_AddDeploymentQueueTelemetry.cs),
[storage-maintenance grants](../../src/Workbench.Server/Persistence/StorageMaintenanceSchema.cs),
and [file-recovery grants](../../src/Workbench.Server/Persistence/FileRecoverySchema.cs).
Collection grants evolve through the [migration inventory](database-migrations.md#migration-compatibility-matrix).

Run `./scripts/verify-database-permissions.ps1` for the baseline role probes and the full server
verification suite for worker, storage/recovery, collection and principal-provisioning coverage.
These SQL checks do not establish live Azure RBAC or mail-provider authorization; those require
the deployment checks in the provider/Azure runbooks.
