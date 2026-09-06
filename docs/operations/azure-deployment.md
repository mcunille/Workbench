# Azure deployment operations

The [accepted deployment design](../specs/2026-09-05-azure-deployment.md) establishes the target;
the templates in [infra/azure](../../infra/azure/main.bicep) implement the resource configuration.
Compilation and parameter checks are local evidence only. No Azure environment, ingress trust,
identity authorization, cold-start result, recovery duration, or cost projection has been validated.
Hosted acceptance remains pending. Provisioning, DNS, deployment and traffic changes require separate
operator authorization; none of the commands below should be run against a retained environment as a test.

## Inputs and offline checks

Use Azure CLI with Bicep 0.46.1 or later, PowerShell 7.5 or later, an authorized subscription, and an
existing ACR registry containing the reviewed image. Give a dedicated user-assigned pull identity only
`AcrPull` on that registry; enable managed-identity authentication on the registry. Pass that identity
resource ID and registry hostname. The pull identity must have no SQL, blob or vault grants: applications
select their distinct system-assigned identities for those services. The existing registry must be
reachable from the selected VNet; a private registry additionally needs operator-configured private DNS
and its own private endpoint. The templates do not change the existing registry.

Copy [the non-secret example](../../infra/azure/main.parameters.example.json) outside Git and replace
every placeholder, including the image digest, stable installation UUID, setup Entra **group** object ID,
SMTP endpoint/sender, alert recipients, billing-currency budget and budget start date. Use the first day
of the deployment month for the budget start date. The example's 200-unit budget is an illustration,
not a price estimate. Keep `activate`, `grantAccess`, `workerEnabled`, `publishIngress` and public recovery
false initially. There is deliberately no guessed trusted proxy address or CIDR in the example.

```powershell
./infra/azure/test-parameters.ps1
./infra/azure/validate-parameters.ps1 -ParametersFile <non-secret-environment-parameters.json>
az bicep build --file infra/azure/main.bicep --outfile <temporary-compiled-template.json>
```

The validator rejects mutable images, empty installation/setup identities, conflicting or noncanonical
origins, wildcard hosts, activation without proxy trust or grants, overly broad proxy CIDRs, and public
traffic pointing implicitly at the latest revision. Run it before each deployment; compiling Bicep
alone does not enforce those cross-field rules. Do not add credential values to parameter JSON or
command arguments. Resource names are deterministic within the resource group; the default address
space is `10.42.0.0/16`, so select a nonoverlapping environment before connecting existing networks.
Configure `vnetAddressPrefix`, `appsSubnetPrefix` and `endpointsSubnetPrefix` for the installation;
the defaults are `10.42.0.0/16`, `10.42.0.0/23` and `10.42.2.0/24`. ARM network validation must confirm
subnet sizing, containment and nonoverlap before deployment, including peered networks. Templates target
Azure public cloud; sovereign-cloud vault DNS and available SKUs need separate review.

## Staged bootstrap

System identities cannot be granted access before their resources exist. Use these explicit phases
to avoid circular secret-resolution dependencies:

1. After authorization, run deployment `what-if`, inspect changes, then deploy with all activation
   switches false. This creates the network, private SQL/blob/vault endpoints and DNS, logs, inactive
   web resource, manual jobs and their identities. The web has no ingress; do not start either job.
   Record the resource IDs and system principal IDs from outputs. Resource outputs contain no secrets.
2. From a protected administrative host with private DNS/network access, populate the vault using
   secret **files**, not `--value` arguments. Required names are `tenant-proof`, `protection-pfx`,
   `protection-password`, `smtp-password`, and `migration-connection`. The PFX secret is Base64-encoded
   PKCS#12 text. Its password is a separate secret. `migration-connection` contains an encrypted SQL
   connection string using `Authentication=Active Directory Managed Identity`, the output SQL host,
   database `Workbench`, `TrustServerCertificate=False`, and bounded pool size; it contains no SQL
   password. The tenant proof must match the bootstrap SQL proof. Restrict bootstrap identity access
   and retain encrypted certificate/proof recovery copies outside the vault's failure domain.
3. Bootstrap schema using the explicit database CLI and setup authority from the private operator
   host. Provision Entra users with the exact system principal object IDs, plus separately selected
   operator and maintenance identities. Use a protected setup connection file and the CLI below.
   Never grant runtime users `db_owner`, schema authority or another workload's role. Runtime
   identities cannot grant their own permissions. The one-time setup group is the SQL Entra admin;
   remove unnecessary membership after bootstrap. Preserve a controlled break-glass procedure.
