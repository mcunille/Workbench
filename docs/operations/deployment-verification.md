# Deployment verification record

See the [production operations completeness audit](production-readiness.md) for subsequently
identified runbook and tooling gaps. The local evidence below does not close those acceptance blockers.

Implementation evidence for issue #12, recorded on 2026-09-06 UTC. These checks used disposable
local resources. No Azure deployment or production operation was performed. The accepted deployment
specification and issue remain open for hosted acceptance.

## Local evidence

- `scripts/verify.ps1 -SkipDependencyInstall` passed locked restore, formatting, API contract
  generation, release builds, 230 server tests, 18 client tests, six browser tests, database creation,
  upgrade/rollback and restore checks, and the published application smoke check. Subsequent proxy
  cases increased the server suite to 234 tests; all 234 passed in the final current-source run.
- `scripts/smoke-container.ps1` rebuilt the final application image and passed the hardened SQL-backed
  runtime checks as non-root user 1654. Its disposable Compose fixture exercised the checked-in
  services and proxy configuration with a locally issued CA certificate whose trust was validated.
  SQL readiness, Secure-cookie login, session continuity after app replacement, forged forwarding
  headers, unknown-host rejection, private app listeners, and worker queue telemetry without restarts
  passed. The temporary HTTP smoke URL was `http://127.0.0.1:54666`; the fixture was cleaned up.
- Bicep 0.46.1 compiled and linted the infrastructure. Parameter tests covered valid IPv4/IPv6 trust,
  broad and mapped-address rejection, and canonical HTTPS port constraints. Compose normalization,
  deployment preflight, proxy address reservation, and workflow action pinning checks passed.
- New behavior was developed with focused failing tests before implementation. Real SQL tests cover
  two independent hosts sharing sessions and abuse controls, concurrent migration lock contention,
  cancelled migration recovery, least-privilege queue aggregates, and deployment readiness.
- Nine selected manual source mutations were killed: three worker-drain changes and six security
  changes. The trusted-hop assertions were strengthened when mutation evidence exposed a gap.
  This was bounded mutation assessment; no broad Stryker or SQL mutation run was performed.
- Independent implementation and security reviews covered the source snapshot and subsequent fixes.
  Reported findings were corrected and reviewed again, including certificate bootstrap ordering,
  custom-origin port validation, invitation bootstrap sequencing, proxy IP allocation, unknown-host
  handling, and IPv4-mapped IPv6 trust boundaries. No formal Codex Security scan was run.

## Windows localhost QA drill (2026-09-06)

An operator completed a manual retained QA installation using Windows, Docker Desktop with WSL2,
SQL Express, one app, one continuous worker, and Caddy at `https://localhost`. The subsequent
[local setup automation](local-self-host.md) incorporates the manual steps; this record does not
represent a successful clean installation using that new automation.

Release identifiers:

- Source commit: `cca9043bd3f5933b59d097059ccfa91719f466ca`.
- Application image: `sha256:0595ae9469c5cbbc06a175042489ac3462f667efaf52cbf0304f77a8daf83827`.
- SQL image: `mcr.microsoft.com/mssql/server@sha256:7c29dfbac885ad7519e219c7fe4aee0e67283e21a10e9c252d13b0fbde1866f8`.
- Caddy image: `caddy@sha256:4c6e91c6ed0e2fa03efd5b44747b625fec79bc9cd06ac5235a779726618e530d`.

Observed outcomes:

- SQL hostname/certificate-chain validation passed with encryption reported `TRUE`; certificate
  verification was not bypassed. The database was contained, migrated, provisioned, and bootstrapped.
- Only Caddy published ports, both bound to `127.0.0.1`. Current-user Windows trust of its public CA
  enabled HTTPS readiness `200` with normal validation. Browser login, refresh/session persistence,
  logout, and service restart recovery passed.
- With writers stopped, SQL `COPY_ONLY` backup with checksums and `RESTORE VERIFYONLY`, the paired
  blob snapshot, recovery credentials/certificates/configuration, and local image archive were saved
  in a Windows ACL-protected local folder. All 42 cataloged file checksums passed.
- A separate Docker network and SQL/blob volumes were used for recovery. SQL restore and
  `DBCC CHECKDB`, restore-pending marking, migration check, operator sanitation, paired manifest
  verification, recovered app readiness, and one recovered worker iteration passed.
- The operator accepted the QA drill as complete. The original QA service remained healthy and
  restore containers were stopped with their data retained.

Undocumented interventions found during the manual run were SQL executable capability requirements,
TLS volume permission ordering, explicit OpenSSL CA-bundle selection, and app-before-worker
data-protection initialization. These are installation requirements, not optional troubleshooting.

Limits: the snapshot contained zero blob revisions because no attachment UI workflow was available.
Recovered browser sign-in and rejection of pre-recovery sessions were not tested. The backup was
local, not encrypted by the backup commands, and not a scheduled off-host backup. No measured recovery
objective, certificate rotation, update/rollback, host reboot, alert delivery, real SMTP, public TLS,
or Azure deployment was established. This is QA evidence, not production-readiness certification.

## Automated local installer verification

On 2026-09-06, the new installer completed a fresh isolated installation with separate loopback
ports, a separate Compose project, and the archived main commit above. SQL certificate validation,
all five database principals, bootstrap, app readiness before worker startup, and a worker iteration
passed without manual provisioning. HTTPS readiness and administrator login plus authenticated
identity retrieval passed against the generated Caddy CA. The HTTP client used the exported CA;
Windows trust was not changed by this automated test. Local-CA revocation availability was treated
as best-effort while certificate-chain and hostname validation remained enabled.

PowerShell configuration and simulated-Docker orchestration checks cover input rejection, TLS
connection quoting, retained-resource refusal, role-specific mounts, loopback ports, startup order,
and stopping public workloads after an injected worker failure. Two targeted manual mutations
(missing tenant validation and disabled SQL certificate validation) were killed. Azure parameter
and Compose proxy contract checks passed. Independent review findings were corrected and reviewed
again. The Windows CI job repeats the offline installer checks; it does not claim a live Docker drill.

An initial tool-run installation outside the workspace could not share newly generated files with
Docker Desktop, although PowerShell saw them. The live test succeeded from an ignored workspace
directory visible to Docker. Setup now checks host-file sharing before generating credentials.
The prior user-operated retained installation was not changed. These automated checks do not extend
the recovery or production claims of the manual drill.

## Pending hosted and installation acceptance

Compilation and local tests do not establish Azure resource deployability or hosted security.
Separately authorized validation must demonstrate real Entra SQL provisioning and RBAC, private
DNS/connectivity, Key Vault access and certificate rotation, the actual ACA proxy peer chain,
custom-domain public TLS, cold starts and scale-out, worker scheduling and races, SMTP delivery,
revision rollout/rollback, paired cloud recovery, and delivered alerts using real telemetry.
Backup-age and dead-letter monitoring still need their documented operational data sources.

The earlier disposable runtime fixture does not prove external SQL certificate validation. The later
Windows localhost drill exercised private-CA validation for the optional local SQL profile, with the
manual corrections recorded above. Public CA issuance and actual SMTP delivery were not tested.
Neither container session persistence nor the empty-blob recovery drill substitutes for full recovery
acceptance with representative data and restored authentication checks.

The [cost worksheet](deployment-costs.md) uses retrieved public rates and synthetic inputs, not a
measured bill. Hosted latency, capacity, job lifetime, recovery, alert delivery, and billing evidence
must be attached before issue #12 or its specification can be marked complete.
