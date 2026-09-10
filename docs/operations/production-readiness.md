# Production acceptance matrix

Recorded status reconciled on 2026-09-09 UTC from linked evidence, without rerunning hosted
checks. This is the single current acceptance matrix. **Completed** means only the recorded scope;
**deferred** means an explicit operator decision; **unverified** means evidence does not establish
the requirement. The [original audit](evidence/2026-09-06-production-audit.md) is historical and
retains its original implementation/review context and later annotations.

The [Azure entry point](azure-deployment.md), [Windows localhost QA guide](local-self-host.md),
[public Linux self-host guide](self-hosted-deployment.md), and [developer setup](../setup.md) serve distinct
purposes. A passing check in one environment does not certify another.

| Area | Recorded state and acceptance boundary | Dated evidence |
| --- | --- | --- |
| Local implementation checks | Completed for recorded revisions: source suite, container smoke, parameter/Compose checks and bounded manual mutations. No broad mutation or complete security-scan verdict. | [2026-09-06](evidence/2026-09-deployment-acceptance.md#local-evidence), [2026-09-07 corrections](evidence/2026-09-deployment-acceptance.md#azure-release-correction-evidence-2026-09-07-utc) |
| Windows localhost installation | Completed manual QA and isolated automated installer: SQL TLS, principal setup, HTTPS/login and startup ordering. Public Linux acceptance remains separate. | [2026-09-06 QA](evidence/2026-09-deployment-acceptance.md#windows-localhost-qa-drill-2026-09-06), [installer](evidence/2026-09-deployment-acceptance.md#automated-local-installer-verification) |
| Windows update/checkpoints | Completed same-release/prior-schema updates, sessions, nonempty checkpoint exports and bounded failure cases in disposable fixtures. No paired restore or browser UI automation in this update drill. | [2026-09-07](evidence/2026-09-07-local-self-host-update.md) |
| Azure launch/private dependencies | Completed public TLS, sign-in, pinned traffic, headers, dependency public-access denial and migration-job readback for recorded installation/release. Fresh reproducible bootstrap remains unverified. | [2026-09-08](evidence/2026-09-deployment-acceptance.md#azure-public-launch-2026-09-08-utc) |
| Azure identity/mail/recovery links | Completed released-worker durable Graph delivery, link use/reuse, session invalidation and mailbox scope checks. Hosted SMTP remains a separate unverified provider path. | [2026-09-08](evidence/2026-09-deployment-acceptance.md#azure-public-launch-2026-09-08-utc) |
| Azure two-replica behavior | Completed shared sessions/revocation and sequential combined account/network login allowance across two replicas. Independent account-only partition, concurrent load and full scale-out capacity remain unverified. | [2026-09-09](evidence/2026-09-deployment-acceptance.md#azure-acceptance-follow-up-2026-09-09-utc) |
| Azure cold starts | Completed one natural client-observed 27.943-second sample, accepted with scale-to-zero retained. More samples and p50/p95 distribution explicitly deferred. | [2026-09-09](evidence/2026-09-deployment-acceptance.md#azure-acceptance-follow-up-2026-09-09-utc) |
| Azure rollback | Completed compatible frontend/docs-only rollback and return to revision `0000005`. Database rollback and incompatible-schema recovery remain unverified. | [2026-09-09](evidence/2026-09-deployment-acceptance.md#azure-acceptance-follow-up-2026-09-09-utc) |
| Alerts/audit ingestion | Completed worker-status-missing and security-change notifications, isolated readiness alert firing/resolution and recorded audit/backup ingestion. Other injected alert failures, dead-letter source and strict paired-checkpoint-age monitor are not established by these results. | [2026-09-08](evidence/2026-09-deployment-acceptance.md#azure-public-launch-2026-09-08-utc), [2026-09-09](evidence/2026-09-deployment-acceptance.md#azure-acceptance-follow-up-2026-09-09-utc) |
| Native Azure backup | Completed scheduled vaulted backup and `AddonAzureBackupJobs` ingestion. Native backup is distinct from optional custom collection and strict offline paired checkpoints. | [2026-09-09](evidence/2026-09-deployment-acceptance.md#azure-acceptance-follow-up-2026-09-09-utc), [native procedure](azure-native-backup.md) |
| Recovery | Completed scoped Windows/Azure empty-data drills and independently recovered/decrypted key material as recorded. Nonempty attachment recovery, recovered Windows browser sign-in/session rejection, full measured application RTO/RPO, logical-server undelete and cross-region Blob recovery remain unverified. | [2026-09-06 QA limits](evidence/2026-09-deployment-acceptance.md#windows-localhost-qa-drill-2026-09-06), [2026-09-08](evidence/2026-09-deployment-acceptance.md#azure-public-launch-2026-09-08-utc) |
| Cost/App Service comparison | Explicitly deferred by operator, who will monitor spending. Worksheet uses public rates and synthetic inputs; USD 100 budget notifies and is not a cap. Prices were not refreshed here. | [2026-09-09 decision](evidence/2026-09-deployment-acceptance.md#azure-acceptance-follow-up-2026-09-09-utc), [worksheet](deployment-costs.md) |
| Remaining production acceptance | Unverified: complete reproducible bootstrap/runbook check, final deployment documentation/configuration security review, certificate rotation and public Linux self-host acceptance. Preserve narrower limits above; drills and image scan do not imply a final no-findings verdict. | [2026-09-09 remaining scope](evidence/2026-09-deployment-acceptance.md#azure-acceptance-follow-up-2026-09-09-utc), [historical expanded gate](evidence/2026-09-06-production-audit.md#completion-gate) |

## Recording further acceptance

Run only separately authorized environment-specific checks. Preserve image/schema/configuration
identifiers, UTC timestamps, observed failures, measured recovery time/data loss and protected
artifact references without secrets or tenant data. Add evidence to the [collection](deployment-verification.md)
and update the relevant row here; do not rewrite historical results into current procedures.
A completed scoped drill does not waive unverified requirements or operator authorization for
production changes, public ingress, recovery or deletion.
