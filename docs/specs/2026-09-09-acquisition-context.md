# Remember an acquisition

**Status: Implemented — owner approved implementation on 2026-09-09.**

Scenario [#73](https://github.com/mcunille/Workbench/issues/73); first delivery
[H9 / #74](https://github.com/mcunille/Workbench/issues/74). Product scope is accepted;
the schema and API below are accepted. Approval implements H9 only. H10–H12 remain separate
deliveries, with the extension boundaries below preventing H9 from obstructing them.

## Purpose and current evidence

A collector can record how a piece entered their collection and retrieve or correct that context
after signing in again. Gift, inheritance, trade, and incomplete knowledge are ordinary cases.
These are collector-recorded facts, not independently verified provenance.

At inspected revision `00d61e8f223a621fc462911844cf4f541705fcfe`, individual items already have
permanent IDs, optional notes/location, archive state, and SQL rowversions. H4 defines checked
updates, recoverable in-session drafts, and explicit conflict reconciliation. H1 creation has
duplicate-safe request identifiers. Reuse these conventions. The
[inventory foundation](2026-09-06-inventory-domain-foundation.md) prohibits a universal purchase
source FK on Items; acquisitions therefore have their own identity and tenant-qualified links.

## H9 fields and vocabulary

| Field | Proposed contract |
| --- | --- |
| Method | Required explicit choice: Purchase, Gift, Inheritance, Trade, Other, or Unknown. No preselected Purchase. |
| Source | Optional plain text, trimmed, maximum 200 characters; blank is null. No supplier/person record or lookup. |
| Acquired date | Optional year, month, and day components, with precision implied by the populated components. |
| Provenance notes | Optional plain text, maximum 4,000 characters; whitespace-only becomes null, otherwise preserve text. |

Source labels are “Purchased from”, “Gift from”, “Inherited from”, “Traded with”, and “Source”
for Other/Unknown. Changing method never silently clears source text. Unknown is an explicit
method choice; absent source/date display as “Not recorded”. Other can be explained in notes
without making notes required. There are no monetary fields, seller requirements, orders,
payments, valuations, ownership status changes, or supplier management.

Dates support Unknown, Year, Month, or Exact date. Year is 1–9999; month requires year and is
1–12; day requires both and must form a valid Gregorian date, including leap years. Null means
unknown; do not substitute January 1, today's date, zero, or a time zone. The UI presents a
precision selector and only the applicable labeled inputs. Display year-only and month-only
values without inventing precision. Future dates are rejected against the current UTC calendar
date at the supplied precision; explain this as the date acquired, not an expected delivery.

## Relationship and entry flow

An acquisition describes one origin event. One acquisition may ultimately describe several
individual items; an item has zero or one current acquisition link in this scenario. This is
descriptive collection context, not a universal restriction on future receipt/allocation history.
Use a separate `Inventory.AcquisitionItems` table with tenant-qualified acquisition and item FKs
and uniqueness on `(TenantId, ItemId)`. Do not put an acquisition FK on Items.

H9 offers Add acquisition from the details of an existing active item. After normal manual item
creation succeeds, the same action is available on its detail screen. Save the item first using
its existing safe retry contract; acquisition creation never creates an item. If acquisition
save fails, the saved item remains usable without an acquisition. Helper text: “Record when and
how you got this piece.” No acquisition is required during item entry.

Saving creates one acquisition and its link in one transaction. Item details then show its
method, source, date precision, and provenance notes, with Edit acquisition. No acquisition
list, multi-item picker, relink/unlink action, or acquisition deletion ships in H9.

H10 owns link correction: explicitly remove or replace a mistaken link without deleting either
identity; serialize changes on the affected items and acquisition, check their versions, and
reject competing links. Shared-context edits must show that they affect all linked pieces and
allow navigation between them. These commands and UI are not exposed by H9.

Archived item details retain readable acquisition context. Add/edit from an archived item is
rejected in both API and SQL even with a current token. Archive/restore preserves the link and
acquisition. For H10, edits through an active linked item update shared context also visible
from archived links; archive does not freeze a historical copy or confer disposal semantics.

## Persistence, isolation, and commands

Add `Inventory.Acquisitions` with server-generated UUID, TenantId, method, nullable source/date
components/notes, server CreatedAtUtc, creation request UUID, and SQL rowversion. Enforce method,
date structure, lengths, tenant-qualified identity, and creation request uniqueness in SQL.
Capture the normalized original creation payload and original item identifier as immutable replay
evidence in a separate tenant-owned creation record, atomically with acquisition/link creation.
Edits cannot alter that evidence. Retain it for the lifetime of the acquisition.

Every new table participates in the existing tenant RLS filter/block policy, with tenant-qualified
FKs, runtime read access, and no runtime direct INSERT/UPDATE/DELETE grants. Restricted commands
validate and mutate through ownership chaining under caller tenant context, without owner
impersonation. Neither API identifiers nor a link can escape tenant isolation. Validate oversized
SQL command inputs before any narrowing conversion. Provisioning and readiness check new grants.

Creation locks the target item, verifies active state and expected item version, checks replay
evidence, creates both rows atomically, and advances the item version. Editing locks the target
item first, verifies active state/link and expected item version, then conditionally updates the
acquisition using its expected version. It advances both versions. Consistent item-then-acquisition
lock ordering serializes edit/archive races. Existing item/photo/archive commands continue to
participate through the item version; no stale preflight read substitutes for the SQL guard.

## API and safe retries

All routes use existing authentication, tenant resolution, antiforgery, Problem Details, and
private no-store response conventions. Reject unknown request properties; never accept tenant,
server IDs, timestamps, or archive changes in the payload. Version tokens are opaque base64
encodings of exactly eight bytes. Optional descriptive fields are replaced, not patched.

| Route | Behavior |
| --- | --- |
| `GET /api/items/{itemId}/acquisition` | 200 with nullable acquisition and current item version; null is a valid empty state. Missing/foreign item is 404. |
| `POST /api/items/{itemId}/acquisition` | Required creationRequestId, expectedItemVersion, method, optional source/year/month/day/notes. 201 on committed creation; 200 on an identical replay with current saved context. |
| `PUT /api/items/{itemId}/acquisition/{acquisitionId}` | Required expectedItemVersion and expectedAcquisitionVersion plus the complete descriptive payload. 200 only after commit. |

Responses include acquisition ID, normalized fields, acquisition version, and current item version.
An inaccessible acquisition or mismatched item/acquisition pair returns the same 404. Validation
returns field-keyed 400. Accessible stale versions return 409 `acquisition_version_conflict`;
an existing different link returns 409 `item_acquisition_conflict`; an archived target returns
409 `item_archived`. Do not return foreign data in error bodies.

Creation replay matches the original normalized fields and item, not the mutable current values
or original concurrency token. A reused UUID with different fields/item returns 409
`acquisition_request_conflict`. An identical replay can read the retained current context even
after archive; it performs no new write. Unique constraints plus a transaction handle simultaneous
identical requests. Different UUIDs targeting one item cannot create two acquisitions or leave an
orphan. Creation rollback removes the acquisition, link, and replay evidence together.

Disable duplicate submits while pending and retain creation UUID, submitted payload, and versions
after a transport failure. Offer retry of that same request and review of saved state. Never
silently rotate UUIDs or acquire a fresh version for a retry. After an uncertain creation, resolve
or explicitly review its result before treating modified input as an edit or a new request.

Edits follow H4's conditional retry behavior without an edit-operation ledger: a lost response
may turn the retry into a conflict. Preserve input and say the save could not be confirmed.
Showing equal saved values proves current state, not which request succeeded. On conflict, fetch
saved context for comparison; if fetching fails retain the draft and allow retry of the read.
Offer Use saved record or Review my edits. Reconciliation starts from current saved values with
the old draft visible for copying; only an explicit save submits the newly reviewed values/token.
A further conflict repeats this flow; there is no force-save or automatic merge.

## UI state and accessibility

Reuse the collection's private in-memory navigation and unsaved-work guard. Acquisition editing
is exclusive with item/photo editing and archive actions within the current view. Preserve drafts
through failed requests and appearance changes; warn before discarding on navigation. Authentication
loss clears private state. No localStorage/sessionStorage drafts or recovery promise after logout
or browser closure. Persisted acquisition context is retrievable in a new authenticated session.

Distinguish loading, absent context, failed load, editing, uncertain save, conflict, and saved
states. A failed load never offers creation as though absence were established. Return focus to
the initiating action on cancel/save and focus validation or conflict feedback appropriately.
Keep collection query, view, and back-navigation state; acquisition edits do not change current
item search membership. Refresh the affected detail versions after mutation.

Use existing visual tokens, semantic labels and buttons, visible focus, announced feedback,
keyboard operation, and touch targets. Stack comparison fields at 320 CSS pixels without horizontal
scroll. Exercise light/dark appearances and reduced-motion/transparency preferences. Notes render
as text, never HTML, and carry a brief “Recorded by you; not independently verified” explanation.

## H11 and H12 extension boundaries

These are scenario design constraints, not H9 implementation deliverables:

- H11 attaches private documents to acquisition identity using existing SQL/blob ownership,
  immutable publication, recovery, and authorized streaming. Initial types: PDF, JPEG, PNG, WebP;
  at most 20 current documents per acquisition and 10 MiB per upload. Validate signatures and
  decoded image bounds; reject encrypted PDFs and unsupported types. Downloads use attachment
  disposition, safe filenames, and no-sniff, with no public blob URLs or inline active documents.
  A format check is not a malware-free guarantee. Use request IDs, checked versions, recoverable
  upload state, and explicit removal; reuse provider retention/reconciliation for unreferenced
  blobs. Archive retains documents; mutations require an active linked item.
- H12 extends a versioned portable package with acquisition records and explicit in-scope item
  links. Include each referenced acquisition/document once, never out-of-scope item identities.
  Preserve nulls and date precision in machine-readable metadata and explain relationships in
  README text. Keep existing CSV behavior explicit rather than silently dropping new context.
  Capture items, links, acquisition text, and document references in one SQL snapshot; copy pinned
  immutable blobs and fail the whole preparation on missing or unreadable required content.
  Retain H8's 10,000-item, 128 MiB aggregate/final ZIP and 120-second limits; add a 10,000-document
  cap, count all entries toward byte limits, and retain existing admission/cleanup rules. No
  partial success or claim that the package is a restorable backup. File paths use server IDs.

H11/H12 require their own focused implementation contracts and verification before coding. Do not
add placeholder attachment/export schema, APIs, or controls in H9. Collector usability testing
across the complete scenario remains separate from automated acceptance and requires a participant.

## Migration and recovery

One coherent additive H9 migration adds the tables, constraints, RLS, restricted commands and
grants, and advances schema/readiness/provisioning markers. Preserve base migrations. No existing
item requires backfill or an acquisition. Verify fresh creation and upgrade from the PR base with
retained active/archived items, photos, and replay evidence. A destructive Down must refuse when
acquisition data exists. Prefer forward correction; use established paired SQL/blob recovery for
restoration. Do not promise old-binary compatibility with the new readiness marker. No production
migration or deployment is authorized by this spec.

## Alternatives and tradeoffs

- Embedding context on Items simplifies H9 but duplicates H10 shared context and violates the
  separate origin-event boundary. Separate identity plus a link table is the smallest shared model.
- Many current acquisitions per item could model complex provenance chains, but needs selection,
  ordering, and history semantics beyond this scenario. A single optional current link is proposed.
- Free-text dates allow ambiguity but cannot reliably preserve precision for retrieval/export.
  Nullable components preserve known facts without invented days; notes can describe uncertainty.
- A supplier subsystem and immutable descriptive edit ledger add obligations unsupported by H9.
  Free text and version-checked correction meet the requested behavior without financial semantics.
- Shared item version checks cause conservative conflicts with photo/text edits. Reusing H4's
  guard is preferable to weakening archive races or introducing automatic conflict merging.

## Acceptance and delivery evidence

Use focused failing tests first, with GIVEN/WHEN/THEN comments. Cover every method, optional facts,
date precision/leap/boundary cases, normalization, re-login retrieval, same item identity after
correction, no-acquisition items, archived reads/write denial, and API plus direct SQL isolation.
Test invalid versions, unknown fields, foreign IDs/FKs, direct write denial, and original replay
after edit/archive. Use real connections for create/create, edit/edit, and edit/archive races;
assert one winning write and no orphan or duplicate records. Inject response loss and failed
conflict reads; verify retained input and deliberate reconciliation without silent overwrites.

Run client/API/SQL and browser checks, generated OpenAPI drift, fresh/upgrade/rollback guards,
affected mutation testing (or accurately report unavailable tooling), `scripts/verify.ps1`, and
`scripts/smoke-container.ps1`. Exercise desktop/mobile/320px, both appearances, keyboard/focus and
motion/transparency preferences. Produce a narrated Playwright walkthrough from current source
using non-sensitive data and retain media outside Git. Attach supported evidence to the ready
PR, reporting attachment limitations if necessary. Update living documentation and the migration
runbook, review the complete diff, commit and push scoped changes, and open a ready-for-review PR
closing #74. Do not mark H10–H12 or the entire scenario complete.
