# Database migrations

Database migrations are an explicit, human-controlled deployment operation. A Workbench web
replica never migrates its database and never receives the setup, operator, or migrator credential.

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
