# First hobbyist scenario: remember and find my collection

**Status:** Partially delivered. H1 was merged in [PR #37](https://github.com/mcunille/Workbench/pull/37)
on 2026-09-07. H2 is implemented according to its accepted [design](2026-09-07-h2-item-photographs.md)
with browser-side preparation. H3 is implemented according to its accepted
[search design](2026-09-07-h3-collection-search.md).
H4 follows its accepted [editing design](2026-09-07-h4-item-editing.md).
This is not a committed sprint forecast.

**Delivery tracking:** [Scenario issue #43](https://github.com/mcunille/Workbench/issues/43).
GitHub issues own story acceptance criteria, implementation progress, dependencies and completion
status. This document retains the scenario, product boundaries and links to accepted specifications.
Update both when an approved decision changes scope; do not maintain a second task checklist here.

## Direction and current experience

Start with a hobbyist who owns individually identifiable gemstones or jewelry and currently
remembers them through boxes, photographs, or informal notes. Their immediate question is:
“What is this piece, and where did I put it?” They may not know its formal classification.
This is a working persona to validate with collectors, not a claim from user research.

Hobbyist-first follows the [product vision](../VISION.md) and
[progressive-capability principle](../DESIGN-PRINCIPLES.md). Build a useful collection tool with
durable records and room to grow; do not require accounting, a business profile, or a permanent
“hobbyist mode.” Professionals should eventually extend the same records and workflows.

H1 now provides a durable collection notebook: create an individual item with a name, optional
notes and storage location, browse it in the responsive gallery or compact List view, and reopen
its details after reload or another authenticated session. Saves are duplicate-safe and unsaved
work is protected. H2 extends these saved records with one photograph, replacement and removal;
H3 adds literal search across the entire collection with navigation-state restoration;
H4 adds descriptive editing with checked versions and recoverable conflict drafts.

Apply the accepted [UI guidance](2026-09-06-ui-design-guidance.md) and
[implemented refinement](../design/h1-refinement/README.md): original stag branding, bronze accents,
warm neutral and charcoal surfaces, responsive cards with Grid/List switching, selective header
translucency, opaque reading surfaces and accessibility fallbacks. System/Light/Dark appearance
must preserve task state. Extend these views rather than reverting to the earlier list-only proposal.
The older [mockup](../design/README.md) does not authorize its additional filters or financial widgets.

A tenant and authorized account must already be available through existing setup or invitation
flows. Public registration, tenant provisioning and production deployment are separate scenarios.

## Scenario outcome

**A hobbyist can record a real item on their phone, recognize it by its photograph, return later
on desktop or mobile, find it, and keep its description and storage location accurate.**

Example: Alex brings home a blue stone, saves “Blue stone from the September fair” with location
“Tray A, slot 3,” and adds a photograph. On a later visit Alex searches for “September,” opens the
matching item, and checks the photograph and location. After moving it, Alex changes its location
to “Display box” and can retrieve that updated record after signing in again.

Success means completing that loop with persisted user data, without developer intervention or
configuring purchasing, bookkeeping, commerce, or a taxonomy. H1 alone does not complete the loop.
A photo-free subset must not be described as completion of the photo-based scenario.

## Deliverables and issue ownership

Deliver in the following sequence, with each increment usable in the running application. Technical
tasks belong to their story; a database, endpoint or component alone is not a delivered user story.
Issue status is authoritative; H1's merged delivery is recorded here as a historical milestone.

| Story | User value and scope | Tracking |
| --- | --- | --- |
| H1 — Keep a record of an item | Save a name, notes and location; browse Grid/List and reopen the durable record. Delivered in PR #37. | [#39](https://github.com/mcunille/Workbench/issues/39) |
| H2 — Recognize a piece from its photograph | Add, replace or remove one photo on a saved item; show it in gallery, compact list and details. Preserve the existing photo on failure. | [#40](https://github.com/mcunille/Workbench/issues/40) |
| H3 — Find an item when I need it | Search names, notes and locations across the entire authorized collection, including pagination. Preserve query, view and position when returning from details. | [#41](https://github.com/mcunille/Workbench/issues/41) |
| H4 — Keep the record accurate | Edit name, notes and location without changing identity. Preserve recoverable edits and detect concurrent changes rather than silently overwriting them. | [#42](https://github.com/mcunille/Workbench/issues/42) |

H1–H4 provide the collection workflow above. All later stories build on H1. H3 does not require
photos to search text, but includes thumbnails when H2 is available. H4 verifies that H3 search
reflects saved edits. The [scenario issue](https://github.com/mcunille/Workbench/issues/43) tracks
the integrated journey and collector usability validation beyond completion of the individual stories.

## Product and design boundaries

The accepted [H1 specification](2026-09-06-h1-collection-notebook.md) and
[inventory domain foundation](2026-09-06-inventory-domain-foundation.md) settle the existing
identity, API and schema boundaries. Remaining stories extend those records and contracts.

- One record in this scenario represents one individually tracked holding. Bulk stock, parcels,
  quantities, sets and component relationships remain later workflows under the domain foundation.
- A descriptive owner name is the only required user-entered field. Notes and storage location are
  optional plain text. Names need not be unique; item identifiers remain stable.
- H2 adds one optional private photograph per saved item. Missing photos retain neutral
  placeholders. Photo removal does not remove the item or promise immediate physical erasure;
  existing blob retention and recovery rules apply.
- Location describes the whereabouts of an individual object; it does not create a structured
  location-management or stock-balance subsystem.
- Do not collect price, currency, valuation, measurements or formal classification in this scenario.
  Acquisition, stock and domain-specific attributes need their own requirements.
- Collection records and photographs remain private to the authorized tenant, including direct
  identifier and image requests. Authorization is enforced by the server.

The accepted [H2 design](2026-09-07-h2-item-photographs.md) settles browser preparation,
file limits, orientation/color, thumbnails, privacy, validation, replacement and removal.
The accepted [H3 design](2026-09-07-h3-collection-search.md) settles literal matching,
pagination, query limits, indexing, and in-memory navigation-state handling.
The accepted [H4 design](2026-09-07-h4-item-editing.md) settles concurrency-token contracts,
safe conditional retry, and explicit conflict recovery without expanding the domain scope.

## Completion and validation

Each story issue carries its acceptance criteria and shared quality requirements: desktop/mobile
usability, keyboard and accessible feedback, both themes, tenant isolation, truthful save/upload
outcomes, failure recovery, focused tests and relevant concurrency/storage verification. Follow
[CONTRIBUTING](../../CONTRIBUTING.md) for application gates and generated contracts, and the existing
migration and recovery runbooks. Include an updated narrated Playwright walkthrough in delivery PRs.

The [scenario issue](https://github.com/mcunille/Workbench/issues/43) owns the final demonstration:
phone creation and photo upload, desktop search and editing, then verification from mobile, including
failure and conflict cases. It also tracks trying the journey with a hobbyist without coaching and
recording completion and hesitation points. Automated tests alone do not establish usability.

## Deliberately later

No dashboards, public sharing, bulk import/edit, saved views, configurable columns, multiple photos,
formal gem taxonomy, valuations, purchase orders, bookkeeping, work orders or commerce in this scenario.
The subsequent [H5 design](2026-09-07-h5-item-archiving.md) adds safe record archiving with retained
read-only links. Deletion and collection export remain separate increments. Evaluate the initial
release as a limited collection pilot, not a complete professional inventory system.

Choose subsequent complete scenarios from observed needs:

1. **Care for my growing collection:** safely archive mistakes and export records, then add grouping
   or richer identification where collectors need it. Portability should not be reserved for experts.
2. **Remember an acquisition:** connect items to seller, date, provenance and supporting documents;
   define financial semantics before adding amounts or accounting effects.
3. **Manage many items efficiently:** import, batch operations, denser views and saved searches with
   validation, partial-failure recovery and the same underlying records.
4. **Operate a business:** add roles, work orders, financial controls and commerce as separately
   accepted end-to-end scenarios. Do not expose empty destinations in the hobbyist experience.
