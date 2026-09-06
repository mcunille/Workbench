# Production operations completeness audit

**Result: not ready for acceptance.** Audited on 2026-09-06 for issue #17 / PR #29 against the
deployment implementation introduced in `a9105b5`. Neither the self-hosted nor Azure runbook can
currently be followed from its stated inputs to a verified secure production service without
inventing operational steps. This is an operations/documentation audit, not a complete security
assessment or evidence of a live production installation.

Two independent read-only reviews covered the Azure and Compose paths. Findings were checked against
the database CLI, provisioning/security code, Compose/Bicep configuration, and verification scripts.
The local setup, source suite, and container smoke evidence in PR #29 remains valid for its stated
scope. It does not establish public TLS, real SMTP, hosted identity/trust, installed monitoring,
certificate rotation, or full recovery on either production path.

## Follow-up: localhost QA installation

The operator completed a Windows Docker Desktop/WSL2 localhost QA installation and an isolated core
restore drill on 2026-09-06. The [verification record](deployment-verification.md#windows-localhost-qa-drill-2026-09-06)
records immutable images, passing checks, manual interventions, and limits. The
[local self-host script](local-self-host.md) now packages fresh installation with tenant/admin inputs,
protected generated credentials, SQL TLS, all five principal roles, and ordered workload startup.
It is a localhost-only QA path, not the public Linux or Azure acceptance procedure.

The manual drill closes the question of whether that selected local configuration can start and
recover its database. A subsequent isolated automated installation passed the checks recorded in
the [automation evidence](deployment-verification.md#automated-local-installer-verification).
Neither drill establishes Linux host
ownership/trust, public ingress, monitoring/scheduled backups, rotation, or full recovery coverage.
The backup had zero blob revisions; restored sign-in and rejection of old sessions were explicitly
left untested when the operator accepted the QA drill. The historical gaps below remain production
requirements except where the new local path supplies a scoped implementation and recorded evidence.

Local automation incorporates these corrections found during the walkthrough:

| Manual failure | Required installation behavior |
| --- | --- |
| SQL executable would not start with all capabilities removed | Retain only `NET_BIND_SERVICE` needed by the selected SQL image while keeping the other restrictions. |
| TLS helper lost directory traversal before setting file modes | Set restricted file modes/ownership before restricting the TLS directory. |
| Mounted CA bundle alone did not select the expected OpenSSL trust source | Set `SSL_CERT_FILE` for SQL, application, worker, and relevant one-shot tools, and test validated TLS. |
| Worker attempted to read a key ring before first app initialization | Wait for app readiness before starting the worker and proxy. |

Setup deliberately refuses an existing installation root and does not implement automatic partial-run
resumption, upgrades, backup scheduling, or certificate rotation. Those must not be inferred from a
successful initial installation.

## Corrections made in this change

| Defect | Correction |
| --- | --- |
| SQL overlay invocation omitted the base Compose file | Show `-f compose.yaml -f infra/compose/local-sql.yaml --profile local-sql`; using only the overlay drops the app/worker/proxy. |
| Azure initial schema/admin steps were implicit | Show explicit setup migration, five-role Entra provisioning, and operator tenant/admin bootstrap in order, with failure checks and distinct authentication. |
| Azure restore marking assigned to the wrong principal | Require setup/restore authority for `Administration.MarkRestorePending`; operator is explicitly denied it. Sanitation remains an operator operation. |
| Restore guide represented disposable regression tests as installed-target validation | Label the scripts correctly and require separate observations against the restored installation. |
| Restore script limitations were omitted | State existing-target and physical-path constraints; it has no new-target or `MOVE` support and is not an Azure PITR tool. |
| Compose preflight and certificate retention coverage were overstated or implicit | State base-only preflight coverage and the need to mount/configure previous certificates in both workloads. |

## Outstanding work before either path is self-contained

Each row is an acceptance blocker, not an optional improvement. A procedure may use linked repository
runbooks, but those links must end in executable instructions with explicit inputs, privileges,
failure handling, retained state, and measured acceptance. “Use a protected session” or “configure
monitoring” alone does not meet that requirement.

| Area | Self-hosted requirement | Azure requirement |
| --- | --- | --- |
| Release and prerequisites | Commands to build/publish or obtain the reviewed immutable image, authenticate the host, and provision the selected SQL TLS trust chain. | Commands for registry/pull identity, image, private administrative host/DNS access, and narrowly scoped bootstrap vault permissions. |
| Secrets and keys | Protected generation/import of proof, all five SQL principals' secrets, administrator password and PFX; Linux ownership/readability; distinct TLS and data-protection certificate lifecycles. | Protected generation/import and recovery copies, vault file uploads, explicit operator/maintenance authentication, and DNS-validated bootstrap TLS certificate creation/upload. |
| SQL and first login | Executable contained database initialization, worker and maintenance user creation, one-shot release-tool mounts/networking, bootstrap, and runtime login. Current password CLI provisions only web/operator/migrator. | Exercise the corrected initial SQL sequence using actual Entra identities, then the separate migration job and runtime administrator login. |
| Trusted ingress | Exercise direct Caddy topology with public TLS, validated SQL TLS, spoofed headers, persistence, and certificate renewal. | Supply an isolated diagnostic workload/procedure to observe immediate peers before activation; inactive web has no ingress while activation requires explicit trust. Prove trust after replica replacement. |
| Worker and mail | Configure actual authenticated SMTP, supervised execution, retries and controlled invitation/recovery delivery. | Execute manual/scheduled jobs, inspect terminal and durable outcomes, test overlap/retries and actual SMTP delivery. |
| Monitoring and backups | Select/configure a protected monitor, schedule paired backups, catalog only verified successful checkpoints, alert on failure/age/dead letters, and prove notification delivery. | Implement or integrate the missing paired-checkpoint success source and dead-letter source, schedules, least-privilege reads, alerts, and injected-failure notification evidence. Current Bicep does not supply these sources. |
| Release and rotation | Supply/test previous-certificate Compose override, complete update/rollback commands, and retained-secret recovery across app/worker recreation. | Concrete job/revision discovery, start/status checks, candidate routing, traffic pinning, drain/rollback commands, certificate transitions, and failure-safe resumption. |
| Isolated recovery | SQL-visible backup/export locations, exact volume copy/mount procedure, existing-target/path preparation or reviewed restore extension, sanitation, digest verification, and runtime acceptance. | Actual PITR command path, verified paired blob versions/catalog, marker/sanitation, source-binding reconstruction at isolated locations, maintenance grants, and measured recovery. SQL Server `COPY_ONLY` instructions are not an Azure SQL backup procedure. |

Current sources establishing the implementation limits:

- [Password principal provisioning](../../src/Workbench.Database/Program.cs) creates three roles' users;
  [Entra provisioning](../../src/Workbench.Server/Administration/EntraPrincipalProvisioning.cs) requires
  exactly five distinct identities and an existing migrated schema.
- [Compose](../../compose.yaml) mounts only the current data-protection certificate;
  [DeploymentSecrets](../../src/Workbench.Server/Security/DeploymentSecrets.cs) supports explicitly
  configured previous certificates. [Preflight](../../scripts/test-self-hosted.ps1) loads only the base file.
- [Azure workloads](../../infra/azure/modules/workloads.bicep) omit ingress/configuration when inactive;
  [parameter validation](../../infra/azure/validate-parameters.ps1) requires explicit trust on activation.
- [Azure monitoring runbook](azure-deployment.md#monitoring-costs-and-hosted-evidence) identifies missing
  checkpoint/dead-letter sources; [deployment evidence](deployment-verification.md) records pending live drills.
- [Restore script](../../scripts/restore-database.ps1) and
  [security migration](../../src/Workbench.Server/Persistence/Migrations/20260904061246_EstablishSecurityBoundaries.cs)
  establish restore-path and role restrictions. [Permission verification](../../scripts/verify-database-permissions.ps1)
  and [migration verification](../../scripts/verify-migrations.ps1) run disposable tests.

## Completion gate

Finish and review the missing operational implementation/procedures before an installation drill.
Retain the accepted application architecture; design decisions still needed include the protected
administrative/diagnostic environment, monitoring integration, and concrete backup/restore automation.
Do not broaden proxy trust, grant runtime setup authority, disable certificate validation, or silently
omit recovery/alerts to complete a walkthrough.

Use separately authorized isolated Linux and Azure installations with controlled DNS, SQL trust,
SMTP destination, private administrative access, backup destination, and alert recipients. Follow
only the repository instructions from a clean starting point; record every undocumented intervention
as a failed step and correct/retest it. Stop at a failed gate rather than expose an incomplete service.

Acceptance must demonstrate first login, least-privilege runtime/worker access, TLS and proxy boundaries,
mail delivery/retry, dependency failure/readiness, durable restart and release behavior, certificate
rotation, scheduled paired backups with delivered alerts, and isolated recovery with revoked old
sessions and exact blob digests. Record image/schema/configuration identifiers, UTC timestamps,
measured recovery time/data loss and protected artifact references without secrets or tenant data.
Only after both procedures and those environment-specific results pass can the corresponding
installation be described as production ready. The current PR does not satisfy that expanded gate.
