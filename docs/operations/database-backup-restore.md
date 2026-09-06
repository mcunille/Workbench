# Database backup and restore

Backups contain tenant data, password hashes, identity-operation hashes, audit history, and encrypted
data-protection keys. Handle a backup as highly sensitive production data: encrypt it, restrict and
audit access, keep it outside the application host and repository, define retention, and securely
dispose of expired copies. Never attach a backup or its credentials to an issue, log, or agent prompt.

Backup and restore cutovers are deliberately human-operated. The scripts require an explicit
database name, an access-controlled connection file, a SQL Server-visible path, and an exact typed
confirmation. They do not schedule operations or decide when production traffic may resume.

## Backup

Use a privileged backup credential that targets either `master` or the named database. The
destination is resolved by SQL Server, not the local PowerShell process.

```powershell
./scripts/backup-database.ps1 `
  -ConnectionFile <path> `
  -Database <name> `
  -Destination <sql-server-visible-backup-path> `
  -Confirmation 'BACKUP <name>'
```

The script requests a copy-only backup with checksum and fails on SQL errors. After completion,
verify the backup according to the database platform, record the application revision and schema
version, move it into encrypted protected storage, and remove the temporary connection file.

## Restore drill or recovery

Choose a target deliberately. A drill should use an isolated database and network. Before a real
recovery, drain application traffic and preserve the incident evidence and current database when
safe. The restore credential must target `master`.

```powershell
./scripts/restore-database.ps1 `
  -ConnectionFile <restore-path> `
  -Database <name> `
  -Source <sql-server-visible-backup-path> `
  -Confirmation 'RESTORE <name>'
```

The script forces the target into single-user mode, restores with replacement and recovery, creates
or updates an owner-controlled restore-pending marker inside the restored database, then returns it
to multi-user mode. This marker is independent of the restored readiness generation, so an older
backup cannot report ready merely because its historical generation values match. If SQL Server
reports a failure, keep traffic stopped and inspect the database state manually; do not assume the
final mode transition occurred.

## Mandatory post-restore sanitation

SQL recovery must be paired with the exact blob snapshot and digest manifest described in the
[provider runbook](blob-and-service-providers.md). Keep every replica and worker offline until both
stores are restored and `storage verify` succeeds. Sanitation cancels identity-delivery outbox rows,
resets outstanding deletion leases, and sets a separate blob-recovery marker when retained content
exists. SQL sanitation alone does not clear that marker or permit worker claims.

A restored cookie must never regain authority over rolled-back account, role, credential, or
revocation state. Before exposing readiness, apply the intended migrations and invoke sanitation
with a unique, non-secret correlation identifier:

```powershell
Workbench.Database migrate --connection-file <migrator-path> --expected-database <name>
Workbench.Database restore sanitize --connection-file <operator-path> `
  --expected-database <name> --correlation-id <recovery-id>
```

Sanitation runs transactionally. It deletes every durable session, pending invitation/recovery
operation, and persisted data-protection key; increments each user's security version; replaces each
security stamp; advances the database restore generation; and appends a system audit event. It is
idempotent in effect but creates a new security boundary on every intentional invocation.

The database readiness procedure fails closed while the restore marker is pending or when the
current restore generation has not been sanitized. Sanitation clears the marker in the same
transaction that invalidates authentication artifacts. Never bypass either check or reuse a
pre-restore key-ring copy.

## Validation before cutover

Run the following against the restored environment before a human authorizes traffic:

```powershell
./scripts/verify-migrations.ps1 -Scenario RestoreRollback
./scripts/verify-database-permissions.ps1
```

Also confirm:

- the expected migration is present and `/health/ready` succeeds through the web principal;
- liveness remains distinct from dependency readiness;
- every pre-restore browser session is rejected;
- pre-restore invitation and recovery links are rejected;
- a new sign-in creates a usable session with the expected tenant and permissions;
- representative tenant rows remain isolated through application and direct role probes;
- audit history contains the restore-sanitation event and correlation identifier; and
- any later blob restore is coherent with SQL metadata before blob workflows are enabled.

Record the drill date, source and target identifiers, application and schema versions, validation
results, exceptions, and the human who authorized cutover. Do not record credentials or token values.
