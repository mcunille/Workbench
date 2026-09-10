# Connect the pieces from an acquisition

**Status: Accepted — owner approved implementation on 2026-09-10.**

Implements [H10 / #75](https://github.com/mcunille/Workbench/issues/75) within
[scenario #73](https://github.com/mcunille/Workbench/issues/73). Extends the accepted
[H9 acquisition design](2026-09-09-acquisition-context.md); H11 documents and H12 exports
remain separate deliveries.

## Purpose and evidence

A collector can connect three individually recorded stones to one fair acquisition, correct
the shared context, and correct mistaken associations without losing either identity.

At revision `1cd7166f2a4049c0b16ef680d3d1a3249d4d815c`, `AcquisitionModel.cs` already
enforces one current acquisition per item through the tenant/item link primary key and
tenant-qualified restrictive foreign keys. `AcquisitionEndpoints.cs` supports creating and
editing context from an item with checked item/acquisition versions. The H9 specification
explicitly reserves shared navigation and link corrections for H10. Extend those mechanisms;
do not reinterpret item identity or introduce a purchase-source column on Items.

## Decisions

- Keep zero or one current acquisition per individual item; an acquisition can describe many
  items. This records collection origin, not future receipt/allocation history.
- Connect, replace, and remove are explicit relationship commands. Replacement is atomic;
  never unlink first and hope the second request succeeds. Removing the last link preserves
  the acquisition, discoverable in the acquisition picker. There is no acquisition deletion.
- Keep source, date, method, and notes on the single acquisition record. Shared editing
  states that changes apply to every associated piece, including archived pieces. Do not
  copy those fields onto items or fan out item updates.
- Archived item details and acquisition navigation opened from them are read-only. Archive
  and restore retain links. An active linked item can edit shared context, as accepted in H9.
  An acquisition with no active linked items can be read and connected to an active item;
  context editing then proceeds through that active item.

Alternatives: multiple current origins per item require selection and correction semantics
outside this scenario. Copying context to pieces permits divergent corrections. Deleting
empty acquisitions loses recoverable context. A link-operation replay ledger would add
persistent command history; checked conditional retries provide truthful recovery without it.

## Collector workflow

Item details offer “View acquisition” and, for active items, “Connect to an acquisition”
when unlinked or “Change acquisition” and “Remove connection” when linked. Existing
“Add acquisition” remains available for unlinked items. The picker searches saved source
and notes and shows method, partial date, source, and a short identity to distinguish events.
It includes empty acquisitions and distinguishes loading, empty, failed, and selected states.

The acquisition view shows shared context and a paginated list of associated active pieces.
“Show archived pieces” is explicit and off by default; archived entries are labeled and open
read-only details. An archive-origin view initially includes its originating archived piece.
The view offers “Connect existing piece” and “Record a new piece” when entered from an
active item. Existing-piece selection includes active items and their current acquisition;
replacing a connection requires reviewing both old and new context before saving.

Recording a new piece reuses ordinary duplicate-safe manual entry. After the item commits,
offer the separate connection save to the selected acquisition. If that save fails, show
“Piece saved; acquisition connection not confirmed,” preserve the saved item ID and intended
acquisition, and retry/reconcile only the connection. Never create a second item on retry.

Removal confirmation names the piece and acquisition and explains that both records remain.
Shared edits disclose their scope before saving. Preserve the current collection query,
filters, sort, view, loaded position, and scroll/focus state while navigating item → acquisition
→ sibling item → back. Keep the acquisition origin as a separate return destination instead
of replacing the existing collection navigation snapshot.

Reuse existing in-memory drafts, unsaved-work guard, tokens, and feedback patterns. Authentication
loss clears private state. Preserve drafts after failed saves and appearance changes. Use
keyboard-accessible controls, visible focus, announced status/errors, focus restoration,
320px stacked layouts, both appearances, and reduced-motion/transparency preferences.

## API and persistence

Retain H9 routes and descriptive field validation. Add authenticated, tenant-scoped, private
no-store reads for acquisition discovery, acquisition detail, and paginated associated pieces:

| Route | Contract |
| --- | --- |
| `GET /api/acquisitions` | Search by source/notes; bounded cursor pagination, including empty acquisitions. |
| `GET /api/acquisitions/{id}` | Current shared fields and acquisition version; inaccessible ID is 404. |
| `GET /api/acquisitions/{id}/items` | Active pieces by default; explicit archived inclusion, bounded cursor pagination. |
| `PUT /api/items/{id}/acquisition-link` | Atomically connect, replace, or remove the current relationship. |

Use a fixed page size of 40, search length at most 200 characters, and stable ordering by
creation timestamp then ID, with tenant-leading indexes where needed. Validate malformed
cursors as 400. Pagination is live browsing, not a snapshot export.

The link request contains the expected item version, expected current acquisition ID
(nullable), its expected version when present, and the target acquisition ID/version
(nullable for removal). Tokens encode exactly eight bytes. Reject inconsistent combinations,
unknown properties, and client-supplied ownership. Success returns the current item acquisition
context and item version. Existing item/photos/location/identity remain untouched except
for the item concurrency token. No-op requests still validate the expected state and versions.

Use restricted stored procedures with caller tenant context, existing RLS and tenant-qualified
foreign keys, and no direct runtime write grants. Within one transaction, lock the item first,
then the old/new acquisitions in deterministic ID order. Verify item active state, expected
link and all versions before mutation. Advance the item version and versions of acquisitions
whose membership changed. Shared context edits retain H9's item-first locking and advance the
acquisition version, causing stale link/context changes to conflict rather than silently win.
Reads must return coherent context/version pairs; preserve H9's guarded item-context reads.

Accessible stale state returns 409 with actionable conflict feedback; archived writes return
409 `item_archived`. Missing/foreign identifiers return indistinguishable 404 responses before
exposing related context. Validate isolation inside SQL as well as API preflight. Update runtime
provisioning, readiness grants, generated OpenAPI declarations, and schema-version assertions.

After a transport failure, retain the exact request and tokens. A retry can return 409 if the
first save committed. Fetch and display current state alongside the intended connection; equal
state does not prove which request committed. Require explicit review before saving with fresh
tokens. Never automatically rebase, force-save, or interpret a failed read as an absent link.
Unique links and conditional transactions prevent duplicates without adding a replay ledger.

Keep immutable H9 creation evidence after relinking/removal. An old creation replay must not
reconnect a detached item or recreate context; it returns current saved state without mutation,
with truthful UI handling if the original acquisition is no longer the current connection.

## Verification and delivery

Use focused failing behavior tests before implementation. Cover three-piece shared edits;
existing/new entry; atomic replacement and last-link removal; archived read-only navigation
and restore; retained collection state and drafts; uncertain saves; and API/direct-SQL tenant
isolation for item, old acquisition, target acquisition, discovery, and archived links.

Exercise actual concurrent SQL sessions for competing target links, link versus archive,
link versus shared edit, opposite-direction replacements, duplicate retries, and old creation
replays after correction. Verify one recoverable winner where expected and no partial change,
duplicate link, or unintended acquisition/item creation. Test permission provisioning and
readiness. Run affected mutation checks and document surviving/equivalent mutants or tooling limits.

Deliver one coherent forward migration, preserving base migrations and existing acquisition
data. Verify fresh schema and upgrade from the PR base. Rollback cannot discard acquisitions
or links; use the existing guarded migration/recovery policy and report its exercised limits.

Run `scripts/verify.ps1`, `scripts/smoke-container.ps1`, and this checkout's `scripts/dev-up.ps1`.
Inspect the workflow in the running preview and report its URL. Record a narrated Playwright
walkthrough with non-sensitive sample data, desktop/mobile, appearances, keyboard, and recovery
states; retain media outside Git and attach representative evidence using supported GitHub CLI
attachment support. Report attachment or coverage blockers truthfully. Then commit, push, and
open a ready-for-review PR. Merge, deployment, and participant usability research are separate.
