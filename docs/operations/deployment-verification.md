# Deployment verification record

Use the [current acceptance matrix](production-readiness.md) to distinguish completed checks,
operator deferrals and unverified requirements. This index owns navigation to immutable,
scoped evidence; it is not another acceptance checklist or operating procedure.

| Record | Scope |
| --- | --- |
| [Initial deployment acceptance, 2026-09-06 through 2026-09-09](evidence/2026-09-deployment-acceptance.md) | Local suite, Windows QA/installer, Azure release corrections, public launch and bounded follow-up, with exact revisions and limits. |
| [Local self-host update, 2026-09-07](evidence/2026-09-07-local-self-host-update.md) | Same-release/prior-schema updates of disposable Windows fixtures, nonempty checkpoints and failure containment. |
| [Original production audit, 2026-09-06](evidence/2026-09-06-production-audit.md) | Historical audit plus later annotations, retained for traceability; not current acceptance. |

Add each later verification change as a scoped, dated record in `evidence/` and link it here,
from its spec/demo, and from the relevant acceptance row. Keep one evidence location per change.
Record source/image/schema identifiers, environment, results and coverage limits; never infer
that later commits were deployed. Store screenshots/recordings outside Git under
[the contributor evidence policy](../../CONTRIBUTING.md). Reusable commands belong in runbooks.

The following headings preserve incoming links to the original record.

## Local evidence

See the [dated evidence](evidence/2026-09-deployment-acceptance.md#local-evidence).

## Windows localhost QA drill (2026-09-06)

See the [dated evidence](evidence/2026-09-deployment-acceptance.md#windows-localhost-qa-drill-2026-09-06).

## Automated local installer verification

See the [dated evidence](evidence/2026-09-deployment-acceptance.md#automated-local-installer-verification).

## Azure release correction evidence (2026-09-07 UTC)

See the [dated evidence](evidence/2026-09-deployment-acceptance.md#azure-release-correction-evidence-2026-09-07-utc).

## Remaining hosted acceptance

See the [dated evidence](evidence/2026-09-deployment-acceptance.md#remaining-hosted-acceptance).

## Azure public launch (2026-09-08 UTC)

See the [dated evidence](evidence/2026-09-deployment-acceptance.md#azure-public-launch-2026-09-08-utc).

## Azure acceptance follow-up (2026-09-09 UTC)

See the [dated evidence](evidence/2026-09-deployment-acceptance.md#azure-acceptance-follow-up-2026-09-09-utc).
