# Azure forwarded metadata trust

Status: implemented; design approved 2026-09-07. Current configuration and operational restrictions
are in [Azure ingress](../operations/azure-ingress.md). Hosted observations are scoped by the
[acceptance matrix](../operations/production-readiness.md), not implied by this status.

## Problem and evidence

The production diagnostic probe observed different header handling on Azure Container Apps
full-hostname and short-name routes. Full-hostname requests appended an address to caller-supplied
`X-Forwarded-For`. Short-name requests retained the supplied chain unchanged, including with a
full hostname supplied in `Host`. Both routes used the same immediate proxy peers. Replacing
replicas did not establish an exclusive or durable proxy address boundary.

## Accepted design

`ReverseProxy:Mode=AzureContainerApps` explicitly accepts the Container Apps environment as a
trust boundary for forwarded client IP and protocol metadata only. It accepts changing immediate
peers, processes exactly one rightmost hop, and never forwards host or prefix. Proxy address lists
and a hop limit other than one are rejected in this mode. Unknown modes fail startup.

The default remains `KnownProxies`, including when the setting is absent. Self-hosted deployments
continue to require explicit trusted peers or narrow networks. Azure deployment parameters expose
`proxyTrustMode` with the same default; enabling the exception is a deliberate operator choice.
This setting declares an assumption, not proof that the process is hosted in Azure. Do not enable
it on a directly reachable self-hosted listener or an environment containing untrusted workloads.

## Narrow exception and residual risk

A compromised workload in the environment can supply arbitrary forwarded metadata on the internal
route and evade the IP component of sensitive-operation limits. Account/subject limits still apply.
Per-IP and subject limits remain SQL-backed and shared across replicas. No deployment-wide fallback
bucket is introduced: its availability impact was rejected by the operator.

This exception grants no application, database, storage, mail, or secret permissions. Authentication,
authorization, antiforgery, tenant isolation, managed-identity least privilege, private endpoints,
explicit allowed hosts, canonical HTTPS links, and secure cookies remain required. HTTPS must still
be enforced at public ingress. No global ASP.NET forwarded-header environment switch is required.

## Acceptance and rollback

- Tests exercise changing peers, one-hop processing, ignored forwarded host, conflicting settings,
  unknown modes, and unchanged default peer restrictions.
- Azure validation permits explicitly selected environment trust with empty lists, but continues
  to reject activation without scoped grants and other existing deployment prerequisites.
- Hosted acceptance verifies real HTTPS/login, two-replica rate limits and session revocation;
  diagnostic success alone is not production readiness.
- No schema or rate-limit threshold changes. Rollback stops public workloads before returning to
  `KnownProxies`; never substitute guessed addresses or silently widen trusted networks.

The rejected alternatives were IP discovery (does not distinguish observed routes), host checks
alone (did not change short-name header handling), and global rate limits (shared availability cost).
