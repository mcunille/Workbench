# Production security controls

**Status:** Implemented, with scoped hosted application recorded in the
[deployment evidence](../operations/evidence/2026-09-deployment-acceptance.md#azure-public-launch-2026-09-08-utc)
and its follow-up. Configuration, observed ingestion, alert delivery and recovery drills are distinct
claims; use the [acceptance matrix](../operations/production-readiness.md) for remaining limits.
Future hosted operations remain separately approval-gated.

The pre-public Azure review found missing canonical ingress restrictions, browser protections and
security event collection. Correct these controls without changing tenant authority, the accepted
one-hop Azure proxy metadata trust boundary, public traffic, workload identities, or backup design.

## Requirements

- Require an explicit Restricted/Public ingress policy at the ARM boundary. Restricted cannot have
  zero rules; Public explicitly has none. Preserve the current allowlist, HTTPS certificate and named
  revision allocation during upgrades. Wrapper validation additionally checks canonical IPv4 CIDRs.
- Apply framing denial, MIME sniffing protection and no-referrer to application responses, including
  static content and errors. HSTS uses trusted HTTPS metadata and excludes local hosts. Do not redirect
  HTTP health probes. A framing-only CSP preserves existing script/style/blob-image behavior.
- Collect authentication-only SQL audit, Key Vault/Blob/registry/native-backup diagnostics, and a
  subscription audit trail. Deliver administrative-change and native backup alerts to the existing
  operator action group. Never enable SQL batch auditing that can expose sensitive statement values.
- Keep resource/application logs at 30 days, including explicit overrides only after service ingestion
  creates the tables. AzureActivity's platform 90-day behavior is an explicit exception. No paid archive,
  Defender tier change or registry upgrade is implicit. Observe ingestion cost after hosted activation.
- Preserve seven-day SQL PITR and native vaulted backups plus geographic redundancy. Add logical-server
  seven-day soft delete and removable accidental-deletion guards. Irreversible native-vault protections
  require separate explicit approval and readback; they do not establish physical WORM or Blob CRR.
- Assess each exact release digest locally against a current vulnerability database, retain evidence,
  and disclose detected components and remaining advisories. Never equate scan completion with approval.

## Scope and acceptance

Repository delivery includes focused regression tests, compiled ARM contracts, required source/browser/
container gates, independent review and scoped preview artifacts. Live Azure deployment, event ingestion,
notification delivery, new image rollout and unrestricted ingress remain separate acceptance steps.
Keep the user-accepted empty recovery drill and seven-day retention. Longer retention, automated recovery,
image cleanup, broad CSP, independent-admin MUA, and supply-chain signing are outside this change.

See [Azure security operations](../operations/azure-security-controls.md),
[ingress policy](../operations/azure-ingress.md), and [browser protections](../operations/browser-security.md).
