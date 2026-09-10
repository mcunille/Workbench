# Database migrations

Database migrations are an explicit, human-controlled deployment operation. A web replica never
migrates its database. Use the migrator identity defined in the authoritative
[database-principal matrix](database-principals.md); keep setup and operator authority out of web configuration.

## Deployment procedure

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
Blob-bearing databases also require the [paired SQL/blob workflow](blob-and-service-providers.md#offline-reconciliation-paired-backup-and-restore).
Development verification does not authorize production migration, rollback, or cutover.

## Local one-time setup

Follow the [canonical setup guide](../setup.md) for generated credentials, SQL containment,
bootstrap, routine migrations, and existing-database precautions. See
[principal provisioning and secret delivery](database-principals.md#provisioning-and-secret-delivery)
for password/Entra identities and the tenant proof key.

Never rewrite shipped migrations or retained database history. Databases with pre-consolidation
provider migration history are not supported upgrade baselines: use a fresh disposable database for
verification, and preserve retained data before planning an explicit transition. Unsupported
schemas require an explicit transition or deliberately disposable replacement; no database or
migration-history rows are automatically reset.

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
| `20260910071000_AddSharedAcquisitions` | `AddAcquisitionContext` | Current required schema: acquisition browsing index and restricted `ChangeAcquisitionLink` EXECUTE. Readiness, provisioning and backup markers advance. Item-first then ordered acquisition locks check versions for atomic connection corrections. Retain identities, archived links and immutable creation evidence; removing the final link retains the acquisition. Verify fresh schema and upgrade from H9, including old creation replays after correction. | Always blocked; shared relationships require forward correction or guarded recovery. |

Product behavior, user-visible concurrency/retry rules and the shipped feature inventory belong in
[collection documentation](../collection.md). Provider retry/backoff behavior belongs in
[identity delivery and worker operations](blob-and-service-providers.md#identity-delivery-and-worker).
The [migration source](../../src/Workbench.Server/Persistence/Migrations) is authoritative for SQL.
