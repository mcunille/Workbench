# Database principals

GEM-02 grants `workbench_web` SELECT on exactly `Gemology.Entries`, `Aliases`,
`SourceAssertions`, and `LocalityAssertions`. These shared reference tables contain no tenant
records. Web and worker direct INSERT/UPDATE/DELETE are denied; worker, operator, and storage
maintenance receive no shared read grants. No catalog publish authority or schema-wide grant
is introduced. Password provisioning accepts these exact object reads and rejects broader grants.

GEM-06 additionally grants web SELECT on tenant-RLS `Gemology.TenantEntries` and `TenantOverrides`
and EXECUTE on exactly `SaveTenantEntry`, `SetTenantEntryArchive`, and `SaveTenantOverrides`.
Direct table INSERT/UPDATE/DELETE remain denied. Worker, operator and public receive no tenant
reference reads or command execution. The commands require the SQL tenant proof, current enabled
tenant/member authority, an explicit transaction, publication then tenant application locks, and
expected rowversions; web cannot mutate another tenant or raw reference storage. C# owns effective
Unicode identity, provenance and final-candidate reconciliation; SQL independently enforces scope,
shape, versions and narrow writes. Service-admin authority supplies no tenant context or access.

BK-05 bill mutations (`Purchasing.SaveSupplierBill`, `ReviewSupplierBill`, `PostSupplierBill`) have
no `workbench_web` EXECUTE grant. The fixed `SupplierBillsManage` and `SupplierBillsPost` permissions
are not assigned to production roles. Bill tables deny direct runtime writes; bounded read procedures
require current management or accounting-report authority. Do not activate these commands as a
deployment shortcut: public entry, evidence holds, complete bill corrections and production
reconciliation acceptance remain release gates.

BK-06 supplier financial tables expose tenant-RLS SELECT to `workbench_web`; direct INSERT, UPDATE
and DELETE remain denied to web and worker. `Purchasing.RecordSupplierPayment`, `ApplySupplierFunds`,
`ReverseSupplierApplication`, `CorrectSupplierPayment`, correction preview and the derivation/kernel
participants receive no runtime EXECUTE grant. `SupplierPaymentsRecord`, `SupplierPaymentsCorrect`
and `SupplierAllocationsManage` receive no production role assignments. Do not grant them to enable
the read-only reports; `BookkeepingAvailable` remains false.

The web role additionally receives SELECT on `Purchasing.SupplierItemControl`,
`SupplierPaymentControl` and `SupplierReportBillIdentity`. The first two validate stored control/source
proof; the last exposes only a bill Id for report-filter validation, including zero-value bills.
These functions use tenant-protected data and grant no direct bill-table browsing or private-file
access. Worker function reads remain unavailable. Password-principal provisioning admits these exact
read grants without broadening financial mutation authority. API reads require AccountingReportsRead;
new write permissions are unnecessary for reporting. Supported recognition-attribution reconstruction
is a protected maintenance/source-owner operation, not a web/worker repair command.

This is the authoritative operational matrix for Workbench database identities. Role names below
are SQL roles; provision a distinct database user or managed identity for each workload. Application
tenant administrators are not SQL operators. Never combine workload roles or give a restricted
workload owner/migrator authority.

## Principal matrix

