# Collection and purchasing architecture

[Return to the main entry](ARCHITECTURE.md).

### Collection records export

Collection export retrieves current item and acquisition facts as CSV v2. The
[H7 design](specs/2026-09-08-h7-collection-export.md) introduced CSV v1;
[H12](specs/2026-09-11-h12-acquisition-export.md) added acquisition-aware CSV and ZIP v2.
Collectors explicitly select active records or active plus archived records; search and loaded pages
do not narrow the export. A bounded SERIALIZABLE read under the existing web principal and tenant
RLS captures one collection state, releasing locks before encoding and delivery. Concurrent writes
may briefly wait. Preparation fails without a file above 10,000 records or 32 MiB, after a 30-second
preparation deadline, or on database failure. Each application instance admits two preparations.

The authenticated, antiforgery-protected POST `/api/beta/items/export` buffers a complete private CSV
attachment and revalidates the session before returning it. No job, shared link or server export file
is introduced by export preparation. The browser offers Download only after the complete body arrives;
its private file and selected scope survive ordinary navigation and appearance changes. Prepared
files expire after ten minutes and clear on sign-out, identity change, or reload. This is a text
portability feature, not a restorable backup. See the [CSV contract](collection-export.md).

The package endpoint, POST `/api/beta/items/export-package`, returns a bounded ZIP containing CSV v2,
manifest.json, README.txt, exact stored current detail photographs and acquisition documents.
One tenant-scoped SQL snapshot captures items, acquisition facts and links, and immutable photo/document
revision references. Provider reads then verify required lengths/digests outside the SQL transaction;
the complete archive is prepared in memory before session revalidation and delivery. A missing,
corrupt or unavailable required file fails the whole preparation. Shared acquisitions and documents
appear once, with only in-scope item links; document contents are not redacted and may describe
out-of-scope pieces. The package shares the two-preparation capacity limit, permits 10,000 records
and 10,000 documents, bounds CSV to 32 MiB, manifest to 16 MiB and content/ZIP to 128 MiB, and uses a 120-second
deadline. It includes neither thumbnails nor retired photographs and cannot restore an installation.
See the [export contract](collection-export.md) for package contents and browser lifetime.

### Collection identity

The [H9 acquisition context](specs/2026-09-09-acquisition-context.md) records an optional origin event
with method, free-text source, partial acquired date, and collector-recorded provenance notes.
Shared acquisitions let several individually recorded
pieces share that context. `Inventory.Acquisitions` has a separate identity and rowversion;
`Inventory.AcquisitionItems` uses tenant-qualified foreign keys and permits at most one current
acquisition per item. `Inventory.AcquisitionCreationRecords` retains immutable creation replay
evidence. All three tables use tenant RLS and deny direct runtime writes.

Restricted create/update commands check item and acquisition versions. The restricted
`Inventory.ChangeAcquisitionLink` command atomically connects, replaces, or removes a relationship,
checking the active item, expected membership, and old/target acquisition versions. It locks the
item first and acquisitions in deterministic order, then advances the affected rowversions.
Removing the last connection preserves the acquisition. Creation retries never create another
item, and old acquisition-creation replays cannot restore a removed or replaced connection.
Empty acquisitions remain discoverable so correcting membership does not destroy origin context.
Link commands use checked conditional retries rather than a persistent command ledger. Matching
current membership cannot prove that an earlier request committed; an uncertain save requires
explicit reconciliation before adopting fresh versions.

