# Workbench documentation

This directory separates current product direction from the historical reasoning behind individual
changes.

Start with the [setup and installation guide](setup.md) to initialize a checkout and log in.

## Current direction

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

The accepted [Azure deployment specification](specs/2026-09-05-azure-deployment.md) defines
scale-to-zero hosting and portable self-hosting. The [Azure runbook](operations/azure-deployment.md)
and [Compose runbook](operations/self-hosted-deployment.md) describe the release configuration and
explicit operational gates. Hosted deployment, cold-start measurements, and cloud recovery evidence
remain pending; checked-in infrastructure is not evidence that an environment has been deployed.
The [cost worksheet](operations/deployment-costs.md) records public retail rates and unmeasured
usage scenarios. The [verification record](operations/deployment-verification.md) separates local
evidence from pending hosted acceptance.

## Still to be decided

The accepted base architecture deliberately does not yet define:

- service plans, prices, or feature packaging;
- detailed workflows for any product area, including inventory and purchasing;
- measured scaling thresholds that would justify new storage engines or service decomposition; and
- provider-specific details assigned to later identity, storage, operations, and deployment plans.

Those choices should be made through focused specs when evidence and concrete requirements make the
decision necessary.
