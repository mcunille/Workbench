# Deployment verification record

See the [production operations completeness audit](production-readiness.md) for historical
runbook gaps and current acceptance boundaries. Evidence is dated and scoped below; later Azure
launch evidence supersedes earlier statements that no hosted installation had been exercised.

Initial implementation evidence for issue #12, recorded on 2026-09-06 UTC. These checks used disposable
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

## Azure release correction evidence (2026-09-07 UTC)

The [release correction](../specs/azure-release-verification-fixes.md) was verified locally against
current main, including its invitation-claim and password-principal security fixes. The final
`scripts/verify.ps1 -SkipDependencyInstall` run passed locked restore, formatting, generated API
drift checks, release builds, 327 server tests, 18 client tests, six browser tests, all four migration
scenarios, and the published release check at temporary URL `http://127.0.0.1:60701`.
Dependencies had been installed with the locked commands in the preceding full run.

`scripts/smoke-container.ps1` rebuilt the corrected image and passed fresh SQL provisioning,
validated internal HTTPS, Secure-cookie login, session continuity after application replacement,
forwarding-header checks, a private application listener, and worker telemetry as UID 1654.
Its temporary application URL was `http://127.0.0.1:56137`; disposable resources were cleaned up.
The first smoke/source run exposed a missing entry in the strict password-provisioning grant
allowlist for the new readiness procedure. The specific grant and its authority assertions were
added before the final passing runs.

Focused failing tests preceded Graph delivery, durable retries, SQL manifest changes, bootstrap
cleanup, and the immediate-prior-schema readiness guard. Four selected manual Graph mutations
were killed: incorrect response acceptance, missing expiry validation, permanent classification
of token-service outages, and accepting an origin query. This was bounded manual mutation testing,
not a broad Stryker or SQL mutation run. Azure parameter/bootstrap tests, Compose configuration,
workflow command-boundary checks, and Bicep build/lint passed. Independent implementation review
findings were corrected, including explicit removal/readback of retained SMTP secret grants when
switching an existing installation to Graph.

These checks did not deploy Azure resources or submit live mail. The operator previously verified
Graph send acceptance and receipt, denial of personal-mailbox sending, and incoming no-reply
rejection using a bootstrap VM. Released Workbench worker delivery and the hosted gates below
remain unverified by this change.

## Remaining hosted acceptance

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

## Azure public launch (2026-09-08 UTC)

The operator approved public access after the hosted walkthrough, security remediation in PR #66,
and successful sign-in on the deployed candidate. This records a particular installation, not a
blanket certification of all deployment paths or a claim that every issue #12 checkbox passed.

- Serving source: `cc0e96996f8f655aa6a1c6b257b068862a8d892f`.
- Registry image: `wbprodafd5e7ff.azurecr.io/workbench@sha256:0e17b57ae2dc0c8be02df198f9a7f92d45444e0c6d7f247ba835541cde922eb3`.
- App `wb-prod-web`, revision `wb-prod-web--0000004`, 100% traffic; healthy readback.
  Revision `0000003` remains available for a compatible, separately approved rollback.
- `https://workbench.whitestagcollection.com/` and `/health/ready` returned 200 after promotion
  and public opening. Anonymous `/api/auth/me` returned 401. The operator confirmed sign-in.
- Live responses contained HSTS `max-age=31536000`, `X-Frame-Options: DENY`,
  `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, and CSP `frame-ancestors 'none'`.
- The explicit Public policy removed the 13 temporary operator/certificate-validation allow rules.
  HTTPS-only ingress, managed certificate binding and revision traffic were preserved. SQL, Blob
  and Key Vault readbacks continued to report public network access Disabled.
- The candidate initially returned readiness 503: the complete release included `AddItemRestoration`
  even though the security PR alone added no migration. Approved job `wb-prod-migration-mi0id95`
  succeeded at 07:26:35 UTC, logged the database migration success marker, and candidate health
  became Healthy. The serving old revision still returned 200. Future releases must compare the
  entire deployed-to-candidate range before scheduling changes.
- The worker retained its one-minute schedule and completed executions using the verified new
  digest. Earlier hosted checks delivered recovery email from the durable queue, verified the
  recovery link, rejected reuse, and invalidated the old session. Mail identity tests allowed only
  the no-reply mailbox, denied personal-mailbox sending, and confirmed incoming-message rejection.
- Worker-status-missing alert delivery was exercised by an approved schedule pause; the alert fired,
  scheduled execution resumed, fresh queue status arrived, and the alert resolved. Security-change
  notifications arrived for approved configuration changes. Multiple emails represented separate
  administrative events, including identically named diagnostic settings on different resources.
- All six resource audit routes read back the intended enabled categories and workspace. Activity
  and Blob records were observed. SQL, Key Vault, ACR and native-backup audit-table ingestion were
  not yet confirmed in the final monitoring query; configuration alone is not ingestion evidence.
- SQL retained seven-day backup retention and seven-day logical-server soft delete. SQL and native
  backup vault deletion locks were applied. The native vault read back GeoRedundant, immutability
  Locked, soft delete AlwaysOn with 14 days, and a seven-day VaultStore policy. These are distinct
  retention controls. Logical-server undelete and customer-initiated cross-region Blob recovery
  were not tested.
- Native backup job `85d46a43-edea-4ec9-a838-bca0ae7e5c79` completed at 05:11:13 UTC; isolated
  restore job `e888960b-9512-46f3-af44-60fe714c3c5b` completed at 05:53:11 UTC. The operator
  accepted the empty-data recovery drill. Earlier SQL PITR, restore sanitation and paired empty
  storage verification passed. This does not establish nonempty attachment recovery or a full
  cross-region application RTO. Recovery remains manual; normal backups need no application outage.
- Downloaded password-manager recovery attachments passed hash checks and authenticated decryption,
  including installation metadata, four recovery values and the certificate private key, without
  the original Windows-protected key or original recovery files. No secrets are recorded here.
- The exact candidate scan detected zero High/Critical advisories, five Medium and seven Low
  package findings. These include Ubuntu/OpenSSL advisories remaining in the upstream runtime;
  this is not a clean-image or complete security-audit verdict.
- Approved cleanup deleted restore target `wbrestoreafd5e7ff0908` and its temporary backup role,
  deleted the unbound `workbench-prod-bootstrap` certificate, and deactivated revision `0000002`.
  Readback confirmed HTTPS 200 and production backup ProtectionConfigured. The temporary VM,
  NAT, subnet and SQL restore infrastructure were already absent from the live inventory.

Remaining evidence for the broader architecture/deployment issues: measured hosted cold-start
samples, multi-replica session/rate-limit tests, an exercised compatible traffic rollback, complete
monitoring ingestion/failure coverage, and measured cost/App Service comparison. The monthly
budget is USD 100 with notifications, not a spending cap. Log Analytics retention is configured
for 30 days; platform Activity Log retention is separate. Public Linux self-host acceptance is not
established by the completed Windows localhost QA drill. Track those limits explicitly rather than
checking every epic criterion because the public launch succeeded.
