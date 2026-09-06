# Azure deployment and portable self-hosting

**Status:** Accepted

Tracks [issue #12](https://github.com/mcunille/Workbench/issues/12), following the implemented
blob and operational providers phase (#11). The user accepted this design on 2026-09-05. Acceptance does not authorize provisioning Azure resources, deployment, DNS changes,
production operations, or merging. Live acceptance evidence remains outstanding until a separately
authorized disposable environment is available.

## Problem and scope

Operators need a reproducible hosted deployment with idle web compute savings and a supported
self-hosted path. Both must preserve the same image, SQL authority, identity, tenant isolation,
blob contracts, and durable worker semantics. This phase adds deployment infrastructure, configuration
validation, operational tooling, and evidence; it adds no domain features or service decomposition.

The existing Dockerfile publishes the web host and database tool into one non-root runtime image.
SQL stores sessions, protection keys, shared rate limits, and leased work. Azure blobs already use
system-assigned managed identity. Migrations are explicit through `DatabaseMigrator` and the database
CLI. The prerequisite issue is closed and its implementation is present at base commit `c837cfb`.

Remaining gaps observed in source:

- `Program.cs` accepts one configured proxy IP, adds it to framework defaults, and consumes one hop.
  Production validation does not require a canonical origin or a public-host allowlist globally.
- `WorkerHost --once` attempts one item; it is not a bounded batch suitable for scheduled jobs.
- `compose.yaml` provides the app and blob volume, but does not describe a complete TLS/proxy,
  public-origin, SMTP, worker, and secret-file deployment.
- No Bicep or hosted deployment harness exists. EF migration coordination requires an actual
  concurrent-run check; a single job replica alone does not prevent overlapping executions.
- SQL Server backup scripts are not Azure SQL point-in-time recovery procedures.

## Proposed topology

Use Bicep modules and non-secret environment parameter files under `infra/azure/`. Parameters include
region, resource names, stable installation UUID, image digest, public origin, resource sizes,
replica limits, job schedule, retention, and existing secret resource identifiers. Reject mutable
image tags for release deployment. Export resource identifiers and public endpoints only.

| Boundary | Proposed hosted default |
| --- | --- |
| Web | Container Apps Consumption, 0.5 vCPU / 1 GiB, minimum 0, maximum 3 replicas, HTTP concurrency target 20 |
| SQL | Azure SQL single database, provisioned small SKU selected in environment configuration; private endpoint |
| Blobs | Private Azure Blob container, managed identity, versioning and soft deletion; private endpoint |
| Network | Dedicated VNet-integrated Container Apps environment and private DNS for dependencies; public HTTPS app ingress only |
| Secrets | Key Vault references and workload-specific mounts; no secret values in Bicep parameter files or outputs |
| Worker | Scheduled Container Apps job every minute, same image, dedicated SQL role, no ingress |
| Migration | Manually invoked Container Apps job, same image/database CLI, dedicated migrator principal, no ingress |
| Registry | Configurable existing registry with immutable digest; managed identity pull where supported |
| Telemetry | Existing safe structured logs plus bounded operational metrics; configured retention, ingestion cap, and alerts |

These resource sizes and scaling thresholds are initial experiment settings, not measured capacity
claims. `Deployment:Replicas` represents the permitted running replica capacity, not ACA's idle minimum;
zero idle replicas must not violate the existing positive capacity validator.

SQL remains provisioned initially because scheduled queue polling can defeat database auto-pause.
Compare its measured cost against serverless SQL before changing the default. Private endpoints,
DNS, registry, logs, backups, and jobs still incur costs when the web tier is at zero.

Use system-assigned identities for blob access and secret retrieval, preserving the current provider.
Add Azure SQL Entra principal provisioning for web, worker, migrator, operator, and maintenance roles,
mapping identities to the existing narrow database roles. Keep contained SQL users for self-hosting.
Do not redesign RLS or grant web/worker schema authority. One-time setup authority belongs to an
explicit operator bootstrap; migration and runtime identities cannot provision their own permissions.
Any Azure SQL compatibility adjustment must preserve the existing SQL Server path and be tested.

The shared SQL key ring retains certificate encryption. Support the platform's mounted secret
representation for the PFX without baking a certificate into the image; decode only in memory if
the mount is Base64 text. Preserve the existing file-based PFX interface for self-hosting. Web and
worker use the same certificate versions; rotation retains old decryption certificates until keys
and pending work no longer require them. Mount the tenant proof separately. SMTP credentials are
scoped to workloads that require them; migrator, operator, and maintenance access stays separate.

## Public origin and proxy trust

Require one canonical HTTPS origin in every production profile, with no user information, query,
fragment, or non-root path. Validate its host against an explicit allowlist and use it for generated
external links and redirects. Reject conflicting SMTP-specific origins. Keep production cookies
Secure regardless of forwarded input. Reject unrecognized hosts before application handlers run.

Replace implicit proxy defaults with explicitly configured exact IPs or narrowly scoped CIDRs;
never trust all proxies or all private networks. Accept only the required forwarded scheme and
client address, consume exactly the configured trusted hop count, and ignore forwarded host.
For ACA's direct ingress path, consume the rightmost platform-appended client address, not an
arbitrary leftmost value. Document the observed socket peer and legitimate ingress ranges.

The environment subnet must not be assumed to identify the immediate ingress peer without evidence.
The hosted harness must prove the trust configuration across replacement replicas and demonstrate
that direct/untrusted peers and forged header prefixes cannot influence client identity. If ACA's
observed topology cannot satisfy this boundary, hosted readiness is blocked pending a focused design
revision; broadening trust to make a test pass is not permitted. No Front Door or additional public
proxy is included in this phase.

Use a custom domain and managed TLS certificate, with separately authorized DNS validation/binding.
Allow the platform hostname only when deliberately selected as the canonical origin. Probe requests
must supply an allowed host or use a tightly scoped health-only mechanism; do not weaken global host
validation to accommodate platform probes.

## Scaling, jobs, and release behavior

Disable session affinity. Two replicas must share sessions, revocation, protection keys, rate-limit
budgets, and blob access, including during a rolling replacement. Rate-limit tests alternate between
replicas and include forged forwarded addresses. Bound connections per replica and account for web,
worker, migration, and overlapping revisions when validating the SQL connection budget.

Add a bounded worker drain mode: stop at an empty eligible queue, configured item limit, deadline,
or cancellation. Start with 100 items / 45 seconds per invocation and a 90-second job timeout.
Keep SQL leases and idempotency as authority when schedules overlap or executions retry. Report
processed count and oldest pending age without payloads; alert at five minutes pending age.
Do not rely on HTTP traffic to deliver recovery email. Public recovery remains disabled until the
worker and SMTP path pass an end-to-end check. Measure backlog throughput before enabling it.

Set explicit startup, liveness, and readiness probes. Start with a 120-second startup budget,
10-second liveness period, and 10-second readiness period with a timeout sufficient for the actual
bounded dependency checks. Liveness has no remote dependencies; readiness verifies schema/security
state and required providers. Tune from measured behavior, avoiding restart loops during dependency
outages. Stop accepting new work during shutdown and bound draining to the platform grace period.

Publish one image digest. Before changing traffic, validate configuration, take/verify the required
backup, run the migration job, and verify its final state and expected schema. Use a database-scoped
lock across the complete migration operation, including separate job executions and CLI invocations.
Characterize existing EF SQL Server locking first and retain it if sufficient; otherwise add an
explicit connection-owned application lock. Test concurrent runs and cancellation against real SQL.
No web startup migration, no scheduled migration job, and no migrator secret in web or worker.

Start the candidate revision without production traffic, run authenticated smoke checks, and switch
traffic only after readiness and compatibility checks. Test mixed revisions only within a declared
schema compatibility window. Roll back the image only if the old release supports the current schema;
otherwise keep traffic stopped and use the reviewed recovery procedure. Never automate destructive
down-migrations. Production traffic changes remain human-authorized.

## Backup, restore, and telemetry

Configure Azure SQL point-in-time retention (initially seven days) and blob soft-delete/version
retention (initially 30 days), both environment parameters. Retention alone is not a paired backup.
For a recoverable checkpoint, drain all writers and workers, capture the exact manifest and retained
blob version identities, and record a recoverable SQL time while writes remain frozen. Verify the
pair and retain its digest, schema version, image digest, and encryption-key recovery dependencies
in access-controlled backup storage. Longer retention must extend all dependent artifacts together.

Restore to an isolated database and blob target with no public route. Establish the restore-pending
guard before any workload connection; run existing session/key/outbox sanitation and exact blob
verification before readiness. Azure SQL uses its platform restore API, not `RESTORE DATABASE`.
Record measured recovery duration and potential data loss. Initial targets are RPO 24 hours for
verified paired checkpoints and RTO four hours; these remain unproven until drilled. Automated
SQL PITR alone does not establish the paired recovery guarantee.

Reuse `SafeTelemetryLoggerProvider`; emit only allowlisted operation identifiers, duration, outcome,
revision, queue age, and resource utilization. Do not log raw URLs containing tokens, cookies,
authorization headers, SQL connection strings, or SMTP content. Default log retention is 30 days;
ingestion budget and alert recipients are explicit environment inputs. Test redaction at the exporter
boundary and include dependency failures. Alerts cover failed jobs, persistent unready replicas,
queue age, backup age, and budget thresholds.

## Self-hosted delivery

Provide a production Compose configuration and runbook with a TLS reverse proxy, private app listener,
explicit worker, mounted secrets, stable filesystem volume, and configurable SQL/SMTP/blob endpoints.
Support external SQL and a documented local SQL service profile. Keep bootstrap and migration as
explicit one-shot operations. Persist proxy certificate state and back up SQL/blob/key material.
Use the same image digest for web, worker, and database tooling.

Preserve the non-root read-only app with bounded temporary storage and no extra capabilities.
Document volume ownership and certificate trust without disabling validation. Default filesystem
storage supports one web replica; multiple replicas require Azure blobs or a shared filesystem whose
atomic publication and durability have been verified. Re-run the deployment from clean documented
configuration, including TLS, login, recovery delivery, restart persistence, and rollback.

## Cost model and reconsideration triggers

Record region, currency, date, SKU, and unit-price source alongside each estimate. Monthly cost is
web active/idle vCPU-seconds and GiB-seconds plus requests, job executions, SQL compute/storage/backup,
blob capacity/operations/versions, registry, private networking/DNS, secrets, telemetry, and egress.
Apply only allowances actually available to the subscription. Record both idle and burst scenarios.

Collect at least 30 idle-to-ready samples and report p50/p95/max, first authenticated request latency,
failures, image size, SQL state, region, and concurrent load. Separate local container startup from
Azure cold starts. Initial review triggers: p95 cold first request above 15 seconds; warm error rate
above 1%; SQL CPU above 70% under the intended peak; or oldest work above five minutes.

Compare a Linux App Service plan at equivalent capacity and availability, including its worker and
network costs. Reconsider the topology if a 30-day measured projection is more than 20% above that
alternative, or meeting latency targets requires minimum-one web replicas for most operating hours.
Do not change topology automatically. No workload measurements or monetary estimate are claimed by
this proposal; pricing and results must be captured during implementation and authorized validation.

## Alternatives and tradeoffs

- Always-on App Service simplifies cold-start behavior but pays for idle capacity; retain as the
  measured comparison rather than replacing the accepted Container Apps baseline speculatively.
- SQL serverless auto-pause may reduce idle database compute, but periodic workers and health traffic
  may prevent pausing and resumption adds latency; benchmark before choosing it.
- Public database/storage endpoints may reduce fixed networking cost but enlarge the exposure and
  firewall-management boundary; prefer private dependencies and include their actual cost.
- A continuously running worker reduces queue latency but creates permanent compute usage; the
  scheduled bounded worker preserves existing semantics with a measurable delivery-delay tradeoff.

## Implementation and acceptance evidence

After acceptance, maintain an untracked implementation plan with these ordered deliverables:

1. TDD for production origin, host/proxy trust, secret loading, bounded worker behavior, and migration
   concurrency. Cover invalid configuration, spoofing, cancellation, and independent SQL connections.
2. Bicep modules, non-secret examples, identity provisioning, job definitions, Compose, and runbooks.
   Compile/lint infrastructure and test configuration contracts without creating cloud resources.
3. Current-source repository verification, container/Compose checks, multi-replica HTTP checks,
   mutation testing of changed security/worker rules, and internal implementation review.
4. Separately authorized isolated Azure validation: real identity/RBAC/private DNS, trusted ingress,
   cold starts, scale-out, job races, TLS, rollout/rollback, paired recovery, telemetry, and cost data.
5. Security review of the final immutable configuration/document/code diff, resolution of reportable
   findings, living-document updates, scoped commit, and ready-for-review PR. No merge or deployment.

Each issue checkbox must link to actual evidence. Unavailable cloud checks remain explicitly pending;
local tests, Azurite, Bicep compilation, and a proposed runbook cannot substitute for hosted results.
The issue and spec must not be marked complete/implemented while those criteria remain unmet.

## Sources checked for this proposal

- [Container Apps ingress](https://learn.microsoft.com/en-us/azure/container-apps/ingress-overview):
  TLS, revision traffic, and platform handling of forwarded headers.
- [Container Apps jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs): scheduled and manual executions.
- [Health probes](https://learn.microsoft.com/en-us/azure/container-apps/health-probes): explicit startup/readiness/liveness configuration.
- [Container Apps billing](https://learn.microsoft.com/en-us/azure/container-apps/billing) and
  [pricing](https://azure.microsoft.com/en-us/pricing/details/container-apps/): compute and request meters.
- [Azure SQL serverless](https://learn.microsoft.com/en-us/azure/azure-sql/database/serverless-tier-overview?view=azuresql):
  database compute and auto-pause tradeoffs.

Provider facts inform the proposal; numeric defaults and acceptance targets above are Workbench
accepted design choices awaiting measurement, not provider guarantees.
