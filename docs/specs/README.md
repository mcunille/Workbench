# Workbench specifications

Specs capture the requirements and reasoning for one meaningful change. They are durable design
history, not a backlog and not a substitute for current documentation.

## Find a decision

| Area | Records |
| --- | --- |
| Foundation | [Base architecture](2026-08-31-base-application-architecture.md), [application foundation](2026-08-31-application-foundation.md), [data/identity/tenancy](2026-09-01-data-identity-tenancy.md) |
| Collection | [Scenario and human validation](2026-09-06-first-hobbyist-scenario.md), [inventory foundation](2026-09-06-inventory-domain-foundation.md), [H1–H9 design links](../collection.md#design-records) |
| Purchasing | [Small-business purchase orders and purchase finances (proposed)](2026-09-11-purchase-orders-and-purchase-finances.md), [PO-01 draft supplier orders (proposed)](2026-09-11-po-01-draft-supplier-orders.md) |
| Visual decisions | [UI guidance](2026-09-06-ui-design-guidance.md), [Tanzanite acceptance sequence](2026-09-08-tanzanite-visual-language.md), [floating labels](2026-09-08-floating-label-fields.md), [navigation](2026-09-09-refined-navigation.md) |
| Providers and recovery | [Blob/operational providers](2026-09-05-blob-operational-providers.md), [online backup/manual recovery](2026-09-07-online-backup-and-manual-recovery.md) |
| Deployment | [Azure](2026-09-05-azure-deployment.md), [forwarded trust](azure-forwarded-metadata-trust.md), [release verification fixes](azure-release-verification-fixes.md), [security controls](2026-09-08-production-security-controls.md), [local self-host update](local-self-host-update.md) |
| Development and verification | [Local iteration](2026-09-08-local-test-iteration.md), [concurrent gate](2026-09-09-concurrent-verification-gate.md), [worktree environments](2026-09-09-worktree-development-environments.md) |

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