| Principal or role | Intended use and authority | Boundary and delivery |
| --- | --- | --- |
| Setup/database owner | One-time initialization and principal provisioning; protected administrative restore work and local development recovery-link generation. | No standing credential in web, workers, ordinary development agents or scheduled jobs. Backup/restore server authority is separate from the restricted operator role. |
| `workbench_migrator` | Explicit reviewed EF migration jobs. SQL grants database `CONTROL`, including authority to alter schema/security controls. | Deployment control-plane identity, not a tenant-data isolation boundary. Deliver only to a separately authorized one-shot migration job; never to web/operator/worker configuration. |
| `workbench_operator` | Execute `Administration.ProvisionTenant` and `Administration.SanitizeRestore` for bootstrap/additional tenants and restore sanitation; execute `ProvisionServiceAdmin`, `DisableServiceAdmin`, `ResetServiceAdminPassword` and `RevokeServiceAdminSessions` in `Administration` for service-admin identity maintenance. | No general tenant-data browsing. Direct Identity/Tenancy/Security and ServiceAdministration account/session access is denied; development recovery-link generation and `MarkRestorePending` are denied. Keep in protected operator tooling only. |
| `workbench_web` | Tenant-scoped runtime reads/writes; narrow identity resolution, invitation claims, item/photo/acquisition/purchase/supplier commands and readiness procedures. Items allow direct SELECT/INSERT, with UPDATE/DELETE denied; creation snapshots, acquisitions, purchase drafts, suppliers, purchase counters, immutable purchase revisions and request receipts are SELECT-only with writes through restricted commands. Purchasing exposes restricted draft create/update/delete, purchase commit/amend and supplier commands; direct table writes remain denied, including reference allocation and retry receipts. Accounting setup, accounts, revisions, receipts and role assignments are SELECT-only; writes use `Accounting.Save` and `Administration.AssignAccountingRoles`. Journal entries/lines, source events, posting receipts, policy freezes, periods, period closures and receipts, and correction groups and receipts are SELECT-only under tenant RLS. Direct runtime writes to these durable accounting tables are denied. The internal `Accounting.PostJournal`, `Accounting.EnsureOpenPeriod`, `Accounting.ClosePeriod` and `Accounting.CorrectJournal` kernels have no runtime execute grants; no production posting, correction or close adapter is installed. Runtime direct writes to Identity roles and claims are denied. | Web credential only in the web workload. No migration history/security-control changes or raw tenant proof-store access. Do not reuse for worker, migration or operator tools. |
| `workbench_worker` | Tenant-scoped reads of queue, storage metadata, identity operations/users and protection keys; tenant audit INSERT. Execute bounded `ClaimWork`, `LockWork`, `CompleteWork`, `RetryWork` and aggregate `ReadWorkQueueStatus`. | Separate worker credential/configuration. Cross-tenant claim returns references without protected payloads; aggregate status exposes counts/age without tenant rows. Tenant proof is required for subsequent tenant reads. Do not combine with web/operator/migrator/owner roles. |
| `workbench_storage_maintenance` | Execute `Storage.ExportManifest`, `AssertMigrationReady`, `RelocateRevision`, `CompleteRecoveryVerification`, `ReplayDeletion`, `ReadRecoveryInventory`, `AcceptFileRecovery`, and `ReadFileRecoveryCompletion`. | Protected maintenance/recovery tooling, never an ordinary web/worker user. Narrow procedures can handle cross-tenant manifests/recovery; this is not general tenant-data browsing. Offline or isolated-target requirements still apply to the selected runbook. |
| Online backup collector (separate managed-identity user) | Direct EXECUTE grant only on `Storage.ExportManifest`, plus CONNECT. No workload-role membership, table reads or mutation grants. | Read-only cross-tenant revision identity/hash inventory; provision separately using the [online backup procedure](online-backup-recovery.md#deploy-and-verify-backup-collection). Expiration receives no SQL user. |

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

Service-admin browser identities are independent of SQL workload principals and tenant identities.
The web role receives only `ServiceAdministration.FindAccountForLogin`, `CreateSession`,
`ResolveSession` and `RevokeSession` execution. It has no password-write command: compatible older
hashes can authenticate, but credential replacement or hash upgrades require an audited operator
password reset. The operator role
receives only the four named service-admin maintenance commands; the worker receives neither set.
All three roles are denied direct SELECT, INSERT, UPDATE and DELETE on service-admin accounts and
sessions. No new SQL credential is needed or delivered to the web image or configuration.

GEM-05 also grants web execution of `Gemology.ReadDrafts`, `ReadDraft`, `SaveDraft`,
`ReadPublication`, `ReadPublicationAudit`, and `PublishDraftBatch`. Each requires a current
service-admin account/session pair, with enabled-account, security-version, revocation, and
expiry checks. Writes revalidate authority after acquiring the catalog transaction lock.
Worker, operator, and public receive no curation EXECUTE grants. Raw `Gemology.Drafts`,
`PublishRequests`, and `PublicationAudit` SELECT/INSERT/UPDATE/DELETE remain denied to web,
worker, and operator, and direct published catalog mutation remains denied. The commands use
ownership chaining for their narrow writes; do not add broad schema grants or put an operator
credential in the web process. Admin HTTP routes resolve actor/session from the dedicated
authenticated principal and never accept tenant identity or client-supplied audit authority.

`PublishDraftBatch` is an internal application command: C# derives Unicode-normalized identity
and alias keys and validates the final catalog. SQL binds raw content and alias projections to
saved drafts and checks uniqueness against supplied keys; it does not independently derive
semantic identity. A direct SQL caller with current admin authority must still honor this
normalization contract. See the [shared reference architecture](../ARCHITECTURE.md#shared-gem-reference-foundation)
for the lock-duration and durable-receipt compatibility constraints.

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

## Service-admin identity maintenance

Use the restricted operator connection and separate access-controlled password files. Provision
prints the new non-secret account ID; retain it for subsequent maintenance. Each command requires
the connection file's database to exactly match `--expected-database`.

```powershell
Workbench.Database service-admin provision --connection-file <operator-path> --expected-database <name> `
  --email <email> --password-file <password-path>
Workbench.Database service-admin revoke-sessions --connection-file <operator-path> --expected-database <name> `
  --account-id <guid>
Workbench.Database service-admin reset-password --connection-file <operator-path> --expected-database <name> `
  --account-id <guid> --password-file <replacement-password-path>
Workbench.Database service-admin disable --connection-file <operator-path> --expected-database <name> `
  --account-id <guid>
```

Passwords must satisfy the current Workbench policy: 14–1024 characters, at least four distinct
characters, and an uppercase letter, lowercase letter, digit and non-alphanumeric character.
Password files may end in a newline; other password whitespace is preserved. Remove temporary
secret files after use. The tool never accepts a password value or connection string as an option.
Unknown, repeated, missing or malformed options fail before maintenance; failures emit a generic
message without input values. Duplicate normalized emails and absent target accounts also fail
without partial changes. Email uniqueness belongs to the separate service-admin account store;
the same email may identify a tenant account independently.

Revoke-sessions ends existing browser authority on the next request while allowing a fresh sign-in.
Reset-password replaces the credential and revokes existing sessions; it preserves a disabled
account's disabled state. Disable ends existing sessions and denies subsequent sign-in. This CLI
provides no enable command, public registration or tenant-admin invitation path. Other successes
emit only the action outcome. These commands maintain identities and do not grant tenant data or
provide a catalog editor or publishing workflow.

## Source and verification

The matrix is checked against [baseline grants](../../src/Workbench.Server/Persistence/Migrations/20260904061246_EstablishSecurityBoundaries.cs),
[password provisioning](../../src/Workbench.Server/Administration/PasswordPrincipalProvisioning.cs),
[Entra provisioning](../../src/Workbench.Server/Administration/EntraPrincipalProvisioning.cs),
[worker grants](../../src/Workbench.Server/Persistence/OperationalSchema.cs),
[aggregate queue grants](../../src/Workbench.Server/Persistence/Migrations/20260906031109_AddDeploymentQueueTelemetry.cs),
[storage-maintenance grants](../../src/Workbench.Server/Persistence/StorageMaintenanceSchema.cs),
and [file-recovery grants](../../src/Workbench.Server/Persistence/FileRecoverySchema.cs).
Service-admin grants are installed by
[the identity schema](../../src/Workbench.Server/Persistence/ServiceAdminIdentitySchema.cs) and
invoked by [operator commands](../../src/Workbench.Server/ServiceAdministration/ServiceAdminOperatorCommands.cs).
Collection grants evolve through the [migration inventory](database-migrations.md#migration-compatibility-matrix).
Accounting kernel provisioning removes only accidental runtime `GRANT` entries from web and worker
principals; an existing protective `DENY` is preserved. No migration or reprovisioning step grants
the test-only synthetic adapters to a production principal.

Run `./scripts/verify-database-permissions.ps1` for the baseline role probes and the full server
verification suite for worker, storage/recovery, collection and principal-provisioning coverage.
These SQL checks do not establish live Azure RBAC or mail-provider authorization; those require
the deployment checks in the provider/Azure runbooks.
