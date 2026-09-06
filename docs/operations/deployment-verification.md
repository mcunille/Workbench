# Deployment verification record

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

## Pending hosted and installation acceptance

Compilation and local tests do not establish Azure resource deployability or hosted security.
Separately authorized validation must demonstrate real Entra SQL provisioning and RBAC, private
DNS/connectivity, Key Vault access and certificate rotation, the actual ACA proxy peer chain,
custom-domain public TLS, cold starts and scale-out, worker scheduling and races, SMTP delivery,
revision rollout/rollback, paired cloud recovery, and delivered alerts using real telemetry.
Backup-age and dead-letter monitoring still need their documented operational data sources.

The optional local-SQL production Compose profile was normalized but was not exercised as a complete
TLS installation. The runtime fixture used disposable SQL with test-only server-certificate trust;
it does not prove external SQL certificate validation. Public CA issuance and actual SMTP delivery
were not tested. Container session persistence does not substitute for a complete paired recovery drill.

The [cost worksheet](deployment-costs.md) uses retrieved public rates and synthetic inputs, not a
measured bill. Hosted latency, capacity, job lifetime, recovery, alert delivery, and billing evidence
must be attached before issue #12 or its specification can be marked complete.
