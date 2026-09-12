# Workbench product context

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

Gemstone and jewelry businesses are the primary audience, as confirmed during product
initialization. They need to understand their inventory, plan purchases, and eventually
connect operational work, financial records, and sales across a shared business context.

Hobbyists and collectors remain supported. They should be able to start with a useful
collection tool without configuring professional accounting or commerce. Professional
capability extends the same product and data model.

## Product Purpose

Workbench helps people understand what they have, what they are making or servicing,
what happened financially, and where and how they are selling. Success means useful,
connected records that preserve item history and remain understandable and retrievable.

The [product vision](docs/VISION.md) owns the long-term direction. This context records
the confirmed business priority and summarizes current guidance for future interface work;
the linked living guides remain authoritative for detailed behavior.

## Positioning

One fully open-source product connects gemstone and jewelry inventory, work orders,
bookkeeping and accounting, and commerce. This is the intended product direction, not
a claim that all four areas are implemented. Local, self-hosted, and hosted use share
the product; hosted convenience must not depend on proprietary core capabilities.

## Operating Context

Users work within an authenticated business or tenant on desktop and mobile web.
Collection records, photographs, acquisition context, and acquisition paperwork support
understanding individual pieces. Supplier purchase drafts retain shopping lists, source
links, and reference prices so members can resume planning later.

Saved content survives sessions. Unsaved drafts and uncertain-request retry state have
the limits documented in the workflow guides; future interfaces must not imply that
in-memory work is durably saved.

## Capabilities and Constraints

- [Collection](docs/collection.md): individually tracked pieces, one current private
  photograph per item, search, descriptive corrections, archive and restore, shared
  acquisition context, acquisition documents, and CSV or ZIP exports.
- [Purchasing](docs/purchasing.md): supplier purchase drafts with shopping-list entries,
  source links, optional reference prices, explicit save and conflict recovery, and draft
  deletion. Drafts create no inventory, acquisition, invoice, payment obligation, or
  accounting entry. Reference prices do not establish a calculated order total.
- Work orders, general-ledger accounting, and commerce remain future workflows.
  Public collection profiles and community are exploratory, not committed scope.
- Tenant isolation and explicit authorization are invariants. Important state changes
  must be explainable; concurrent edits and uncertain outcomes require deliberate recovery.
- Archive is not sale, disposal, or ownership transfer. Downloaded collection exports
  are portable records, not restorable application backups.
- The existing React client and ASP.NET Core API ship as one same-origin release unit
  with SQL Server tenant isolation; [architecture](docs/ARCHITECTURE.md) owns the contract.
- Specific business segments, detailed future workflows, service plans, pricing, and
  packaging remain open decisions. Do not invent them from the business priority.

## Brand Commitments

The product is Workbench, by The White Stag Collection. Preserve the existing Workbench
bench-pin wordmark and stag assets. [DESIGN.md](DESIGN.md) owns the accepted visual system;
this product record does not replace it. Branding is governed separately from the
software license by the [trademark policy](TRADEMARKS.md).

## Evidence on Hand

Current behavior is documented in the collection and purchasing guides. The
[demo index](docs/demos/README.md) links walkthroughs and reproduction procedures;
the [acceptance matrix](docs/operations/production-readiness.md) records dated operational
evidence. Tests and walkthroughs do not establish independent user usability or broader
production acceptance. Do not turn sample data into customers, testimonials, or claims.

## Product Principles

1. Prioritize business jobs while keeping the first useful collection workflow approachable.
2. Preserve domain truth, ownership boundaries, and explainable changes as capability grows.
3. Use ledger semantics for financial truth, without treating every operational document
   as a financial posting.
4. Keep one open-source product and preserve useful data ownership and portability.
5. Make configuration earn its complexity and defer expensive irreversible decisions
   until supported by evidence.

These summarize the [design principles](docs/DESIGN-PRINCIPLES.md), which retain the
full product and engineering guidance.

## Accessibility & Inclusion

Preserve the established keyboard operation, visible focus, accessible control names,
mobile and enlarged-text layouts, and reduced-motion, reduced-transparency, and
forced-colors support documented in [DESIGN.md](DESIGN.md). No additional audience-specific
accessibility requirements were established during initialization.
