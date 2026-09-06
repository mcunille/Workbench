# Self-hosted deployment

**Readiness status:** This runbook is not yet a self-contained, verified production installation.
The [production operations audit](production-readiness.md) identifies missing provisioning,
rotation, monitoring, and recovery procedures. Passing the local smoke test does not close them.

This is the production Compose path for one web replica and one continuous worker using the same
release image. Only Caddy publishes ports 80/443; the app and SQL listeners remain on Docker bridges.
A production installation must pass the acceptance drill below. Static Compose checks do not establish
TLS, login, mail delivery, filesystem durability, or recovery readiness.

## Prepare the installation

Use a Linux Docker host, a public DNS hostname routed directly to that host, an approved image digest,
and either an external SQL Server or the optional local SQL profile. There must be no CDN, load balancer,
or additional proxy in front of Caddy with this configuration. If another hop is required, establish
and test its exact trust boundary first. Caddy obtains public certificates; DNS and inbound port 80/443
must work. Preserve the `proxy-data` and `proxy-config` volumes for certificate renewal state.

Copy `infra/compose/deployment.env.example` outside the checkout and fill in its non-secret values.
Keep the installation UUID, Compose project name, volume names, and blob root unchanged across releases.
Select an unused private subnet and a reserved IPv4 within it for `WORKBENCH_KNOWN_PROXY`; Compose assigns
that address to Caddy and the app trusts only that peer. Do not attach unrelated containers to these
networks. The worker joins only the dependency network. Caddy replaces forwarded client and scheme
headers, and removes forwarded host; the app uses the explicit public hostname. See the
[Caddy forwarding documentation](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy#headers).
IPv6 public clients can reach Caddy; its app-side peer in this topology is IPv4.

Create the following files in the protected absolute `WORKBENCH_SECRET_DIRECTORY`, outside source control:

| File | Contents / access |
| --- | --- |
| `web-connection` | Narrow web SQL connection; web only |
| `worker-connection` | Narrow worker SQL connection; worker only |
| `tenant-proof` | Existing base64 tenant context proof key |
| `data-protection.pfx` | Shared data-protection certificate with private key |
| `certificate-password` | PFX password, shared by web and worker |
| `smtp-password` | SMTP password; create an empty file when delivery is disabled |

Compose secrets are host file mounts, not an encrypted secret store. Restrict directory access to the
operator and give mounted files read access to runtime UID/GID 1654 without making them world-readable.
Confirm actual mount permissions on the target Linux host; Compose file-backed secret UID/mode fields
are not used to pretend host ownership changes. Never paste connection strings or passwords in shell
arguments, Compose environment settings, tickets, or logs. The app and worker receive no migration,
operator, setup, or maintenance connection. SQL connections must require encryption and certificate
validation (`Encrypt=True;TrustServerCertificate=False`), with bounded connection pools sized for both
workloads and release overlap. An organizational CA must be in the runtime trust store; build a reviewed
image with that CA when necessary rather than disabling certificate validation.

Bootstrap users/roles and run migrations explicitly following the
[database migration runbook](database-migrations.md). Use the release image's database tool at
`/opt/workbench/database/Workbench.Database.dll` for release operations, overriding the entrypoint to
`dotnet` and mounting only the relevant protected connection file. See the tool's command help and
runbook for exact role-specific arguments. Never run bootstrap or migration during web startup.

The initial empty blob named volume is populated from the image directory owned by UID 1654.
For a restored or pre-created volume, verify that owner and that the filesystem supports the durability
requirements in [blob and service providers](blob-and-service-providers.md). Keep one app replica.
An Azure blob override requires the existing system-assigned managed identity provider to be available
on the actual host; setting `WORKBENCH_STORAGE_PROVIDER=Azure` on an ordinary Docker host does not
provide an Azure identity. There is no SAS/connection-string shortcut. Shared filesystem replicas require
separate verified mount semantics and are outside this single-host configuration.

## Optional local SQL

Add **both** `-f compose.yaml -f infra/compose/local-sql.yaml --profile local-sql` to Compose invocations.
An explicit `-f` replaces automatic base-file discovery; passing only the SQL overlay omits the
application, worker, and proxy. Paths are relative to the first file; see
[Compose file merging](https://docs.docker.com/compose/how-tos/multiple-compose-files/merge/).
For example, after preparing the required SQL secrets and certificates:

```powershell
docker compose --env-file /srv/workbench/deployment.env `
  -f compose.yaml -f infra/compose/local-sql.yaml --profile local-sql config --quiet
docker compose --env-file /srv/workbench/deployment.env `
  -f compose.yaml -f infra/compose/local-sql.yaml --profile local-sql up -d sql
```

Retain the same base/overlay/profile arguments for subsequent `pull`, `up`, `stop`, and `ps` commands.
Do not start app/worker/proxy until database provisioning has completed.
Set a licensed production
`WORKBENCH_SQL_EDITION`; Developer edition is for disposable testing only. Prepare
`sql-bootstrap-password` and `sql-tls/server.pem` / `sql-tls/server.key` under the secret directory.
The TLS certificate must match the SQL DNS name used in connections (`sql` on the dependency network)
and chain to a CA trusted by the Workbench runtime. Make these files readable by SQL's non-root UID 10001,
with the private key restricted to that identity. The checked-in `mssql.conf` requires TLS 1.2.
The SQL bootstrap password enters only the SQL process environment from its mounted file; the web and
worker never mount it. Rotate and manage the administrative identity through a protected SQL session.
See [SQL container security](https://learn.microsoft.com/en-us/sql/linux/containers/security?view=sql-server-ver17).

Start SQL explicitly, wait for it to accept a validated TLS connection from an authorized administrative
tool on the dependency network, then bootstrap and migrate. SQL has no host port mapping. The optional
profile preserves data in `sql-data`; never remove it during a release. Plan SQL image patching and
licensed resource capacity independently of application updates. This profile has not been represented
as a managed backup service: configure and drill the existing SQL backup procedure.

## Validate and start

From the repository root, substitute your protected environment file path:

```powershell
./scripts/test-compose-proxy.ps1
./scripts/test-self-hosted.ps1 -EnvironmentFile /srv/workbench/deployment.env
docker compose --env-file /srv/workbench/deployment.env pull
docker compose --env-file /srv/workbench/deployment.env up -d app worker proxy
docker compose --env-file /srv/workbench/deployment.env ps
```

`test-self-hosted.ps1` requires an immutable SHA-256 application image reference, matching HTTPS
origin/host, and existing secret files. Do not skip this preflight. Caddy and optional SQL are version
pinned; record their resolved digests with release evidence and review updates. Do not dump normalized
configuration after adding any operator-specific overrides that could contain secrets.

Leave recovery and invitation disabled initially. Configure authenticated TLS SMTP using the example
settings. In an isolated acceptance installation with access restricted to the operator, enable
invitation, recreate app/worker, and verify an authenticated tenant administrator's invitation reaches
a controlled mailbox. The invitation flag must be enabled for that operation to enqueue mail. Disable
it after the drill unless that flow is intentionally enabled. Only after this evidence enable the
chosen flows in the publicly accessible installation. The worker is a supervised continuous process,
independent of HTTP traffic, with graceful shutdown. Its inherited HTTP healthcheck is disabled because
it has no HTTP listener; supervise process restarts and persisted queue age/dead letters separately.

The checked-in `test-self-hosted.ps1` validates only `compose.yaml`; it does not load the optional SQL
overlay or custom rotation overrides. `config --quiet` verifies merged syntax, not SQL certificates,
file ownership, connectivity, or readiness. Those require an isolated installation drill.

## Data-protection certificate rotation

Retaining old certificates on disk is insufficient. Both workloads must mount each retained PFX and
its password and configure `DataProtection__PreviousCertificates__0__Path` and
`DataProtection__PreviousCertificates__0__PasswordFile` (use consecutive indexes for more versions).
`DataProtection__PreviousCertificates__0__Format=Pfx` selects binary PFX; Azure's Base64 secret
representation is a separate format. The runtime loads these alongside the current certificate for
decryption; current secrets alone do not preserve old key access.

Before replacing the current PFX, prepare a protected Compose override for both app and worker with
those mounts/settings, preserving their existing secrets. Apply and verify that override while the
old certificate is still current. Then replace current certificate/password together and recreate
both workloads with the same override. Verify existing sessions and pending encrypted mail still
work, and drill restoration of a retained paired backup. Keep old certificates and override entries
until no retained keys, work, or backups depend on them. This required override and live rotation drill
are not yet supplied as an executable, verified installation procedure; see the audit.

## Acceptance and release evidence

Reserve the proxy address outside `WORKBENCH_INGRESS_DYNAMIC_RANGE` and inside
`WORKBENCH_INGRESS_SUBNET`. The defaults use `.2` for the proxy and `.128/25` for dynamically assigned
containers. Change both ranges together for a different subnet; otherwise Docker can allocate the
proxy's IP to another container before the proxy starts. The static preflight checks this boundary.

Record the image digest, schema, installation UUID, host platform, timestamp, and each outcome:

1. From a clean documented configuration, verify valid public TLS and HTTP-to-HTTPS redirect, successful
   readiness, and no host-published app/SQL listener. An unknown Host must not reach application handlers.
2. Log in with a controlled account; verify Secure cookies, authenticated navigation, logout and revoked
   session rejection. Send forged `X-Forwarded-For`, `X-Forwarded-Proto`, and `X-Forwarded-Host` headers;
   identity/rate limiting must still use the real client and canonical origin. Repeat after proxy/app
   replacement; inspect the socket peer privately to prove it remains the reserved proxy address.
3. Trigger recovery to a controlled mailbox, observe worker completion, and use the link once. Stop web
   traffic and confirm queued mail still delivers. Test SMTP failure/retry without recording mail tokens.
4. Create representative data and blobs, restart/recreate containers without deleting volumes, then
   verify login/session continuity and exact stored blob bytes through the authorized application flow.
5. Take and verify a paired SQL/blob/key checkpoint following the
   [backup/restore runbook](database-backup-restore.md). Stop web and workers while recording a consistent
   checkpoint. Restore to isolated targets, sanitize sessions/work, and verify schema and exact manifest
   before exposing any route. Retain all certificate versions needed to decrypt retained keys.
6. Rehearse release and rollback on disposable data. Stop ingress/writers, take the verified checkpoint,
   run the explicit migration, recreate web/worker with the candidate digest, then restore ingress only
   after readiness and authenticated checks. Revert the image only within the documented schema
   compatibility window; otherwise keep traffic stopped and perform the reviewed isolated restore.

Do not use `docker compose down --volumes` as a restart or rollback operation. This recipe has no
zero-downtime claim. The [local verification record](deployment-verification.md) covers disposable
SQL-backed Compose, validated internal-CA TLS, and session persistence after app replacement.
Public CA issuance, external SQL certificate validation, the optional local-SQL production profile,
SMTP delivery, and full installation recovery remain pending separately authorized acceptance.
