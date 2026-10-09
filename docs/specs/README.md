# Workbench specifications

Specs retain requirements and decisions that still need a separate home, including unfinished
designs. After delivery, distill useful constraints into maintained guides and retire redundant
specs. Git preserves chronology; dated evidence records retain their specific verification scope.

## Find a decision

[BK-07 durable financial evidence](2026-09-28-bk-07-durable-financial-evidence.md) implements
atomic evidence holds, configured retention, authorized disposal, and paired recovery protection.
The approved implementation includes capture, retention, disposal, UI, recovery and legacy backfill.
Current-source full gates, preview evidence and independent review are recorded separately. Production
bookkeeping remains unavailable; maintained invariants live in [Accounting](../accounting.md#durable-financial-evidence).

[Gemological reference library](2026-09-29-gemological-reference-library.md) proposes a sourced
Workbench catalog curated through a separate service-admin editor, with tenant additions and
field-level overrides. The first release is a standalone reference; numerical type properties
and collection-item links are later work.

[GEM-02 shared reference persistence and reads](2026-10-01-gem-02-shared-reference.md)
defines shared storage, attributed claims, bounded authenticated reads, and SQL permissions.
Its shared foundation is implemented; the scoped record includes verification and the remaining
full-SQL deadline limitation. Pilot content, publishing, tenant overlays and browser screens remain
separate stories in the proposed parent roadmap.

[MCP agent interface](2026-09-28-mcp-agent-interface.md) proposes full agent-eligible business
workflow coverage through tenant-owned service principals with predefined role assignments.
It is a product and architecture draft; identity/tenant administration remains human-only.
Its [milestones and agent handoff stories](2026-09-28-mcp-milestones-and-stories.md) define delivery
outcomes, dependencies, validation evidence, and parallel work.

[Raised content workspace](2026-09-25-raised-content-workspace.md) records the implemented
desktop atmospheric content sheet in front of a solid canvas navigation backdrop, retaining
Tanzanite and Quartz edge materials. The owner approved the local visual direction;
current-source container smoke, full browser coverage, and scoped review passed.
The refreshed final-source preview passed desktop and mobile visual inspection.

[API lifecycle](../api-lifecycle.md) owns the beta contract, purchasing compatibility inventory,
approved historical replay removal and coordinated rollout/recovery boundaries.

[PO-07 bookkeeping foundation and prerequisites](2026-09-20-po-07-deposits-and-payments.md)
retains the public payment workflow and remaining bookkeeping prerequisites. BK-01–07 supply
internal foundations with their own documented boundaries; production bookkeeping remains unavailable.

[BK-01 accounting configuration, accounts, and authorization](2026-09-21-bk-01-accounting-foundation.md)
records the implemented general-account setup and two-role authorization model,
and links the separate reporting, portability, migration, and jurisdiction-reporting design issues.
Setup completeness does not activate bookkeeping.

[BK-03 corrections and period controls](2026-09-24-bk-03-corrections-and-period-controls.md)
records the implemented internal atomic reversal/replacement and closed-period enforcement on the
BK-02 journal. Production close remains gated by BK-09–11; no production correction adapter is enabled.

[BK-04 classified purchase recognition](2026-09-25-bk-04-classified-purchase-recognition.md)
records the approved, implemented internal recognition and invoice matching foundation, classified
cost/tax treatment and immutable receipt-recognition records. `BookkeepingAvailable` remains false;
no production posting adapter or public financial-write UI is enabled.

[BK-06 supplier open items and allocations](2026-09-27-bk-06-supplier-open-items-and-allocations.md)
records the approved, implemented internal payment/allocation/correction boundary and authorized
four-control read APIs with frozen posting/recorded cutoffs. BK-05 is merged. Targeted SQL/HTTP,
mutation and supported-base upgrade/guarded SQL recovery evidence is recorded; complete-branch
review, full verification, container smoke and retained-preview inspection remain pending.
Reporting cost is unmeasured and grows with history. No runtime write grants, public financial
entry or production bookkeeping activation are delivered by BK-06; BK-07 supplies physical holds.

| Area | Records |
| --- | --- |
| Foundation | [Base architecture](2026-08-31-base-application-architecture.md), [data/identity/tenancy](2026-09-01-data-identity-tenancy.md). Current release-unit rationale lives in [Architecture](../ARCHITECTURE.md#application-structure), and generated-contract/error guidance in [API lifecycle](../api-lifecycle.md#generated-contracts-and-api-errors). |
| Collection | [Collector journey and validation](../collection.md#collector-journey-and-validation), [inventory foundation](2026-09-06-inventory-domain-foundation.md), [collection design links](../collection.md#design-records) |
| Purchasing | [Small-business purchase orders and purchase finances (proposed)](2026-09-11-purchase-orders-and-purchase-finances.md), [current purchase workflows](../purchasing.md), [technical constraints and rationale](../ARCHITECTURE.md#purchase-orders), [PO-04 commitment and amendments](2026-09-17-po-04-commitment-and-amendments.md) |
| Visual decisions | [Current UI guidance](../../DESIGN.md), [floating labels](../../DESIGN.md#text-input), [original navigation interactions](2026-09-09-refined-navigation.md), [raised content workspace](2026-09-25-raised-content-workspace.md) |
| Providers and recovery | [SQL-authoritative recovery policy](../operations/online-backup-recovery.md#sql-authoritative-recovery-policy). Provider rationale lives in [Architecture](../ARCHITECTURE.md#blob-storage); operational constraints and retained acceptance requirements live in the [provider runbook](../operations/blob-and-service-providers.md#retained-provider-acceptance-requirements). |
| Deployment | [Azure design](2026-09-05-azure-deployment.md), [bootstrap and forwarded trust](../operations/azure-deployment.md), [security controls](../operations/azure-security-controls.md) |
| Development and verification | [Current schema contract](../operations/database-migrations.md#maintaining-the-current-schema-contract), [Local iteration](../../CONTRIBUTING.md#focused-local-iteration), [concurrent gate](../../tests/README.md#gate-provenance-and-scheduling), [duration balancing](../../tests/README.md#refresh-server-timing-data), [test suite efficiency](../../tests/README.md), [worktree environments](2026-09-09-worktree-development-environments.md) |

Local self-host update instructions and retained design rationale live in the
[update runbook](../operations/local-self-host.md#update-an-existing-installation); dated checks remain
in the [verification record](../operations/local-self-host-update-verification.md).

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
spec status to **Implemented**. Preserve the reasoning so future contributors can understand why
the current design exists. Retain specs with distinct constraints, alternatives, unresolved work,
or required historical evidence. During an authorized [documentation cleanup](../../.agents/skills/doc-audit/SKILL.md),
a redundant spec may be retired after its durable knowledge is distilled into maintained docs
and its references and retention needs are accounted for. Implementation status alone is not
evidence that a spec is redundant.
