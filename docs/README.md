# Workbench documentation

This directory separates current product direction from the historical reasoning behind individual
changes.

## Current direction

- [VISION.md](VISION.md) defines what Workbench is for, the people it serves, and its major product
  areas.
- [DESIGN-PRINCIPLES.md](DESIGN-PRINCIPLES.md) defines the durable rules used to evaluate future
  product and technical decisions.
- [Base application architecture](specs/2026-08-31-base-application-architecture.md) defines the
  accepted React, ASP.NET Core, SQL Server, blob-provider, tenancy, identity, and hosted/self-hosted
  infrastructure direction. Implementation remains phased and tracked separately.

These documents are living documentation. They should describe the project's current direction.

## Change specifications

The [specs](specs/README.md) directory contains one dated document for each meaningful change that
needs durable requirements or design reasoning. Specs preserve context and rejected alternatives;
they do not replace the living documentation above.

## Still to be decided

The accepted base architecture deliberately does not yet define:

- service plans, prices, or feature packaging;
- detailed workflows for any product area, including inventory and purchasing;
- measured scaling thresholds that would justify new storage engines or service decomposition; and
- provider-specific details assigned to later identity, storage, operations, and deployment plans.

Those choices should be made through focused specs when evidence and concrete requirements make the
decision necessary.
