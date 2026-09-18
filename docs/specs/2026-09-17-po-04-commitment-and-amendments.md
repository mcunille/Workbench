# PO-04: commit purchase orders and preserve amendments

**Status:** Approved by the owner on 2026-09-17; implementation in progress.

## Outcome and scope

Implement PO-04 from the [purchasing scenario](2026-09-11-purchase-orders-and-purchase-finances.md)
on the PO-01/02/03/05 baseline. An owner explicitly records a placed purchase, can inspect its
original agreed contents, and explains later changes through immutable amendments. Existing
incomplete drafts remain editable and deletable. This design does not implement invoices,
payments, fulfillment, cancellation, returns, closure, exports, or ledger postings.

The choices below were approved for this increment, rather than decisions already established
by the scenario. In particular, commitment validity, amendment boundaries, and durable storage
are new contracts.

## Commitment

- Commit a saved draft with an explicit calendar order date (`YYYY-MM-DD`), independent of the
  UTC timestamp when Workbench records it. Require a valid date; do not infer it from creation.
  Future dates are allowed for entered supplier records; the UI must not imply scheduled execution.
- Require a nonblank supplier snapshot name (a directory link is optional), one currency, and
  at least one line. Every retained line must have a description, positive quantity and supported
  unit, including total-line pricing. Incomplete extra lines must be completed or removed.
- Keep existing precision, pricing modes, discounts, charges and calculation rules. Prices and
  charge amounts may remain unknown. Existing unresolved legacy pricing must be explicitly
  resolved or cleared before commitment; never silently discard or interpret it.
- Preserve the PO identifier, business reference, line IDs, charge IDs, supplier snapshot,
  references, source links, notes, and complete financial input in revision 1.
- Commit only saved content with its expected row version. Unsaved changes must first be saved
  successfully and reviewed; a failed or uncertain save cannot fall through to commitment.
- Label the order **Ordered**. This does not confirm estimates, create a balance due, mark it
  paid or received, send anything to a supplier, or create inventory.

Known line prices remain estimates because the present line contract has no confirmation field.
Retain each charge's existing estimated/confirmed status. Display **Supplier estimate** and
**Total purchase estimate**, with unknown/incomplete amounts explicit; never relabel the whole
total as confirmed merely because the order was committed.

## Amendments and history

Ordered content is read-only outside **Create amendment**. An amendment edits a local copy of the
latest revision using existing controls and submits the complete replacement contents, order
date, expected version and a required reason (1–2,000 trimmed UTF-16 code units). No durable
amendment draft or background autosave is introduced. Discarding it leaves the order unchanged.

Apply commitment validation to each amendment. Allow correction of supplier snapshots, references,
date, quantities, descriptions, prices, discounts, charges and notes; show the previous and proposed
values before confirmation. Preserve IDs for continuing lines/charges and use new IDs for additions.
Removed lines/charges remain visible in earlier revisions. Reducing quantities or removing a line
records an amendment only; it must not claim cancellation, refund, receipt, or stock movement.
At least one valid line remains required. Retain existing confirmed-charge correction checks in
addition to the amendment reason. Reject no-op amendments after normalization.

Currency is fixed after commitment. Changing currency requires a separate future correction
design; the draft-only clear-amounts workflow must not permit relabeling an ordered purchase.
There is no return to draft or deletion of an ordered purchase in this increment.

Append a numbered full snapshot for each amendment, atomically with the current projection.
Record actor user ID, server UTC time and reason; revision 1 identifies commitment. Store the
full supplier/contact snapshot, date and calculation snapshot with a calculation-policy version
so future directory or calculation changes cannot rewrite historical evidence. Display a paged
revision list and an inspectable before/after comparison, including additions, removals and
changes to dates, identity, units and money. Do not depend on current directory/user names to
identify the historical supplier or actor.

## Persistence, authorization and retries

Keep the existing `Purchasing.DraftOrders` row as the current purchase projection to preserve
identities and existing receipt foreign keys; add order state, nullable order date and revision
number. The internal historical table name is not a public lifecycle promise. Add a tenant-owned
revision table containing immutable versioned snapshots and a separate receipt table for commit
and amendment requests. Enforce unique tenant/order/revision and tenant/request keys.

Use restricted SQL commands for commitment and amendments, with authoritative validation in the
server and SQL command boundary as in existing purchasing writes. SQL must independently reject
invalid state, foreign tenant references, invalid contents and stale versions. Apply existing RLS
and tenant proof, active membership/actor checks, antiforgery and private/no-store responses.
The web principal gets read and command execution permissions, never direct revision or receipt
mutation. Extend request size limits and duplicate/unknown JSON property rejection to new routes.

Within one transaction, lock the order, check expected version/state, append exactly one revision,
update the projection and record the successful receipt. Bind a request ID to the authenticated
actor, tenant, operation, target, expected version and canonical payload. Exact successful retries
return the original receipt even after later amendments; changed payloads conflict. A lost response
offers retry using the frozen original request. Parallel commit/update/delete/amend commands must
serialize consistently so only the expected version succeeds and no history can be bypassed.
Existing draft mutation receipt replays remain supported, including after commitment; a replay
does not reapply the old draft write. Check replay before rejecting a new operation on order state.

## HTTP and compatibility

