# Database migrations

Database migrations are an explicit, human-controlled deployment operation. A Workbench web
replica never migrates its database and never receives the setup, operator, or migrator credential.

## Item detail editing release

`20260907194500_AddItemDetailEditing` adds `Inventory.UpdateItemDetails` after the photograph
release. Its conditional update changes only name, notes, and descriptive location using the
expected rowversion under caller tenant isolation. Runtime principals receive EXECUTE permission;
direct item UPDATE/DELETE remains denied. Existing item identity, text, and photographs are retained.
It also adds tenant-qualified `Inventory.ItemCreationSnapshots`, captured once by the first edit
in the same transaction. Creation retries compare these immutable original fields and return the
current item. Runtime snapshot access is SELECT-only; direct writes are denied. Existing items
need no backfill. Down migration is deliberately disabled to preserve creation evidence;
an empty tenant-filtered view from the migrator is never treated as proof that deletion is safe.
The new application requires the new schema marker and command permission before reporting ready.

Apply this additive migration through the explicit migrator before releasing H4. Verify fresh
creation and upgrade from the previous schema with retained items and photos. Keep previous
migrations unchanged. Use a forward correction for recovery; an application rollback must account
for schema-readiness compatibility. Existing paired SQL/blob backup and restore procedures remain
authoritative; reverting binaries is not authorization to discard saved edits or collection data.
The snapshot addition is consolidated into this PR's development-only H4 migration; the base
schema migrations are unchanged. Disposable test databases are recreated. A retained installation
that applied an earlier development version needs a forward correction, not a rewritten history entry.

## Item photograph release

`20260907082353_AddItemPhotographs` follows the shipped collection notebook migration. It adds
the nullable current-photo pointer, tenant-qualified photo and operation history tables, RLS,
and `Inventory.SetItemPhoto`. Existing items retain all text and start without photographs.
The procedure changes only the photo pointer under caller tenant isolation and an expected
rowversion; ordinary runtime SQL remains denied direct UPDATE/DELETE on `Inventory.Items`.
New photo/history grants are included in principal provisioning and readiness checks.

Apply this one additive migration through the explicit migrator before releasing H2. The current
application refuses readiness on the H1 schema. Migration history and the paired-backup manifest
advance together. Upgrade verification includes H1 items with saved text, and fresh schema
verification includes cross-tenant restrictions and the item-qualified current-photo FK.

The down migration rejects destructive rollback. Preserve photos and operation history through a
forward correction or the paired SQL/blob restore process. An old application's schema-readiness
contract may refuse the new schema; reverting binaries alone is not an established rollback path.

## Collection notebook release

`20260907060000_AddCollectionNotebook` adds tenant-owned `Inventory.Items`, individual-object
constraints, chronological browsing and creation-request uniqueness, row-level security, and
restricted runtime access. It follows `20260907054000_AddProviderRetryDelay` without
rewriting that or any earlier migration. The matching application requires the collection schema
and effective SELECT/INSERT permissions before reporting ready; liveness remains independent.

During integration with the provider-retry release, the unmerged, development-only notebook
migration was reordered after that base migration. The provider-retry migration remains unchanged;
the notebook migration advances its readiness version marker and the backup manifest schema boundary.

Apply it through the explicit migrator procedure below before releasing the H1 web application.
The runtime can create and read individual objects; it cannot update or delete saved collection
rows. Request UUID uniqueness prevents retry or concurrent submission from duplicating an item.
Collection text remains in SQL and is included in ordinary database backups; H1 adds no blob data.

Upgrade verification must include the immediate prior provider-retry schema with retained tenant
and identity data, followed by a persisted collection create/read. The clean drill also creates
the new table and validates its constraints and tenant isolation using restricted principals.

The down migration deliberately refuses to delete collection records. Use a reviewed forward
correction or the established offline [restore and sanitation procedure](database-backup-restore.md).
Reverting application binaries is not permission to drop the table: preserve new records and
verify the older release's schema/readiness compatibility before an application-only rollback.

## Principal boundary

| Principal | Intended use | Must not be available to |
| --- | --- | --- |
| Setup/database owner | One-time database initialization and principal provisioning | Web containers, agents doing routine development, scheduled jobs |
| `workbench_migrator` | Applying and rolling back reviewed EF Core migrations | Web containers and application configuration |
| `workbench_operator` | Bootstrap, additional-tenant provisioning, and restore sanitation | Web containers and ordinary tenant users |
| `workbench_web` | Runtime queries and commands through the application | Migration or operator tooling |

Store production principal secrets in the deployment platform's secret facility. Deliver the
migrator secret only to a one-shot migration job, remove it when that job exits, and audit access to
it. Do not put connection strings or password files in source control, container layers, logs,
command history, agent prompts, or build artifacts. Rotate a principal immediately if its secret may
have crossed one of those boundaries.

The migrator necessarily has schema-change authority and can therefore alter controls enforced in
SQL. Treat it as a deployment control-plane identity: no interactive application use, no standing
mount in a web replica, short-lived delivery, separately authorized invocation, and credential
rotation independent of the web principal.

