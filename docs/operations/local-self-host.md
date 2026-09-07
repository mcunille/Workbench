# Local self-host on Windows

This installs a retained, localhost-only QA service on Windows with PowerShell 7.4 or later and Docker Desktop's
WSL2 Linux-container engine. It automates the manual 2026-09-06 self-host walkthrough. It is separate
from the disposable development fixture in [setup](../setup.md) and the public-host procedure in
[self-hosted deployment](self-hosted-deployment.md). The [verification record](deployment-verification.md)
states what the manual drill established and what remains unverified.

## Inputs and prerequisites

Install PowerShell 7.4 or later, Git, and Docker Desktop configured for Linux containers on a compatible amd64
host. Start Docker before running setup. The application builds inside Docker; a host .NET SDK and
Node installation are not needed for this path. Allow space for the application build, SQL data,
uploaded files, and backups. The selected loopback ports (defaults `127.0.0.1:80` and
`127.0.0.1:443`) must be available. Do not stop
unrelated services to free them without establishing who owns them.

Run from the repository root in a normal PowerShell 7.4 or later window:

```powershell
./scripts/setup-local-self-host.ps1 `
    -TenantName 'Workbench QA' `
    -AdminEmail 'qa-admin@example.test' `
    -SourceRef main `
    -TrustLocalCertificate
```

`SourceRef` defaults to `HEAD`. It must resolve in the local repository; fetch the desired release
first when necessary. Setup resolves it to a commit and builds a Git archive of that commit, excluding
uncommitted files and ignored development credentials. Use a reviewed release commit for repeatable
installation. The script records the resolved application image and dependency digests.

The tenant name and administrator email are the only required settings. Alternatively, place these
non-secret settings in a JSON file outside the checkout:
Copy [the example configuration](../../infra/compose/local-self-host.example.json) or use:

```json
{
  "TenantName": "Workbench QA",
  "AdminEmail": "qa-admin@example.test",
  "SourceRef": "main",
  "TrustLocalCertificate": true
}
```

```powershell
./scripts/setup-local-self-host.ps1 -ConfigurationFile C:/WorkbenchSetup/qa.json
```

`InstallationRoot` can also be supplied as a parameter or JSON property. Its default is
`$env:LOCALAPPDATA/WorkbenchSelfHost`. Choose a private local directory outside source control and
shared/synced folders. Setup requires a fresh directory; it refuses an existing installation root.
Do not point it at the QA installation from the manual drill.

Optional `HttpPort` and `HttpsPort` parameters or JSON properties select different loopback ports for
a side-by-side installation. Use distinct roots and ports; the Compose project name is derived from
the installation path, isolating its networks and volumes. The public origin is derived from
`localhost` and the selected HTTPS port. For example, `-HttpPort 8081 -HttpsPort 8443` uses
`https://localhost:8443`.
Use separate browser profiles for side-by-side installations: cookies are scoped to the hostname,
not the port, so both installations otherwise share cookie names in the browser.

`TrustLocalCertificate` explicitly authorizes importing Caddy's generated public CA certificate into
the current Windows user's trusted root store. This grants that CA trust for this user, not just one
browser tab. Setup does not install machine-wide trust. Without the switch or JSON setting, follow the
script's certificate-trust instruction before expecting the browser or HTTPS readiness request to
validate. The recorded setup status is `AwaitingWindowsTrust`; manually importing the certificate
and verifying HTTPS does not rewrite that historical status. It is not an HTTPS verification pass.
Do not bypass certificate errors with `-SkipCertificateCheck`.

## What setup performs

The script generates a stable installation ID and distinct random credentials; no password input is
required. It protects the secrets directory for the current Windows account and SYSTEM. File ACLs
restrict access but do not encrypt the files; Docker administrators remain trusted administrators.

Setup performs these operations in order:

1. Validate prerequisites and fresh-install resource ownership, archive the selected commit, build the
   release image, and pin the images used by this installation.
2. Generate the SQL CA and hostname-valid `sql` certificate, protected data-protection PFX, tenant proof
   key, separate database credentials, and initial administrator password.
3. Configure validated SQL TLS with the CA bundle and explicit `SSL_CERT_FILE`, prepare SQL TLS volume
   ownership for UID 10001, and start isolated SQL Express without a published host port.
4. Wait for a certificate-validated encrypted SQL connection, create the contained database, migrate,
   provision web/operator/migrator/worker/maintenance principals, and bootstrap the tenant and admin.
5. Start the app and wait for readiness before starting the worker. This lets the app establish the
   shared data-protection key ring before the worker attempts to read it.
6. Start Caddy with only loopback ports published, export its public root certificate, and establish
   current-user trust only when requested. Verify HTTPS when trust is available.

SQL TLS, application data protection, and browser HTTPS use separate certificates. The SQL TLS helper
sets file permissions before restricting directory traversal; SQL receives only the capability needed
by its executable while retaining the remaining capability restrictions. Application and worker
containers receive their own narrow credentials, not setup/migration/operator authority.

This profile uses SQL Express and a single web replica with a continuous worker. Email delivery,
public invitations, and public account recovery remain disabled. The administrator email is a login
identifier; setup does not send mail. The SQL and app listeners are private to Docker networks.

## Verify the installation

