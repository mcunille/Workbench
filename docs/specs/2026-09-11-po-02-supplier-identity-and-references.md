# PO-02: supplier identity and purchase references

**Status:** Implemented — verified on 2026-09-12; delivery in PR #107.

## Problem and scope

Implement PO-02 from the [purchasing scenario](2026-09-11-purchase-orders-and-purchase-finances.md):
identify the supplier, keep the details needed to contact them, and find a purchase by a stable
business reference or the supplier's order reference. Editing a supplier must not rewrite purchases.

The owner confirmed a small reusable supplier directory, with independently stored details on each
order, during design discussion. One-off suppliers remain supported.
No accounting configuration is required.

The dependency is [PO-01 PR #105](https://github.com/mcunille/Workbench/pull/105), inspected at
`fd93dfcd983f9143b62b786e613bcbb45f8799cf` and merged into main as `debb61f`. Its
[PO-01 design](https://github.com/mcunille/Workbench/blob/fd93dfcd983f9143b62b786e613bcbb45f8799cf/docs/specs/2026-09-11-po-01-draft-supplier-orders.md)
and draft contracts provide the baseline. The integrated PO-01 specification and server purchasing
contracts match the inspected revision; this implementation is based on the integrated revision.

PO-01 stores nullable provisional supplier text, mutable draft content, tenant-scoped UUIDs,
rowversion concurrency and compact durable request receipts. It has no supplier directory,
business PO reference or search. Draft deletion clears content and retains a minimal tombstone.

PO-02 adds supplier/contact details, the transaction platform on each order, stable purchase
references and narrowly scoped lookup.
Quantities, commitment, invoices, payments, sending orders, documents, general reporting, supplier
merging and supplier roles remain separate stories. Existing acquisition seller text is not
automatically converted or linked to a supplier.

## Product decisions

| Concern | Behavior |
| --- | --- |
| Reusable supplier | A business-owned record with a required name and optional contact details. Duplicate names are allowed; a name is not identity. |
| One-off supplier | Enter details directly on the draft without creating a directory record. An empty draft remains valid. |
| Historical details | Copy supplier details onto the order when selected. Reading the order uses that copy, never a live directory join for its displayed details. |
| Transaction platform | Record where this purchase happens on each PO, such as Retail, Instagram or Gem Rock Auctions. The same supplier may have orders on different platforms. |
| Workbench reference | Assign `PO-000001`, increasing within the business, on first successful save. Immutable, never reused, no year reset. |
| Supplier order reference | Optional editable text on the draft, distinct from both the Workbench reference and any future invoice number. |
| Finding an order | Search saved drafts by Workbench reference, supplier order reference, supplier snapshot name or draft title. Broader PO-12 filtering remains deferred. |

Assigning a reference does not mean an order was placed. Display **Draft** beside it. Before the
first save, show “Assigned when saved.” Use at least six digits, expanding rather than truncating
when the number grows. References are unique within a business, not globally; URLs continue using
the existing UUID. No custom prefixes, editable numbering or gapless accounting sequence is promised.
Deleting a draft retires its reference permanently.

## Supplier details and lifecycle

Use the same field set for a supplier and its order snapshot:

| Field | Limit and meaning |
| --- | --- |
| Name | 200 UTF-16 code units; required in the directory, optional on an incomplete draft. |
| Contact name | Optional, 200. |
| Email | Optional, 254; one address, validated without sending a message. |
| Phone | Optional, 100; retain international notation and extensions without country assumptions. |
| Website | Optional, 2,048; existing absolute HTTP(S), hostname and no-credentials rules. |
| Postal address | Optional, 2,000; multiline text, no inferred jurisdiction or structured tax meaning. |

Trim single-line fields; whitespace-only optional fields become null. Preserve nonblank address
line breaks. Reject control characters in single-line fields. Publish matching API and restricted
SQL validation rules before implementation; do not make browser validation the only boundary.
Supplier notes, multiple contacts, payment instructions and banking credentials are outside scope.

### Transaction platform on the order

The owner requires a platform in the PO's supplier details to identify where this transaction
happens. A supplier can trade through multiple platforms; keep one supplier identity across those
orders. Platform belongs to the purchase, not to a single platform field on the supplier directory.
For example, two orders linked to the same supplier can record Instagram and Gem Rock Auctions,
while another records Retail for an in-person purchase.

Add **Platform** as optional text of at most 200 UTF-16 code units, using the same trimming,
blank-to-null and single-line validation rules as supplier name. Allow custom values; these examples
are illustrative, not a closed list or integrations. An incomplete draft may leave it blank, shown
as Not set. Do not infer a platform from the supplier's website, source links or previous purchases.
Requirements at commitment belong to PO-04.

Save the platform independently on each PO. Supplier selection, directory edits and **Use current
supplier details** must leave it unchanged. Changing the platform must likewise preserve the supplier
link, contact snapshot and supplier order reference. The owner may explicitly edit these fields to
match the transaction; existing order source links can hold a storefront, listing or conversation URL.
Platform is not a payment method and does not trigger communication or a marketplace connection.
Supplier platform profiles, reusable handles and a managed platform directory are deferred.

Draft comparison, safe retry and deletion cover platform exactly as other saved purchase details.
PO-04 must preserve it with committed purchase details and govern later amendments. A change on one
draft cannot alter the platform on another order, even when both use the same supplier.

Directory actions are create, edit and archive/reactivate. Archive removes a supplier from default
selection; it preserves identity, existing order links and snapshots. There is no hard delete or
automatic name-based merge. An archived supplier may still be viewed and corrected. A draft already
linked to it may be saved without changing that link, with its archived state visible. New selection
requires an active supplier.

Selecting a supplier copies its displayed details into local draft state; **Save draft** persists
the association and snapshot together. Changing an order's contact details changes only that order.
Editing the directory changes only future selections. Do not silently refresh even an uncommitted
draft. **Use current supplier details** previews old and new values and replaces the local snapshot
only after confirmation; saving remains explicit. A failed load leaves the previous snapshot intact.

Changing supplier or switching to one-off entry must explain whether existing local details will be
replaced or retained. **Keep details as one-off** clears only the supplier link. Never silently carry
the previous supplier's order reference to a different supplier: prompt to keep or clear it.
Ordinary directory editing never changes any order version or saved time.

The snapshot is the supplier information deliberately saved on this draft, not a claim that every
past draft edit is recoverable. PO-04 must preserve the committed snapshot and govern amendments;
PO-13 owns a broader explainable history. Compact retry receipts are not an audit-history substitute.

## User flow

Keep Purchase orders in the existing navigation. Add **Manage suppliers** within purchasing, rather
than a new top-level product area. Provide a searchable supplier list and a compact create/edit form;
show contact details to distinguish duplicate names. Archive requires explicit confirmation.

The draft header shows Workbench reference and Draft status. A Supplier section offers directory
selection or one-off entry, Platform, optional contact fields, and Supplier order reference. Optional fields
can expand progressively. Preserve PO-01's grouped editor, explicit save, error summary, comparison,
unsaved-navigation protection, mobile stacking, keyboard operation and light/dark appearance.

Inline **New supplier** saves a directory record independently, then selects it locally. Explain
that saving the supplier does not save the order. If order saving fails or the user discards the
draft, the supplier remains available. Do not bundle two records into an implicit cross-record save.

Order rows show reference, title, supplier snapshot name, optional platform and supplier order reference.
Platform filtering and search are deferred to PO-12; this increment records and displays the value.
Search applies server-side to the entire active business's saved drafts, not just loaded rows.
Use a bounded, trimmed query of at most 200 code units and literal, case-insensitive substring
matching; SQL wildcard characters in input are ordinary text. Do not search contact details.
Empty query restores normal browsing. Show a distinct no-matches state and a clear-search action.

Retain PO-01's 50-row forward pagination and updated-time/UUID ordering. Changing the query restarts
at page one; bind the new cursor version to the normalized query and reject mismatched reuse.
Failed loads retain rows, and obsolete responses cannot replace results for a newer query.
Directory browsing follows the same bounded pagination/recovery pattern, searching name only and
making archived inclusion explicit. Detailed cursor encoding belongs in the implementation contract.

## Persistence, API and concurrency boundaries

Keep supplier records within Purchasing for this increment; a generalized contacts/CRM module is
not justified. Add tenant-owned Suppliers with server UUID, contact fields, archive flag, creation
and update actor/timestamps, and rowversion. Use tenant-qualified foreign keys and no cascading deletes.
Add optional SupplierId and supplier snapshot contact fields to DraftOrders, retaining SupplierName
as its independently stored snapshot name. Add nullable Platform (`nvarchar(200)`) on DraftOrders,
independent of SupplierId and supplier snapshot refresh. Include `platform` in V2 draft content,
detail and summary responses, and fingerprinting. Add nullable SupplierOrderReference and a permanent
numeric PO sequence value; format the latter consistently for display/search. Keep shopping-list
JSON and its reference-price semantics unchanged.

Enforce uniqueness of `(TenantId, PoNumber)` in SQL, including deletion tombstones. Allocate through
a tenant counter locked and advanced inside the same transaction as draft creation and its receipt.
Never use MAX+1 or allocate in the browser. Concurrent first saves cannot collide. Check an existing
receipt before allocating; an exact retry receives the same draft and therefore the same reference.
Return a clear failure on sequence exhaustion, with no partial save. Do not clear the number on
deletion; clear new supplier/contact/platform/reference content alongside PO-01's existing cleared fields.

Supplier commands use explicit save/archive operations, expected rowversion and compact atomic
request receipts, following PO-01's duplicate prevention and current-detail reload pattern. Reuse
the pattern without building a general command framework. Failed supplier saves retain local input;
stale versions require deliberate reconciliation. No contact bodies belong in receipts or logs.

Draft saves include the chosen SupplierId and exact local snapshot, not an instruction to fetch
whatever details happen to be current at save time. Revalidate supplier ownership and active status
for a newly selected link inside the write transaction. Directory edits between selection and save
do not replace the owner's reviewed snapshot. Concurrent archive and selection must serialize;
an archive that wins prevents a new link and returns a recoverable supplier-selection conflict.
An unchanged existing link remains valid even if archived. Define a consistent lock order across
draft, supplier, counter and request locks during implementation.

Extend detail and summary responses with read-only Workbench references. Add supplier list/detail
reads and create/update/archive mutations under `/api/suppliers`. Existing draft routes continue
identifying records by UUID. Generate the final OpenAPI contract with explicit nulls, bounds,
stable field errors and status codes; foreign suppliers return the same 404 as missing suppliers.
Snapshot edits participate in draft comparison and fingerprinting just like all existing content.

PO-01 rejects unknown request fields and fingerprints a fixed representation. Therefore adding
supplier fields is not a transparent replacement-contract change. Introduce an explicit V2 draft
write representation and fingerprint version. Keep V1 canonicalization for resolving successful
pre-upgrade receipts. An old request with no matching receipt must fail with a reload-required
contract response before mutation; never interpret missing supplier fields as permission to clear
them. Preserve existing receipt bytes and their original versions/times. V1 read compatibility,
version negotiation and error DTOs are specified below; no rolling mixed-version writer deployment
is assumed.

### Versioned wire contract

V2 uses `/api/v2/purchase-order-drafts` for list, create and UUID detail/update/delete routes.
`DraftContentV2` retains every V1 field and adds required nullable `supplierId`,
`supplierContactName`, `supplierEmail`, `supplierPhone`, `supplierWebsite`, `supplierPostalAddress`,
`supplierOrderReference` (maximum 200 code units) and `platform`. Explicit nulls retain incomplete
drafts; unknown fields remain invalid. Supplier contact validation uses the limits above.
Create and update envelopes retain requestId and expectedVersion semantics. Save receipts are unchanged.
Detail adds `poReference` and `supplierIsArchived` (false when unlinked). Summary adds `poReference`,
`supplierOrderReference` and `platform`; list envelopes retain items and nextCursor. Search uses
optional `query` and `cursor` parameters. Read-only supplier archive state does not replace snapshot data.

V1 reads remain available at the original routes and retain their original DTOs. V1 create/update
requests may resolve matching existing receipts; unmatched requests return HTTP 426 with
`draft_contract_reload_required`, without a write. Changed-input reuse remains a 409 request conflict.
The V1 deletion request contract remains valid, and its command clears V2 fields as well. Successful
old receipts retain their original fingerprint/version/time. New V2 writes fingerprint all V2 fields.

Supplier list/detail routes are `/api/suppliers` and `/api/suppliers/{id}`. List accepts `query`,
`cursor` and `includeArchived`, default false. `SupplierContent` has required `name` and required
nullable `contactName`, `email`, `phone`, `website`, `postalAddress`. POST takes `{requestId,supplier}`;
PUT takes `{requestId,expectedVersion,supplier}`. POST `/api/suppliers/{id}/archive` takes
`{requestId,expectedVersion,isArchived}` for both archive and reactivation. Details contain
`id`, `supplier`, `isArchived`, `createdAtUtc`, `updatedAtUtc`, `version`; pages contain `items` and
`nextCursor`. Successful mutations return compact `{requestId,replayed,supplierId,savedVersion,
completedAtUtc}` receipts, followed by a current-detail read before editing continues.

Use HTTP 400 `supplier_validation_failed` with field-path errors, 404 `supplier_not_found`, and
409 `supplier_version_conflict` or `supplier_request_conflict`. A draft selecting a now-archived
supplier returns 409 `supplier_selection_conflict`; retain draft input and allow selection correction.
Foreign supplier identities remain indistinguishable from missing identities. Existing authentication,
antiforgery, request-size and uncertain-outcome handling apply to these endpoints.

## Security, migration and recovery

Use current authenticated business authority, tenant RLS and EF filters on suppliers, counters and
receipts. Deny direct runtime writes; expose only restricted commands. Apply authentication,
antiforgery, bounded request bodies and private/no-store responses. Supplier IDs, order numbers and
search cursors grant no authority. Contacts are private business data; do not log queries or values,
fetch websites server-side, or send email. Contact fields cannot bypass safe text/link rendering.

Add one coherent migration after PO-01's final consolidated migration, without rewriting it.
Backfill numbers for existing nondeleted drafts per tenant in deterministic creation-time/SQL UUID
order. Preserve content, identities and timestamps; do not invent supplier directory records from
matching names. Existing deleted tombstones predate numbering and may remain unnumbered; all new
drafts receive and retain numbers. Seed counters above assigned values. New contact fields, platform
and links begin null; do not guess a platform from existing text or URLs. Document that backfilled
numbers indicate identity, not commitment chronology.

Adding persisted fields can change rowversion; old receipts must still resolve even when current
detail differs. Drain writers for migration and release the compatible build only after readiness
checks. Update schema/readiness/backup markers and permission provisioning. Block destructive Down;
use forward correction or the established guarded recovery process. Preserve PO-01 retained preview
data and its distinct pre-consolidation history; this design does not authorize resetting it.

## Alternatives and tradeoffs

Independent order details alone meet contact capture with less UI/schema, but repeat suppliers
must be entered repeatedly and lack stable reusable identity. Prefer the small directory only if
that reuse is wanted. Linking live supplier data is simpler than copying it, but changes historical
purchase details when a phone number or name changes. Full supplier revision history is unnecessary
for the narrower snapshot guarantee and does not replace committed-order history.

Assigning PO numbers at commitment avoids numbering abandoned drafts, but leaves PO-02 drafts without
a useful permanent reference until PO-04. Editable numbers accommodate external conventions but add
collision and renumbering policy. The immutable automatic number is the smaller default;
the separate supplier reference captures external identifiers without redefining internal identity.

## Acceptance criteria and verification

1. Save an empty draft and receive a unique reference; reload and exact retry preserve it. Concurrent
   creates in one business get distinct references; two businesses may independently use PO-000001.
2. Save supplier contact details directly or select a reusable supplier. Reopen the draft with exactly
   its saved snapshot. Editing or archiving that supplier changes no saved draft details or version.
3. Explicitly refresh a draft's supplier snapshot with a preview. Cancel retains local edits; save
   applies only the reviewed values. Competing draft edits still require comparison and reconciliation.
4. Keep supplier order references separate, allow duplicate external references, and find matches
   across all saved drafts using each supported search field. Pagination cannot cross queries or tenants.
5. Archive blocks new selections but preserves existing links and contact snapshots. Concurrent
   selection/archive has one consistent outcome. Same-name suppliers remain independently addressable.
6. Deleting a numbered draft clears supplier content, never releases the reference, and leaves exact
   replay safe. Retrying creation after deletion does not recreate or renumber the draft.
7. Migration preserves existing content and request evidence. Matching V1 retries still resolve;
   unmatched old writes cannot erase V2 fields. Fresh creation and upgrade both enforce tenant isolation.
8. Failures retain input and distinguish supplier-save success from order-save success. Supplier and
   draft writes produce no collection, acquisition, payment or accounting records.
9. Save and reopen two POs for the same supplier with different platforms, including a custom value.
   Editing either platform changes only that PO. Selecting or refreshing a supplier never overwrites
   platform; changing platform preserves supplier details and references. Blank remains valid on a draft.
   Validation failures retain the value, concurrent platform edits require reconciliation, changed-input
   retries conflict, deletion clears it, and migration leaves existing drafts' platforms unset.

Implementation uses focused failing tests with GIVEN/WHEN/THEN comments, real SQL transaction,
permission and migration probes, and affected mutation testing. Cover counter races, foreign IDs,
snapshot and platform independence, archive races, old receipts and unknown-outcome retries. Exercise the browser
workflow with current source, including mobile, keyboard, failed search, supplier save followed by
draft failure, and concurrency comparison. Run CONTRIBUTING.md's full application gates then.
Record actual implementation evidence separately; these acceptance criteria alone establish no
application verification evidence.

## Accepted decisions

- Confirmed: reusable supplier directory plus independent order snapshots.
- Confirmed requirement: each PO records its transaction platform independently of the supplier's
  other orders. Entry is optional free text, including custom platforms.
- Automatic permanent numbering at first save, without custom formats or yearly reset.
- One contact/address per supplier and the limited reference/name/title search scope.

The owner authorized implementation after requesting early review in PR #107. Implementation follows
these defaults and the versioned contract above. Merge and production operations remain separately
authorized actions.
