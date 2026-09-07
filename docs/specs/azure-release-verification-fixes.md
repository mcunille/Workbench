# Azure release verification fixes

Status: approved; implementation verification in progress.

## Problem and scope

The hosted verification run exposed three release blockers: the runtime supports SMTP but the
approved Microsoft 365 configuration uses Graph application RBAC; SQL provisioning encoded managed
identity object IDs where Azure SQL requires application/client IDs; and `activate=false` omitted
runtime secrets without stopping the web revision. Two worker log alerts also failed deployment
because their queries do not support one-minute evaluation.

The users are deployment operators and administrators requesting invitation/recovery messages.
This change makes the verified configuration reproducible through the repository. It does not
authorize publishing ingress, enabling public identity operations, merging, or deploying a new image.
Existing database migrations and retained production data must remain unchanged.

## Graph delivery

Add an explicit `Graph` selection alongside the existing SMTP, development, and disabled delivery
providers. Use the existing durable identity-message queue and existing invitation/recovery content,
including canonical HTTPS origins and fragment tokens. Keep SMTP operational for self-hosted installs.

Graph configuration declares the sending mailbox object ID, the dedicated mail managed identity's
client ID, and canonical public origin. Restrict requests to Microsoft Graph v1.0 using managed
identity tokens; do not support arbitrary Graph endpoints, client secrets, delegated credentials,
fallback developer identities, or mailbox-read permissions. Use an injected HTTP transport and
credential for tests; production selects the configured managed identity explicitly.

Only the worker receives the mail identity in Azure. The web validates provider configuration and
queues messages but must not acquire a mail token or send directly. Inspect all delivery call sites
and preserve this boundary. Migration receives no mail identity or mail configuration. Graph mode
does not create, read, mount, or authorize an SMTP password secret.

Validate message purpose, expiry, recipient, origin, and configured mailbox before sending. Use a
bounded request deadline, propagate cancellation, and disable HTTP redirects. Accept only HTTP 202
as submission acceptance, not delivery confirmation. Do not log response bodies, bearer tokens,
recipients, message bodies, or identity links. Distinguish permanent authorization/validation errors
from throttling and transient service/network failures. Let the existing bounded queue retry policy
own retries; avoid nested HTTP retry loops and document possible duplicate delivery after an
ambiguous timeout. Honor Retry-After within the queue's bounded scheduling policy when supplied.

Readiness must not send mail or require broad mailbox-read grants. Web readiness validates Graph
configuration, without claiming live send authorization. Worker acceptance includes token acquisition,
actual delivered mail, denied personal-mailbox sending, and the no-reply inbound rejection rule.

## SQL identity contract

Replace the ambiguous provisioning manifest with explicitly versioned managed-identity mappings
containing `name`, `role`, `principalId`, and `clientId`. The operator obtains both IDs from Azure;
offline SQL provisioning cannot independently verify their Entra relationship. Validate distinct,
nonempty IDs and exactly the five supported roles. Encode `clientId` as the SQL SID; retain
`principalId` for Azure RBAC and Exchange registration, never substitute client IDs in those APIs.

Reject legacy objectId-only manifests with a safe, actionable error before changing SQL. Do not
silently reinterpret old fields or rotate the tenant proof. Existing correct mappings remain
idempotent. Mismatched mappings stop and require the documented guarded repair, preserving roles,
checking unexpected grants/ownership, and using a transaction. The retained installation has already
been repaired; do not recreate its users as part of code verification.

## Bootstrap lifecycle and monitoring

Keep system-assigned workload identities and the two-phase provisioning model. Add an explicit
bootstrap orchestration step that deactivates every web revision and verifies no active revisions
and zero replicas before reporting completion. `activate=false` alone must never be described as
stopping compute. Provisioning may briefly start an unconfigured revision; the production validator
continues to fail closed. Failed orchestration must attempt deactivation and report incomplete cleanup.
Manual worker/migration jobs remain unstarted until their prerequisites pass.

A genuinely atomic no-start deployment would require separating identity creation from workload
creation, for example by moving runtime identities to user-assigned identities. Defer that larger
identity migration; use explicit, verified deactivation for this release and disclose the transient
startup limitation. Do not add a long-running idle process or disable production validation.

Support configuring/running the migration job independently of web activation. Preserve temporary
bootstrap subnets during setup; remove their dependencies and the subnet before a main-template
deployment that declares only the permanent subnets. Document VM, NAT, role-assignment, group-member,
and transfer-key cleanup in dependency order, with verified local recovery material as a prerequisite.

Set the queue-age and missing-worker-status alert evaluation intervals to five minutes. Keep the
worker's once-per-minute schedule and other alert frequencies unchanged.

## Operations and acceptance

Update parameter examples, validators, setup/runbooks, recovery instructions, and the production
readiness evidence checklist. Include the OpenSSL separate passphrase-file correction, managed
identity SID distinction, scoped M365 checks, and the difference between job acceptance, completion,
console-log ingestion, and delivered mail. Do not commit live credentials or production parameter files.

Use focused failing tests before behavior changes. Cover Graph payloads and sender confinement,
redirect rejection, cancellation, 202 versus other statuses, retry classification, safe diagnostics,
web/worker identity separation, legacy manifest rejection, correct SID construction, and safe
bootstrap completion/failure cleanup. Exercise configured and unconfigured provider branches and
compile/test both Azure delivery variants. Use focused mutation testing where tooling is available.

Run repository verification and container smoke gates, Azure parameter tests, Compose proxy checks,
and Bicep compilation. Report unavailable checks accurately. Deliver scoped changes through a
ready-for-review PR. A new immutable image and separate hosted web/worker acceptance are required
after merge; the earlier bootstrap-VM mail test is not proof that Workbench's new provider works.

Rollback keeps workloads stopped and retains the previous image/configuration and recovery keys.
There are no schema down-migrations. The prior objectId-only CLI must not be rerun against repaired
managed-identity users. Public TLS, proxy trust, runtime authorization, queue delivery, recovery,
alerts, and costs remain production acceptance gates.
