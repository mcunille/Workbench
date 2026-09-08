# Workbench documentation

This directory separates current product direction from the historical reasoning behind individual
changes.

Start with the [setup and installation guide](setup.md) to initialize a checkout and log in.
The [production operations audit](operations/production-readiness.md) distinguishes historical gaps
from the [completed Azure launch](operations/deployment-verification.md#azure-public-launch-2026-09-08-utc)
and the remaining scale, monitoring, cost, and portability checks.

## Current direction

- [Tanzanite style reference](../DESIGN.md) documents the implemented application visual language,
  including color tokens, typography, glass materials, branding, components, and reusable design briefs.
- [VISION.md](VISION.md) defines what Workbench is for, the people it serves, and its major product
  areas.
- [DESIGN-PRINCIPLES.md](DESIGN-PRINCIPLES.md) defines the durable rules used to evaluate future
  product and technical decisions.
- [ARCHITECTURE.md](ARCHITECTURE.md) is the authoritative living description of the accepted React,
  ASP.NET Core, SQL Server, blob-provider, tenancy, identity, and hosted/self-hosted infrastructure
  direction. Implementation remains phased and tracked separately.

These documents are living documentation. They should describe the project's current direction.

## Change specifications

For scoped design, planning, delegation, debugging, and implementation review practices, see
[the development workflow](development-workflow.md). Read the section relevant to the task.

The [specs](specs/README.md) directory contains one dated document for each meaningful change that
needs durable requirements or design reasoning. Specs preserve context and rejected alternatives;
they do not replace the living documentation above.

The accepted [UI design guidance](specs/2026-09-06-ui-design-guidance.md) applies UI Skills research
to Workbench's visual system, progressive capability, desktop/mobile layouts, accessibility, and
light/dark themes. The Tanzanite reference above supersedes its original bronze palette. The
[reviewed mockup](design/README.md) preserves the visual reference and verification limits.
Design acceptance does not imply an implemented application UI or accepted domain workflows.

The proposed [first hobbyist scenario](specs/2026-09-06-first-hobbyist-scenario.md) applies that
guidance to a complete collection journey: save an item, recognize it by photograph, find it,
and keep it accurate. It defines four value-delivering stories and shared completion criteria;
H1 has an accepted [implementation design](specs/2026-09-06-h1-collection-notebook.md) and
[inventory domain foundation](specs/2026-09-06-inventory-domain-foundation.md) informed by GemInv.
H2 has an accepted [photograph design](specs/2026-09-07-h2-item-photographs.md), including browser
preparation, bounded server sanitation, and atomic photo replacement. H3 has an accepted
[search design](specs/2026-09-07-h3-collection-search.md), including literal SQL search,
bounded pagination, and private in-memory navigation state. H4 follows the accepted
[editing design](specs/2026-09-07-h4-item-editing.md), with checked saves and explicit conflict recovery;
these increments do not complete the entire scenario.

The accepted [H5 archiving design](specs/2026-09-07-h5-item-archiving.md) extends the collection with
confirmed, version-checked archiving, active-only browsing/search, and read-only access to retained
records and photographs through their existing links.

Archive recovery follows the [H6 design](specs/2026-09-07-h6-archive-recovery.md): a separate
searchable Archive, retained read-only details/photos, and version-checked restoration to the
active collection. See the [walkthrough](demos/h6/README.md) and migration runbook for evidence
and release compatibility. No permanent deletion is provided.

The accepted [base-architecture specification](specs/2026-08-31-base-application-architecture.md) is
the decision record behind `ARCHITECTURE.md`.

The accepted [application-foundation specification](specs/2026-08-31-application-foundation.md)
defines the first implementation phase: the independently developed React client and ASP.NET Core
API, their typed same-origin release unit, health contracts, hardened container, and verification
gates.

The accepted [data, identity, and tenancy specification](specs/2026-09-01-data-identity-tenancy.md)
defines the next implementation phase: authoritative SQL persistence, database-enforced tenant
isolation, built-in identity, durable sessions, explicit migrations, and restore invalidation.

The accepted [blob and operational providers specification](specs/2026-09-05-blob-operational-providers.md)
defines immutable blob storage, SMTP delivery, shared abuse controls, and durable workers. The
[provider runbook](operations/blob-and-service-providers.md) covers configuration, retention,
reconciliation, paired backups, restore verification, and provider migration.

The accepted [online backup and manual recovery direction](specs/2026-09-07-online-backup-and-manual-recovery.md)
requires uninterrupted backup collection and SQL-authoritative reconciliation, with explicit
missing-file acceptance and tenant notices after manual recovery. The
[online backup runbook](operations/online-backup-recovery.md) documents the separate collector and
guarded recovery commands; hosted verification remains required. Existing offline maintenance
commands retain their documented requirements.

The accepted [Azure deployment specification](specs/2026-09-05-azure-deployment.md) defines
scale-to-zero hosting and portable self-hosting. The [Azure runbook](operations/azure-deployment.md)
and [Compose runbook](operations/self-hosted-deployment.md) describe the release configuration and
explicit operational gates. The first hosted deployment and scoped recovery drill are recorded;
measured cold starts, hosted scale-out and broader recovery coverage remain pending.
The [cost worksheet](operations/deployment-costs.md) records public retail rates and unmeasured
usage scenarios. The [verification record](operations/deployment-verification.md) separates local
evidence from hosted launch results and outstanding acceptance checks. Use the
[Azure release checklist](operations/azure-release.md) for subsequent upgrades and cleanup.

## Still to be decided

The accepted base architecture deliberately does not yet define:

- service plans, prices, or feature packaging;
- detailed workflows for any product area, including inventory and purchasing;
- measured scaling thresholds that would justify new storage engines or service decomposition; and
- provider-specific details assigned to later identity, storage, operations, and deployment plans.

Those choices should be made through focused specs when evidence and concrete requirements make the
decision necessary.