4. Set `grantAccess=true`, retaining `activate=false`, and deploy again. The access module grants
   web/worker Blob Data Contributor only on the installation container and Key Vault Secrets User
   only on their four secret resources. Migration gets only its connection secret. Allow RBAC
   propagation and verify grants before activating. Recreating a system identity requires repeating
   grants and SQL mapping; matching a display name is insufficient.
5. Observe legitimate ACA socket peers in the isolated environment and fill the exact proxy list or
   narrow CIDRs. For a custom canonical hostname, first obtain a bootstrap certificate using DNS
   validation (which does not require public app ingress), upload it to the Container Apps environment
   through the operator's secure certificate workflow, and set `customDomainCertificateId` to its
   resource ID. Keep its private key/password out of parameters and command arguments. The operator's
   diagnostic client must trust its issuer. Set `activate=true`, keeping `publishIngress=false`, `workerEnabled=false`, and
   public recovery disabled. Run the migration job manually and inspect its terminal result/schema.
   Exercise private ingress from a protected diagnostic workload in the same Container Apps
   environment, manual worker execution, SQL authorization, blob access and SMTP. Internal app
   ingress is environment-only; an arbitrary host elsewhere in the VNet cannot reach it.
6. Enable the minute worker schedule only after successful bounded delivery and lease-overlap tests.
   After the private checks pass, separately authorize public DNS and ingress using the already-bound
   bootstrap certificate and pinned revision traffic. Perform public TLS checks, then request the ACA
   managed certificate and replace the binding as described below. Public checks and cold-start
   measurements follow this explicit exposure authorization; they are not prerequisites that must
   somehow run against an inaccessible public endpoint. Public recovery remains disabled until
   controlled end-to-end email delivery succeeds.

```powershell
dotnet Workbench.Database.dll principals provision-entra --connection-file <setup-connection-file> `
  --expected-database Workbench --identity-file <identity-mappings.json> `
  --tenant-context-proof-key-file <proof-key-file>
```

The identity file is a JSON array of `{ "role": "workbench_web", "name": "<database-user-name>",
"objectId": "<actual-system-principal-id>" }` entries for `workbench_web`, `workbench_worker`,
`workbench_migrator`, `workbench_operator` and `workbench_storage_maintenance`. Select non-workload
operator/maintenance identities explicitly. Do not substitute application/client IDs for object IDs.
The CLI and SQL role checks are the authority; verify the actual installed CLI help before execution.

Certificate rotation uses `previousCertificates`, an array such as
`[{ "secretName": "protection-2026-pfx", "passwordSecretName": "protection-2026-password" }]`.
Each referenced PFX is Base64 text and its password is separately mounted. Before replacing the current
secret, create retained secret copies and grant their scoped runtime reads using the access module
with the actual principal IDs and the new array. Then roll both workloads with the retained list,
verify old protected data decrypts, and only then update the current secret and roll again. Keep names
unique and prefixed `protection-`; migration secret aliases are refused by parameter validation.
Do not remove retained entries until the corresponding keys, outstanding work and recovery checkpoints
no longer require them. Secret refresh alone does not reload an already-started application's
certificate objects; explicitly replace and verify all runtime replicas/jobs during rotation.

Web and worker share the certificate, proof, blob binding and SMTP settings. The web connection pool
is 20 per replica, worker 20 and migration file should be bounded to 5. With three replicas and two
overlapping revisions, budget at least `2 * 3 * 20 + 20 + 5 = 145` potential pooled connections plus
operator/check traffic. Reduce the pools or replica ceiling if the selected SQL SKU cannot sustain
that ceiling. Idle minimum zero is separate from `Deployment:Replicas=3`, which declares capacity.

## Trust, TLS and readiness acceptance

Do not equate the environment subnet with the immediate proxy peer. Record socket peer, appended
forwarded chain, revision and replica under protected diagnostic controls without logging cookies,
tokens or payloads. Repeat after replacing replicas. Configure only peers established by that evidence.
Direct/untrusted requests and attacker-supplied leftmost `X-Forwarded-For` prefixes must not change
the effective client, rate-limit bucket or secure cookie behavior. Forwarded host is ignored. If
stable narrow trust cannot be established, leave public ingress disabled and return to design review.