Add `/api/beta/purchase-orders` browse/read endpoints for the unified list and current order view,
`POST /api/beta/purchase-order-drafts/{id}/commit`, and
`POST /api/beta/purchase-orders/{id}/amendments`. Provide paged revision summaries and individual
revision reads under `/api/beta/purchase-orders/{id}/revisions`. All identifiers are tenant-scoped.
Commit requests contain request ID, expected version and order date. Amendment requests additionally
contain reason and full content. Responses identify the purchase, saved version and revision.
Use 400 for invalid input, 404 for inaccessible records and 409 for stale/state/request conflicts.

Keep existing draft URLs and payloads for drafts and successful retries. Draft browsing excludes
ordered records; direct draft reads of ordered records return a state conflict with same-tenant
navigation information. New draft updates/deletes on an ordered record are rejected by SQL as well
as HTTP. The new client uses the unified list with explicit Draft/Ordered labels and an optional
state filter; existing search fields and stable pagination semantics remain.

This changes the lifecycle semantics observable by older clients. Advance the beta revision so an
old bundled editor must reload before issuing writes; preserve its recovery text and uncertain
request identity. Regenerate OpenAPI/TypeScript and update the API lifecycle inventory. Do not add
historical API adapters. Deploy the matching frontend and server together after migration, with
old writers stopped. A separate committed-order aggregate would preserve the literal draft table
name but complicate identity, references, receipts and unified reads; retaining the current row
with append-only history is the smaller proposal. Renaming all draft APIs would break more callers.

## Interface

Extend the existing Tanzanite purchasing surface in Operate mode. Keep the draft editor and compact
lines/charges. A saved draft offers **Record as ordered** leading to a review of saved contents,
date and estimate limitations, followed by explicit confirmation. Ordered detail leads with PO
reference, supplier, Ordered state and order date, then itemization and estimates. **Create amendment**
and **View history** are separate actions; draft deletion and ordinary save are unavailable here.

Use existing conflict comparison and recovery patterns for amendments. Validation reveals and focuses
the affected fields; failures preserve input; uncertain requests prevent replacement submissions.
Keep keyboard operation, focus return, unsaved-navigation protection, session-loss clearing and
mobile layout consistent with existing purchasing. History remains usable without color cues.

## Migration and recovery

Introduce one forward migration from the PR base, preserving every existing draft, tombstone,
reference, supplier snapshot, row version and receipt. Backfill state as Draft with no order date
or revision history; do not invent commitments. Modify protected draft commands to enforce state
without losing their exact replay behavior. Add history/RLS/grants and update readiness, schema
markers and backup compatibility. Do not rewrite base or retained migrations.

Block destructive Down to preserve commitment evidence. Rollback uses a reviewed forward correction
or verified backup restore with explicit accounting for subsequent writes. Verify fresh creation
and upgrade from the PR base, retained data, restricted-principal permissions and restore behavior.

Implementation note: the isolated retained preview applied `AddPurchaseOrderCommitment` before
final SQL edge-case verification. Preserve that applied migration and use
`HardenPurchaseOrderCommitmentValidation` for supplier GUID normalization, trimmed amendment reasons
and older structured-line adaptation. After that correction was also applied, independent review
identified a missing projection for the oldest retained line formats. The forward
`ProjectRetainedPurchaseOrderLines` correction makes commitment match public read projections while
preserving unresolved quotes for validation. These three migrations deliberately remain separate
under the repository's prohibition on rewriting applied retained history; fresh and predecessor
upgrades apply them in order. The preview's installed procedure was inspected before correction.

## Acceptance and verification

1. Save an incomplete draft unchanged; reject commitment with missing supplier/currency, no lines,
   invalid date or incomplete quantities/descriptions/units. Report actionable field errors.
2. Commit a valid purchase with unknown prices and estimated charges. Reload and show Ordered,
   entered date, unchanged reference, exact agreed contents and visibly unresolved costs.
3. Amend quantities, supplier details and costs with a reason. Reload both revisions and compare
   them; original content, actor and recorded time remain unchanged. Test no-op/blank reason,
   currency change and invalid replacement rejection without losing local input.
4. Exercise exact retries, changed-request conflicts, concurrent commit/edit/delete and concurrent
   amendments through real SQL and authenticated HTTP. Assert one revision per successful request.
5. Deny cross-business reads/writes/history and direct restricted-principal history mutation.
   Confirm draft update/delete cannot bypass ordered-state protection or alter snapshots.
6. Verify existing draft retry receipts, supplier-directory edits, collection and acquisition flows
   remain unaffected, including upgrade fixtures with retained historical pricing content.
7. Cover pure rules, API transport, client recovery and meaningful persisted browser journeys using
   TDD and Gherkin comments. Run focused mutation testing for state/validation rules where available;
   report tooling and coverage limits accurately.
8. Run `scripts/verify.ps1` and `scripts/smoke-container.ps1`; rebuild via `scripts/dev-up.ps1`, inspect
   the persisted commitment/amendment/history workflow at desktop and mobile sizes, and report the
   preview URL. Capture non-sensitive visual evidence outside Git and attach it if supported.

After design approval, implement and verify the whole increment, update current documentation,
perform independent internal review, then commit and open a ready-for-review PR. No merge or
production operations are included.