After successful setup, open [https://localhost](https://localhost) and confirm there is no certificate
warning. Retrieve the generated password privately, for example with the clipboard:

```powershell
$installRoot = Join-Path $env:LOCALAPPDATA 'WorkbenchSelfHost'
Get-Content -Raw (Join-Path $installRoot 'secrets/admin-password') | Set-Clipboard
```

Use your configured administrator email to sign in. Check the tenant name, refresh an authenticated
page, and sign out. Clear the clipboard with `Set-Clipboard -Value ''`. Substitute your installation
root above if you selected a different one. Never paste secret files into reports or chats.

The generated standalone `compose.json` in the protected installation root and its named volumes are
retained installation state. It contains the complete configuration for this installation; do not
combine it with the manual drill's environment file/overlays. Preserve the installation
ID, image identifiers, configuration, passwords, SQL CA, data-protection PFX, and Caddy CA state. Do not
use `docker compose down --volumes` to restart or repair this service.

## Failures and subsequent operations

### Update an existing installation

See the [update verification record](local-self-host-update-verification.md) for exercised
upgrade paths and the limits of that evidence.

Use PowerShell 7.4 or later from an updated repository checkout. Fetch the desired
release first, then explicitly select its reviewed commit or ref:

```powershell
git fetch origin
./scripts/update-local-self-host.ps1 `
    -SourceRef origin/main `
    -Confirmation 'UPDATE Workbench'
```

For a nondefault installation, add `-InstallationRoot C:/WorkbenchSetup/qa` using
its original path. The ref must be the installed commit or a descendant; downgrades
and divergent release histories need a reviewed transition. This command supports
installations generated by setup, including those created before the updater existed.
It rejects incomplete setup/update state and unsupported configuration changes.
First establish current-user certificate trust and successful HTTPS readiness as
described above. Close other installation operations and use a clean shell without
deployment environment overrides.

The command builds the committed application before downtime. It then stops proxy,
app, and worker; takes a SQL COPY_ONLY/checksum backup with VERIFYONLY; snapshots
retained blobs with the installed release's maintenance tool; and saves the paired
manifest, configuration, credentials, and certificate state in a protected checkpoint.
It applies migrations using the migrator credential, starts app and verifies readiness,
tests the worker, then starts continuous worker and ingress and verifies trusted HTTPS.
SQL and Caddy images, ports, credentials, certificates, and volumes remain unchanged.
Do not run setup again to update an installation.

Successful output gives the local URL and checkpoint directory beneath
`<InstallationRoot>/updates/<release>/checkpoint`. Sign in, inspect retained data,
refresh an authenticated page, and sign out. `update.json` records the installed
release and result; `installation.json` remains the historical setup record. Previous
Compose files, release images, source archives, and checkpoint files are retained.
Updates do not prune them automatically.

Blob snapshots first use a Linux Docker volume because Windows bind mounts do not
support the storage tool's atomic publication operation. The updater exports the
snapshot to the protected Windows checkpoint and verifies every copied blob's length
and SHA-256 against the manifest. The journal and catalog record the retained
`SnapshotVolume`; this volume is also preserved for recovery and is not pruned automatically.

Checkpoint files contain sensitive data and keys. Their ACL is restricted to the
current Windows account and SYSTEM; ACL protection is not encryption. Move a copy
to encrypted, access-controlled off-host storage under your backup policy. The catalog
pairs SQL with its exact blob manifest and file hashes. VERIFYONLY and blob checks
do not establish a successful restore drill or survival of host loss.

### Update failures

Build and preflight failures leave the running installation unchanged. After downtime
begins, failures attempt to keep ingress and writers stopped. The error and `update.json`
report the failed phase and whether stopped workloads were verified. If this value is
false or absent after interruption, inspect actual container state before proceeding.
An incomplete journal blocks another update; the file lock alone does not authorize retry.

- Before `migration`, preserve the journal and partial checkpoint, resolve the reported
  problem, and verify that the previous Compose file/image and database remain unchanged.
  Only then perform a reviewed recovery to the previous running state.
- At `migration` or later, assume database changes may have committed. Keep writers
  stopped and choose a reviewed forward correction or the existing
  [paired restore and sanitation procedure](database-backup-restore.md). An old image
  or successful SQL backup is not permission to down-migrate or restart an incompatible app.
- Preserve `<release>/previous-compose.json`, any `previous-update.json`, and the checkpoint.
  Do not delete `update.json` merely to bypass its recovery guard. Reconcile the journal
  with the verified recovered release as part of the reviewed recovery procedure.

An interrupted `Running` journal's `Phase` is the last operation about to execute;
a `Failed` journal additionally retains `FailedPhase`. Treat `migration` as possibly
applied even if no completion message was printed. Restoration, automatic resume,
dependency-image upgrades, and certificate rotation are separate operations.

### Setup failures

Setup stops at a failed check. It does not automatically resume a partially provisioned installation,
overwrite credentials, or roll back databases and volumes. Keep its output and inspect the failed step
without printing secret files. Earlier completed resources may remain; an existing root is not proof
of success. Preserve retained data and establish which resources belong to this attempt before any
cleanup. Rerunning setup is not an upgrade, repair, or restore procedure.

Backups and restoration follow the [paired backup runbook](database-backup-restore.md). Initial setup
does not schedule backups, install monitoring, automate rotation, or certify production
readiness. A local backup is useful for a restore rehearsal but cannot survive loss of the host.
The manual QA drill restored an empty blob store; it did not verify restored browser authentication,
old-session rejection, or real attachment recovery. See the [production audit](production-readiness.md)
before adapting this installation to production or making it reachable from another computer.