Tenant RLS also requires a distinct 32-byte proof key. Principal provisioning writes that key into
an owner-only SQL table; the web and operator roles are explicitly denied direct access. The same
value is delivered separately to the application workload, preferably as a read-only mounted secret
file. A web connection string by itself therefore cannot select an arbitrary tenant through
`SESSION_CONTEXT`. Keep the proof key separate from every database password, rotate both sides
together under drained traffic, and never expose the raw value in container environment inspection.

Development recovery-link generation is deliberately not granted to the operator role because it
returns a raw credential-reset capability for an existing user. It requires the local one-time
setup/database-owner connection, is never part of a production web or operator environment, and
writes only to an explicitly named new file. Remove that file immediately after use.

## Local one-time setup

Follow the [canonical setup guide](../setup.md) for generated credentials, SQL containment,
bootstrap, routine migrations, and existing-database precautions. The original identity baseline
has shipped; never rewrite shipped migrations or retained database history. Earlier unmerged
PR snapshots require an explicit transition or a deliberately disposable replacement.

For a non-development provisioning job, pass the Base64-encoded 32-byte value only through
`--tenant-context-proof-key-file`. After provisioning, remove that temporary file. Configure web
replicas with `WORKBENCH_TENANT_CONTEXT_PROOF_KEY_FILE` pointing to their read-only secret mount.

The blob/provider phase adds one migration, `20260905222755_AddBlobAndOperationalProviders`, after
the two established baseline migrations. It consolidates three development-only migrations from
earlier revisions of PR #23. Databases created by those earlier revisions must not be treated as an
upgrade baseline: use a fresh disposable database for verification, and preserve any retained data
before planning an explicit transition. No database or migration-history rows are automatically reset.

## Authoring and validating a migration

The deployment phase adds `20260906031109_AddDeploymentQueueTelemetry` after the shipped provider
schema. It adds aggregate worker telemetry and deployment readiness procedures with narrow execution
grants; it does not rewrite the baseline or change tenant rows. The current release requires this
migration before web readiness or worker activation. Upgrade verification includes the provider
release as its base. Application rollback to that release is allowed only after verifying its
readiness/schema compatibility; this migration's down path removes its additive procedures and
restores the prior readiness version without deleting durable data.

Keep migrations deterministic and reversible where SQL Server permits. Review generated SQL and
permission changes, especially RLS predicates, grants, denials, migration history, security tables,
and readiness procedures. Run all four drills against disposable real SQL Server databases:

```powershell
./scripts/verify-migrations.ps1 -Scenario Clean
./scripts/verify-migrations.ps1 -Scenario Upgrade
./scripts/verify-migrations.ps1 -Scenario ReversibleRollback
./scripts/verify-migrations.ps1 -Scenario RestoreRollback
./scripts/verify-database-permissions.ps1
```

The clean drill applies every migration to an empty database. For the initial database release,
Upgrade starts from `InitialSchema`; after the baseline ships, it starts from the previous supported
release. The historical `ReversibleRollback` scenario now verifies that blob metadata migrations
refuse a destructive down-migration; retained revisions and queued work require offline recovery.
Restore rollback
validates the restored-schema path and mandatory security sanitation. Permission probes exercise the
actual web, operator, and migrator roles.

## Deployment procedure

1. Identify the immutable application revision and its expected migration.
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

Application rollback is safe only within the documented schema compatibility window. If an older
binary is incompatible, do not improvise a down migration against live data; follow the reviewed
restore procedure instead.

## Additional tenant provisioning

Only an installation operator may create another tenant. Supply the operator connection and the new
administrator password in separate access-controlled files:

```powershell
Workbench.Database tenant create --connection-file <operator-path> --expected-database <name> `
  --tenant-name <tenant-name> --admin-email <email> --password-file <password-path>
```

The operator interface grants no general tenant-data browsing authority. Tenant administrators own
user management inside their tenant after provisioning.

## Invitation identity claims

`20260906092000_DeferInvitationIdentityClaim` releases global login claims held by
pending or cancelled credentialless users. Tenant user rows, roles, invitation tokens,
and delivery work remain intact. Accepted accounts, including disabled accounts with
passwords, retain their identities. Stop old web replicas before applying this migration:
only the matching application version claims identity during invitation consumption.
The application reports unready until its web principal can execute the required invitation
claim procedure. Missing procedure or revoked/denied execution authority keeps readiness
unhealthy while liveness remains available.
Rollback is blocked because restoring pre-acceptance claims could collide with identities
accepted since migration. Use a reviewed forward migration or the established offline
restore and sanitation procedure.

## Provider retry scheduling

`20260907054000_AddProviderRetryDelay` adds an optional bounded provider delay to
`Operations.RetryWork` without rewriting shipped migrations or pending work. Apply it before
starting the matching web and worker release. Web readiness remains unhealthy on the immediate
prior invitation schema until the new retry capability is available.

Graph `Retry-After` cannot shorten exponential backoff and is capped at one hour. Scheduling
remains in SQL, with the existing five-attempt limit, lease fencing, and terminal payload cleanup.
Down migration is blocked; use a reviewed forward correction or the offline restore procedure.