Tenant-scoped, paginated acquisition discovery and membership reads support shared navigation;
membership browsing excludes archived pieces unless explicitly requested. Archive and restore
retain connections. Archived item details and acquisition views opened from them are read-only;
an active linked item can still edit the shared context. Conflict recovery preserves private
in-session drafts and requires explicit reconciliation. CSV v2 includes acquisition facts and partial
date precision; ZIP v2 also includes current acquisition documents and scoped relationships, as
described in [collection export](#collection-records-export).

Acquisition documents have tenant-qualified acquisition and immutable attachment/revision links.
Restricted prepare/finalize commands enforce active linkage, versions, capacity and mutation replay
evidence. Pending uploads reserve capacity but remain absent from saved document lists. Provider
publication and integrity verification precede the SQL transaction exposing the document; a losing
finalization retires its bytes through the existing retention lifecycle. Session application locks
serialize exact request UUIDs across replicas. Rename and removal retain command evidence; successful
replay does not resurrect removed documents. Original validated bytes are private attachment downloads
after complete bounded digest verification. The [provider runbook](operations/blob-and-service-providers.md#acquisition-document-validation)
owns content policy, parser qualification, resource bounds and recovery behavior.

`Inventory.Items` holds tenant-owned physical identities. H1 enforces `TrackingKind = Individual`
and has no editable quantity, financial value, category requirement, or purchase parent. Names
remain user-entered regardless of future classification. A server UUID is permanent; a separate
tenant-unique creation request UUID makes concurrent submissions and uncertain-save retries
idempotent. SQL rowversion establishes a concurrency token for subsequent edit workflows.

The runtime has SELECT/INSERT access with RLS; direct collection UPDATE/DELETE remains denied.
`/api/beta/items` exposes create and chronological cursor-paged browsing; item details return only
the public contract. Responses are private and not stored in HTTP caches. Drafts stay in browser
memory; only the System/Light/Dark appearance preference is persisted locally. Authentication
loss clears protected client state.

Creation request identity is separate from the server-assigned item ID so retries resolve the
same operation while intentionally identical pieces remain valid separate records. Button disabling
alone cannot settle a lost response. Drafts stay in memory to avoid leaving private collection data
in browser storage after authentication ends. Bounded chronological traversal keeps every item
reachable without an unbounded query; it does not promise a frozen collection snapshot.

The accepted [inventory foundation](specs/2026-09-06-inventory-domain-foundation.md) separates
classification, individual/lot tracking, measurements, stock movements, composition, acquisition,
and valuation. Later lot and work-order features must preserve existing identities and record
splits, consumption, and transformations explicitly; they do not reinterpret H1 rows.

### Item photographs

The browser prepares JPEG, PNG, or WebP sources locally, preserving orientation and aspect ratio
and producing a preview before an explicit upload. Original files remain on the device. The
server accepts at most 4 MiB and 2,048 pixels per edge, sanitizes the image, and derives an
uncropped thumbnail. Only freshly encoded, metadata-stripped WebP variants enter private storage.

`Inventory.ItemPhotoOperations` binds each request UUID to its input digest and expected item
rowversion. Pending attachment revisions are persisted before provider I/O. Both variants and
the current item pointer become visible in one SQL transaction; replacement/removal retires the
old pair under the existing retention rules. Exact retries resolve uncertain acknowledgments.
An item-scoped SQL procedure compares rowversion and changes only `CurrentPhotoId`; the runtime
retains its denial of direct item UPDATE/DELETE. Tenant-qualified foreign keys and RLS protect
both new tables. Photo authority follows the existing authenticated inventory membership.

Photo URLs resolve the current item/photo relationship and session on every request. Responses
are private/no-store and verified to EOF before successful delivery. There are no public provider
URLs or original/historical photo routes. Pending and retained variants remain within the existing
manifest, reconciliation, worker hold, and paired recovery boundary; see the
[provider runbook](operations/blob-and-service-providers.md#item-photograph-ingestion).

### Purchase orders

The [Purchasing module](purchasing.md) stores mutable planning documents independently of inventory,
acquisitions and financial records. `Purchasing.DraftOrders` combines searchable draft headers with
bounded versioned JSON for source links and draft lines. Quantities, units and pricing bases remain
explicit; nullable decimal strings distinguish unknown from zero. Server calculation uses exact
scaled integers and four-place rounding for merchandise estimates, without creating obligations.

Tenant RLS, tenant-qualified foreign keys, current authenticated authority and restricted SQL
commands protect both drafts and their immutable request receipts. The runtime cannot directly
insert, update or delete purchasing rows. Each save atomically records a compact request receipt;
a versioned server-computed fingerprint rejects reuse of the same request UUID with changed content.
Retries return recorded success without executing again. Receipts retain identifiers, resulting
version and completion time, not historical request/response bodies. Current details are loaded
separately after success, with explicit comparison if the draft has changed since that save.
Compact receipts provide duplicate prevention without retaining historical draft bodies; they
are operational evidence, not a user-visible edit history. Pruning requires an explicit replay-expiry
contract. Draft deletion clears content while retaining a tombstone for receipt foreign keys and
the permanent PO reference; this does not offer archive or restore semantics.

Updates replace one draft document after rowversion validation. A per-request transaction lock and
draft row lock serialize competing retries and edits. The authenticated, antiforgery-protected API
is private/no-store and bounds request bodies before binding. Tenant-scoped browsing uses descending
updated-time/UUID keyset pagination; it is a live list and refreshes after local saves. The
[purchasing guide](purchasing.md) owns user-facing recovery behavior.

PO-02 adds reusable tenant-owned suppliers with independently stored contact snapshots on each
draft. Directory edits do not mutate orders; an explicit draft save applies reviewed snapshot
changes. The transaction platform belongs to the order and is independent of the supplier link.
Supplier archival prevents new selections while preserving existing links and snapshots.
Duplicate supplier names do not establish identity; no name-based merging is implied.
Social profiles are ordered reference text, not verified external identities or links, and are
excluded from purchase contact snapshots. Empty profiles are omitted from canonical supplier
requests so original six-field successful requests retain their exact retry identity.

Permanent business PO numbers are assigned by a transactional tenant counter on first save and
retained on deletion tombstones. The counter does not promise a gapless accounting sequence.
Reference/name/title search runs within the tenant, with query-bound
forward cursors. Beta draft writes fingerprint supplier, platform and supplier-based line pricing.
Retired V1–V4 routes reject all requests, including old successful retries; no historical replay
adapters remain. Current beta retries retain their compact receipts and cannot silently clear newer
fields. Supplier writes use the same compact-receipt and rowversion
reconciliation principles.

PO-03 keeps the JSON aggregate. Supplier-based pricing introduced content schema 3 through the single
current beta command implementation, with one quantity/unit and a per-unit or total-line amount.
Exact decimal arithmetic calculates line amounts; a fixed total does not require quantity. Beta
reads project older schema 1/2 entries without writing them. Complete old quotes retain their
totals; unresolved references and structured quotes remain explicit until the owner resolves them.
One supplier quantity basis avoids implying a separate inventory count. Older batch rates become
per-unit prices only when four-place representation preserves the original rounded gross;
otherwise preserve the line total. Never infer a basis for an unresolved reference price.
SQL validates the same numeric, unit, compatibility and gross bounds as the server.

All application APIs use `/api/beta/...` until the first release establishes v1. The
[API lifecycle](api-lifecycle.md) owns version support, stale-browser recovery, retired receipt
adapters, and rollout/rollback boundaries. Public contract versions do not determine the lifetime
of persisted content or immutable receipts.

The authenticated calculation endpoint shares server rules without persisting input; the client
cancels obsolete previews and hides stale results. Detail responses include derived line gross
and subtotal information.

PO-05 extends the beta contract and writes content schema 4 with line/order discounts and categorized
charge rows in the same aggregate. The server calculates four-place reductions from explicit
merchandise bases, then supplier and third-party charge totals separately. Unknown inputs never
become zero or a complete purchase estimate. Restricted current SQL commands independently validate
the closed shape, amounts, discount bounds, currency transitions and confirmed-charge correction
notes. New writes require adjustment properties; missing properties cannot erase saved financial
inputs. The bundled client and server share one beta contract without revision negotiation; stale browsers rely on normal validation/conflict handling and may need a manual reload. Retired
public routes remain unsupported. Stored receipt history remains intact.
Reads upgrade older content without persisting it, preserving unresolved legacy quotes.
One line discount and one order discount keep bases inspectable without sequential discount stacks;
both apply to merchandise only. Unknown bases cannot produce validated totals from partial sums.
The effective payee of a confirmed supplier charge includes the supplier ID and name snapshot:
changing either requires fresh explanatory notes, while contact-only changes do not.
Draft saves create no commitment, invoice, balance or ledger posting.

PO-04 retains the same purchase row and permanent reference while introducing explicit Draft/Ordered
state, calendar order date and sequential immutable revision snapshots. Commitment requires valid
supplier, currency and itemized quantities, while unresolved costs remain estimated or unknown.
Ordered content changes only through a reasoned amendment; currency is fixed and deletion is blocked.
`PurchaseOrderRevisions` preserves full contents, supplier/contact snapshots, actor, recording time,
reason and a versioned calculation snapshot. `PurchaseOrderReceipts` binds successful requests to
the actor, tenant, operation and canonical input. Restricted SQL commands atomically append history,
update the current projection and save the receipt; tenant RLS and denied direct mutations protect
both history tables. Existing draft receipt retries still succeed without reapplying old writes.
Amendment no-op detection compares decoded content and the order date, retaining array order
and scalar distinctions; JSON formatting, escaping and member order are not content changes.
Reject an unchanged amendment before advancing versions, revisions or receipts. This semantic
comparison does not change the exact canonical fingerprint used for request retry identity.
The unified beta purchase API serves drafts, ordered purchases and paged history. See the
[PO-04 specification](specs/2026-09-17-po-04-commitment-and-amendments.md).

PO-06 attaches private invoice files directly to ordered purchases. Purchasing-owned document
metadata and operation evidence link to the existing immutable storage revisions; no synthetic
acquisition or structured invoice is created. Restricted SQL preparation/finalization commands
serialize capacity and check PO/document versions around provider publication. File changes
advance the PO rowversion without changing its agreed-content revision. Existing format validation,
private downloads, exact-request recovery, seven-day retention and paired SQL/blob recovery apply.
Publish bytes outside parent locks, then recheck versions and current authority before exposing
the document. Ambiguous publication retains bytes and operation evidence for exact retry or
reconciliation; it must not be treated as permission to delete bytes and restart. Acquisition
documents retain their independent ownership and lifecycle.