The app's allowlist is exactly `publicHost`; every HTTP probe explicitly supplies it. Startup allows
120 seconds, liveness checks every 10 seconds without remote dependencies, and readiness uses a
10-second timeout. Measure actual readiness duration and adjust with evidence. Verify bad Host headers
are rejected and HTTPS secure cookies persist through rollout. Tests must include two replicas sharing
login/session revocation, protection keys, rate-limit budgets and blobs without session affinity.

ACA managed-certificate issuance requires public ingress reachable by the issuer. It cannot bootstrap
the private validation stage. Use the DNS-validated uploaded bootstrap certificate from step 5 for
private testing and initial authorized public ingress. Once public DNS/ingress is reachable, use
[the Container Apps custom-domain procedure](https://learn.microsoft.com/en-us/azure/container-apps/custom-domains-managed-certificates)
to obtain the managed certificate. Retain the working bootstrap binding until issuance succeeds;
failure must not remove TLS or enable insecure ingress. Change `customDomainCertificateId` to the
managed certificate resource ID, apply the reviewed binding update, and verify TLS/renewal before
retiring the bootstrap certificate. The
template binds `publicHost` with SNI and preserves that binding on subsequent deployments. No private
key or certificate bytes enter parameters. Public custom-host activation fails parameter validation
without a certificate reference. Leave it empty only when deliberately using the platform hostname.
Bind only the canonical hostname and record certificate renewal behavior. Keep platform hostnames
outside `AllowedHosts` unless intentionally choosing one as the canonical origin. Private candidate
tests need a controlled resolver/host route that preserves canonical Host and validates TLS; do not
disable certificate validation to make the test pass.

## Release and rollback

Before an upgrade, record the current serving revision names, digest, schema version and paired
checkpoint. Disable scheduled worker execution and drain active jobs when the schema compatibility
window requires it. Supply `releaseTraffic` with explicit **existing** revision names and weights
totaling 100 before updating the image; never send production traffic implicitly to `latestRevision`.
The candidate shares the image digest with manual worker/migration jobs. Keep production allocation
pinned while starting migration, inspecting successful completion, checking schema and running
candidate authenticated smoke tests. In multiple-revision mode, use a private revision route or a
temporary operator-controlled test binding; prove its host/TLS configuration before testing.

Only after approval, replace the pinned allocation with the verified revision name, then enable the
worker schedule. Public rollout commands are intentionally not embedded in a CI script. On failure,
leave traffic on a compatible old revision. Roll back an image only when it supports the installed
schema; otherwise stop traffic and execute the reviewed recovery procedure. Never automate down-
migrations. A single job replica prevents neither overlapping executions nor concurrent external
CLI invocations: the database migration lock and lease fencing remain authoritative.

Worker jobs use `--worker --drain`, 100 items, a 45-second drain budget, a 90-second platform timeout,
one replica and one retry. Validate retry/overlap outcomes and queue age separately from process exit.
The migration job is manual, has no retries and a 30-minute timeout, and receives no SMTP/blob/proof
secrets. Measure actual shutdown against the 60-second web grace period.

## Paired checkpoint and isolated Azure restore

SQL PITR retention defaults to seven days; blob versions and soft-deleted containers/objects to
30 days. These settings do not establish a recoverable pair. Freeze every writer and worker, ensure
no execution remains, produce and verify the exact protected blob manifest/copy using the
[provider backup procedure](blob-and-service-providers.md), then record a SQL recoverable UTC point
while writes stay frozen. Capture exact blob version IDs with the manifest, digests, image/schema
versions, installation UUID, provider binding and key recovery dependencies in the protected backup
catalog. Confirm the SQL point is actually restorable before calling the checkpoint successful.

For a drill use [Azure SQL point-in-time restore](https://learn.microsoft.com/en-us/azure/azure-sql/database/recovery-using-backups?view=azuresql)
to a new isolated database, not `RESTORE DATABASE`. Keep runtime principals and public routes blocked
before its first workload connection. Establish restore-pending state through the protected operator
session, run `restore sanitize`, restore the exact paired blobs, and run `storage verify` before
readiness. Restore SQL retains old provider aliases: when the isolated blob URI differs, use the
documented offline `storage migrate` procedure to relocate from an exact restored source binding;
changing environment variables alone cannot relocate references. Failure to reconstruct that source
binding keeps the drill blocked. Do not point a drill at the writable production container.

Record measured recovery time, data loss, sanitation and every digest check. Initial RPO 24 hours and
RTO four hours are unproven targets. Keep old certificate versions available until neither retained
keys nor work require them. Extend retention for SQL, blobs, manifests and cryptographic recovery
material together. Do not destroy source stores or rotate away the only recovery keys after a drill.

## Monitoring, costs and hosted evidence

The template installs bounded Log Analytics retention/ingestion, job-failure metric alerts,
queue-age/worker-silence/readiness log alerts, and 80% actual/100% forecast monthly budget
notifications. Budgets notify; they do not stop spending.
Confirm the actual `Executions` metric `state` value is `Failed` and trigger a synthetic job failure
to prove the alert reaches operators; see [job metrics](https://learn.microsoft.com/en-us/azure/azure-monitor/reference/supported-metrics/microsoft-app-jobs-metrics).
The queue alert parses `Event=WorkQueueStatus` with `PendingCount` and `OldestPendingAgeSeconds`
from worker stdout. It alerts when the latest valid status reports pending work older than 300 seconds.
The companion alert fires if no valid status arrives for ten minutes, including an empty queue's
zero-count/zero-age status. Both enable only with the scheduled worker, so an intentional disabled
schedule does not page operators. A log-ingestion cap or export outage can also trigger the missing-
status alert; investigate both execution and telemetry before restarting or replaying work.

Queries use [the documented console/system tables](https://learn.microsoft.com/en-us/azure/container-apps/log-monitoring)
and the job replica-name prefix from [the jobs log-query example](https://learn.microsoft.com/en-us/azure/container-apps/jobs-get-started-cli).
Readiness alerting watches actual system-log text containing `readiness probe failed` for one revision,
with recent failures spanning at least five minutes. It does not interpret legitimate scale-to-zero
as unhealthy, or claim that silence proves readiness. Validate that the platform emits this text in
the selected environment, including failures that prevent application startup. If it does not, leave
hosted acceptance pending and connect an observed supported status source; do not invent a readiness
metric. All three scheduled queries skip deployment-time query validation because the tables do not
exist during inactive bootstrap. Run each query against the live workspace, inject each failure and
prove alert delivery before accepting the hosted installation. Bicep compilation does not validate KQL
execution, log schema, timing or notification delivery.

Dead-letter and paired-backup age over 24 hours additionally require a protected operational monitor;
the templates have no successful-checkpoint emitter or dead-letter count source. Configure and exercise
those sources before hosted acceptance. A timestamp supplied without a verified paired checkpoint
must never count as a successful backup. Do not claim job-success metrics prove delivery or freshness.
Use allowlisted telemetry only; verify exporter output and external platform logs exclude tokens,
cookies, SQL strings and mail. HTTP access logging is not enabled by this template because its
documented fields include query strings and forwarded client addresses.

For each idle/burst cost estimate record date, region, currency, SKU, meter, unit price, billable units,
free allowance actually available, subtotal and source. Include web vCPU/GiB-seconds and requests,
scheduled jobs, provisioned SQL/storage/backups, blob versions/operations, ACR, three private endpoints,
private DNS, vault, log ingestion/retention/alerts and egress. The app's zero replica floor does not
zero these fixed costs. Use current [Azure prices](https://azure.microsoft.com/en-us/pricing/calculator/)
and reconcile with Cost Management after the isolated run. Compare equivalent Linux App Service
capacity including workers/networking. The [cost worksheet](deployment-costs.md) records retrieved
retail rates and explicitly unmeasured scenarios; the [verification record](deployment-verification.md)
separates completed local checks from pending hosted acceptance.

Store an access-controlled evidence table with scenario, UTC time, image, schema, region, resource
configuration, observed result, failures and artifact reference. Required hosted rows are: private DNS
and denied public dependencies; least-privilege managed identities; trusted ingress/spoof rejection;
custom TLS; two-replica sessions/rates; migration concurrency/cancellation; worker retries/SMTP;
dependency outages/readiness; exporter redaction; rollout and rollback; paired restore; alerts; and cost.
Collect at least 30 genuine idle-to-ready samples, reporting p50/p95/max and first authenticated request
latency, image size, SQL state and concurrent load. Local startup is not an Azure cold-start sample.
Review the design if cold p95 exceeds 15 seconds, warm errors exceed 1%, SQL CPU exceeds 70%, queue age
exceeds five minutes, or 30-day projected cost exceeds equivalent App Service by 20%. No automatic
topology change is authorized by a threshold breach.
