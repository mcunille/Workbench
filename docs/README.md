# Workbench documentation

Use the task table to find the owner of each current contract or procedure. Dated specs preserve
reasoning, alternatives and acceptance constraints; evidence records preserve what was exercised
at a particular revision. Neither replaces current instructions.

| Task | Start here / current owner |
| --- | --- |
| Use the collection | [Collection guide](collection.md) |
| Read or decode downloaded files | [CSV and ZIP formats](collection-export.md) |
| Initialize a developer checkout | [Canonical setup](setup.md) |
| Run Windows localhost QA | [Local self-host](operations/local-self-host.md) |
| Deploy publicly on Linux | [Public self-host deployment](operations/self-hosted-deployment.md) |
| Deploy or update Azure | [Ordered Azure entry point](operations/azure-deployment.md) |
| Assess production acceptance | [Current acceptance matrix](operations/production-readiness.md), [dated evidence](operations/deployment-verification.md) |
| Change the schema | [Migration procedure and compatibility matrix](operations/database-migrations.md) |
| Identify SQL authorities | [Database-principal matrix](operations/database-principals.md) |
| Configure blobs, SMTP, workers and retention | [Provider runbook](operations/blob-and-service-providers.md) |
| Configure hosted native backups | [Azure native backup](operations/azure-native-backup.md) |
| Use optional custom online collection or reviewed manual recovery | [Online backup and recovery](operations/online-backup-recovery.md) |
| Perform strict offline restore | [Database backup and restore](operations/database-backup-restore.md) |
| Understand current technical contracts | [Architecture](ARCHITECTURE.md) |
| Evaluate security boundaries | [Threat model](security/data-identity-threat-model.md), [browser security](operations/browser-security.md) |
| Understand product direction | [Vision](VISION.md), [design principles](DESIGN-PRINCIPLES.md) |
| Apply current visual values | [DESIGN.md](../DESIGN.md) |
| Find historical design decisions | [Specs index](specs/README.md), [collection design records](collection.md#design-records) |
| Reproduce a walkthrough | [Demo index](demos/README.md) |
| Contribute and verify a change | [Contributing](../CONTRIBUTING.md), [development workflow](development-workflow.md) |

## Current direction

Vision and design principles own product intent. Architecture owns the implemented technical
contracts. DESIGN.md owns visual values; [design references](design/README.md) and
[experimental navigation](design-ideas/README.md) retain their distinct reference roles.
See the [navigation decision](specs/2026-09-09-refined-navigation.md) for accepted interaction scope.

## Change specifications

[Specs](specs/README.md) retain constraints, alternatives, human-validation requirements and historical
acceptance sequence. Implementation makes the corresponding living guide current; it does not turn
an old verification record into fresh evidence. The [original production audit](operations/evidence/2026-09-06-production-audit.md)
is historical context for the current acceptance matrix. The [cost worksheet](operations/deployment-costs.md)
retains its own measurement and pricing limits.

Local [test iteration](specs/2026-09-08-local-test-iteration.md),
[concurrent verification](specs/2026-09-09-concurrent-verification-gate.md) and
[forwarded metadata trust](specs/azure-forwarded-metadata-trust.md) remain distinct decisions.

## Still to be decided

Service plans, prices, feature packaging, detailed future workflows and measured scaling thresholds
require their own evidence and focused decisions. Current collection capabilities are described in
the collection guide; their existence does not establish accounting or commerce workflows.
