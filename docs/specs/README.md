# Workbench specifications

Specs capture the requirements and reasoning for one meaningful change. They are durable design
history, not a backlog and not a substitute for current documentation.

## Find a decision

[Beta API lifecycle and purchasing consolidation](2026-09-16-beta-api-lifecycle.md)
records the approved transition for issue #120.

[PO-07 bookkeeping foundation and prerequisites](2026-09-20-po-07-deposits-and-payments.md)
proposes ledger-backed payments, structured supplier bills, and prerequisite accounting stories.
It remains a design proposal, not implemented bookkeeping.

[BK-01 accounting configuration, accounts, and authorization](2026-09-21-bk-01-accounting-foundation.md)
records the implemented general-account setup and two-role authorization model,
and links the separate reporting, portability, migration, and jurisdiction-reporting design issues.
Setup completeness does not activate bookkeeping.

[BK-03 corrections and period controls](2026-09-24-bk-03-corrections-and-period-controls.md)
records the accepted design for atomic reversal/replacement and closed-period enforcement on the
BK-02 journal. Implementation is pending; production close remains gated by BK-09–11.

| Area | Records |
| --- | --- |
| Foundation | [Base architecture](2026-08-31-base-application-architecture.md), [application foundation](2026-08-31-application-foundation.md), [data/identity/tenancy](2026-09-01-data-identity-tenancy.md) |
| Collection | [Scenario and human validation](2026-09-06-first-hobbyist-scenario.md), [inventory foundation](2026-09-06-inventory-domain-foundation.md), [H1–H12 design links](../collection.md#design-records) |
| Purchasing | [Small-business purchase orders and purchase finances (proposed)](2026-09-11-purchase-orders-and-purchase-finances.md), [PO-01 draft supplier orders](2026-09-11-po-01-draft-supplier-orders.md), [Supplier social handles](2026-09-20-supplier-profiles.md), [PO-02 supplier identity and references](2026-09-11-po-02-supplier-identity-and-references.md), [PO-03 itemized quantities and prices](2026-09-16-po-03-itemized-quantities-and-prices.md), [PO-04 commitment and amendments](2026-09-17-po-04-commitment-and-amendments.md), [PO-05 discounts and additional charges](2026-09-16-po-05-discounts-and-charges.md) |
| Visual decisions | [UI guidance](2026-09-06-ui-design-guidance.md), [Tanzanite acceptance sequence](2026-09-08-tanzanite-visual-language.md), [floating labels](2026-09-08-floating-label-fields.md), [navigation](2026-09-09-refined-navigation.md) |
| Providers and recovery | [Blob/operational providers](2026-09-05-blob-operational-providers.md), [online backup/manual recovery](2026-09-07-online-backup-and-manual-recovery.md) |
| Deployment | [Azure](2026-09-05-azure-deployment.md), [forwarded trust](azure-forwarded-metadata-trust.md), [release verification fixes](azure-release-verification-fixes.md), [security controls](2026-09-08-production-security-controls.md), [local self-host update](local-self-host-update.md) |
| Development and verification | [Current schema bookkeeping](2026-09-21-current-schema-bookkeeping.md), [Local iteration](2026-09-08-local-test-iteration.md), [concurrent gate](2026-09-09-concurrent-verification-gate.md), [duration balancing](2026-09-16-duration-balanced-server-partitions.md), [test suite efficiency](2026-09-16-test-suite-efficiency.md), [worktree environments](2026-09-09-worktree-development-environments.md) |

Current instructions are indexed by [task](../README.md). [DESIGN.md](../../DESIGN.md) owns current
visual values; [collection export](../collection-export.md) owns current CSV/ZIP formats.

## When to write a spec

Write a spec when a change introduces meaningful product behavior or changes a durable boundary,
including:

- a workflow or accounting rule;
- a public contract or data model;
- a security or privacy property;
- a dependency, storage, deployment, or operational commitment;
- behavior shared by more than one product area.

Small corrections, routine maintenance, and changes already governed by an accepted spec generally
do not need a new one.

## Naming

Use a date and a short descriptive name:

```text
YYYY-MM-DD-short-topic-name.md
```

## Status

Every spec begins with a status:

- **Proposed** — under discussion and not approved for implementation.
- **Accepted** — approved as the direction to implement.
- **Implemented** — reflected in the product and its living documentation.
- **Superseded** — replaced by a linked, newer spec.

Acceptance of a spec approves its direction. It does not imply that the behavior has been built or
deployed.

## Suggested structure

Scale the document to the change, but normally cover:

1. summary and status;
2. problem and affected users;
3. goals, non-goals, constraints, and invariants;
4. current behavior and evidence;
5. proposed design and affected boundaries;
6. alternatives and why they were rejected;
7. security, privacy, failure, and recovery behavior;
8. compatibility, migration, and rollback;
9. verification and success criteria;
10. residual risks and unresolved questions.

When implementation makes a spec true, update the appropriate living documentation and change the
spec status to **Implemented**. Preserve the spec so future contributors can understand why the
current design exists.

The implemented [PO-03 specification](2026-09-16-po-03-itemized-quantities-and-prices.md) consolidates supplier-based line pricing, its V4 compatibility contract and migration requirements.
