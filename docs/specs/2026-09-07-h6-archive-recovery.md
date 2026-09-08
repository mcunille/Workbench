# H6: recover an archived record

**Status: Implemented — owner approved this design on 2026-09-07.**
Implements [issue #54](https://github.com/mcunille/Workbench/issues/54), building on the
implemented [H5 lifecycle](2026-09-07-h5-item-archiving.md).

## Purpose and boundaries

A collector can find a mistakenly archived record, inspect its retained details and photograph,
and return that same record to active browsing without developer assistance. Archive is record
management, with no sale, possession, disposal, or financial meaning. Preserve the
[inventory foundation](2026-09-06-inventory-domain-foundation.md).

This increment adds archive browsing and single-record restoration. Archived records remain
read-only until restored: no descriptive editing, photo addition, replacement, or removal.
No permanent deletion, bulk operations, grouping, export policy, or package import is included.
H7/H8 own export decisions; correct H5's sentence assigning export inclusion to H6 during delivery.

## Existing evidence and chosen approach

At base commit `9e618a1`, H5 already supplies nullable `ArchivedAtUtc`, shared SQL rowversion,
checked archive/edit/photo commands, retained direct reads and photo access, and tenant RLS.
`InventoryEndpoints.ListAsync` implements active-only literal search and cursor pagination;
`CollectionMemory` retains one authenticated traversal. Reuse these contracts and visual patterns
with explicit active/archive scope, rather than introducing another record model or search engine.

## Archive browsing contract

Add authenticated `GET /api/items/archived`, returning the existing `ItemPageResponse` shape.
Accept `q` and `cursor` with the exact [H3 rules](2026-09-07-h3-collection-search.md): normalized
query at most 200 UTF-16 code units, no NUL, literal contiguous substring matching in name,
notes, or location, case-insensitive and accent-sensitive SQL collation. Invalid query/cursor
returns 400 Problem Details. Parameterize values and retain cancellation and command timeouts.

Apply tenant and `ArchivedAtUtc IS NOT NULL` predicates before text matching, ordering, and
pagination. Keep 50 results per page, ascending original creation time then SQL Server UUID
ordering, and the existing continuation encoding. This predictable order matches the collection;
restoration does not alter original chronology. Repeat the normalized query on every page.
Pagination reflects committed data per request, not a cross-request snapshot. Concurrent changes
may require refreshing from page one. Ordinary `GET /api/items` remains active-only.

Reuse the retained all-record tenant/chronology index initially. Measure representative archive
queries, including sparse archives, no matches, and later pages; do not claim unbounded scale.
An additional archived-only index is unnecessary without measured evidence. No global archive
query filter is introduced: original creation replay, direct reads, and photo reads retain access
to authorized records in either state. All responses remain private and no-store.

## Restoration command and persistence

Add authenticated, antiforgery-protected `POST /api/items/{id}/restore`, accepting exactly
`expectedVersion`. Validate base64 decoding to eight bytes and reject unknown properties with
400 validation Problem Details. Return 200 with committed `ItemDetailResponse` on a new restore.

Implement restricted `Inventory.RestoreItem` under the existing caller RLS and ownership chain.
Require an explicit transaction and a valid version at the SQL boundary. Atomically update only
`ArchivedAtUtc` to null when tenant-visible ID, archived state, and rowversion all match. SQL
advances rowversion. Read the resulting details under the mutation lock and commit before success.
Retain direct runtime UPDATE/DELETE denial and narrow EXECUTE permissions; no impersonation.

Do not change ID, tenant, creation request ID/time, immutable creation snapshots, descriptive
fields, current photo reference, blob retention, or completed photo-operation outcomes. Original
creation requests still replay to the same current item after any archive/restore cycle; differing
creation payloads still conflict. Restoration neither republishes nor duplicates the photograph.

Missing and other-tenant IDs return indistinguishable 404 responses. A visible active record
returns 409 with `item_active`, even for an identical retry; leave its version and contents alone.
An archived row with a stale version returns 409 with `item_version_conflict`. Determine these
outcomes consistently within the command transaction. Never label an old request successful
merely because the record is currently active.

Two restores sharing one archived version have at most one new successful mutation. A delayed
restore from an earlier archive cycle must not undo a later re-archive. Existing edit/photo/archive
predicates continue to arbitrate against this shared version: archived-version edits/photos cannot
become valid merely because a concurrent restore made the row active. Existing completed photo
replay semantics remain intact; the client reloads current details after a replay.

## Interaction and navigation

Add a clearly labeled Archive destination at `/inventory/archive`, reachable from the collection
and visibly distinct from the active Collection. Reuse Grid/List, thumbnails, Search, Clear,
and Load more. Distinguish loading, empty archive, no matches, initial failure, and later-page
failure; expose Retry. Retain earlier pages when loading more fails and ignore outdated responses.

Keep separate in-memory active and archive traversals: query draft/submitted query, Grid/List,
pages/cursor, selected item, and scroll position. Appearance changes and ordinary navigation
preserve each traversal; reload starts a new traversal. Authentication loss or identity change
clears all private memory. Do not persist private traversal data in browser storage.

Open the canonical `/inventory/{id}` detail link from either browser. Remember the originating
browser for Back to archive/collection and browser Back/Forward; direct links without an origin
offer the destination appropriate to the fetched current state. Read-only returns restore position
and focus. Unavailable or moved records must not leave stale cards in the originating browser.

Archived details show an Archived label, timestamp, saved text, and current photograph, plus
Restore to collection. Omit edit and photo mutation controls. Opening restoration confirmation
names the item, explains its return to active browsing, and captures the displayed archived version.
Cancel before submission sends no request. Keep one local workflow and prevent duplicate submits.
Replace H5's no-restoration warning with an explanation that recovery is available in Archive.

After a confirmed restore, stay on current item details, announce restoration, expose active
editing/photo/archive actions, and offer View in collection. Keep Back to archive when that was
the origin. Invalidate both traversals while preserving their query and view preferences and
restart their pages/positions. This also applies to archive, uncertain lifecycle outcomes, and
late completions after details unmount, scoped to the original authenticated identity. Another
authorized session sees the persisted membership and unchanged identity/photo after reload.

## Failure, conflict, and safe retry

On transport failure or server error, say restoration could not be confirmed; keep the original
token and offer Retry and Review current record. Retry sends that same token, never an
automatically refreshed version. Protect navigation while pending or uncertain using the existing
unsaved-work mechanism. Leaving does not imply that the submitted operation was cancelled.

After conflict, reload current details. If active, show the current saved state and explain that
it is already in the collection, without attributing success to this request. If archived, show
the current record and require explicit new confirmation against its new version. This includes
an intervening restore/re-archive cycle. If the recovery read fails, retain uncertainty and offer
Retry loading; do not offer a fresh restore against an unknown version. Handle missing records
and authentication failures through existing unavailable/session flows.

An old edit draft encountering an archived record remains recoverable under H4/H5. Require
discarding or finishing that local recovery workflow before starting restoration. Never replay
an old text/photo operation automatically after restore; active editing starts from current state.

Use existing appearance tokens and semantic controls, keyboard focus into confirmation and back
on cancellation, announced pending/error/outcome states, visible focus, 44 CSS-pixel touch targets,
320px layouts, both appearances, and reduced-motion/transparency preferences.

## Migration and operational recovery

Deliver one forward migration adding the restore command and its permission. Update schema/readiness
markers, provisioning checks, and generated API declarations. Preserve every base migration and
the existing column/index/model shape. Require migration before deploying the new application.
Document rollback compatibility: the previous H5 application can read the same active/archived
data after restoration, but lacks archive browsing/restore UI. If the new procedure is removed in
a supported down migration, restore prior permission/schema markers without altering item data.
Never reverse restoration by guessing earlier archive timestamps. Use forward correction or the
documented paired SQL/blob recovery procedure when data recovery is needed; no production action
is authorized by this implementation.

## Alternatives and tradeoffs

- A separate archive endpoint keeps active browsing semantics explicit. An `includeArchived`
  toggle risks mixing record-management views and is unnecessary for this story.
- Shared versions and outcome review avoid a durable restore-request ledger. The tradeoff is an
  explicit conflict/read after a lost success response, rather than proof of which request won.
- Read-only archived records preserve H5's mutation boundary. Editing them in place would require
  a different lifecycle contract; restoration already provides a clear path to editing.

## Acceptance and delivery

After approval, maintain an uncommitted implementation plan and execute inline because persistence,
API, UI state, and verification are dependent deliverables. Write focused failing tests with GIVEN,
WHEN, and THEN comments before each behavior change, then implement and verify:

- Archived-only search beyond 50 rows, literal matching, invalid inputs, empty/no-match/failure
  states, pagination retry, and tenant isolation through HTTP and direct SQL with foreign IDs.
- Unchanged identity, text, creation replay, and photo across restoration, reload, and another
  session; active search includes the restored item and archive search excludes it.
- Restricted SQL permission/transaction/version enforcement and real independent-connection
  restore/restore races, archived-version edit/photo versus restore, and delayed restore after
  re-archive. Verify at most one eligible mutation and no duplicate or silent overwrite.
- Lost successful responses, repeated retry, failure before commit, failed recovery reads,
  already-active conflicts, renewed confirmation, and no implicit replay of stale edits/photos.
- Desktop/mobile, keyboard/focus, both appearances and accessibility preferences, independent
  traversal restoration, late completions, identity clearing, and cancellation/navigation protection.
- Fresh database and upgrade from the PR base with archived items, edited creation snapshots,
  retained photos and completed/pending photo operations; supported rollback/recovery checks.

Run targeted mutation testing, investigating survivors and stating tooling/equivalence limits.
Run `scripts/verify.ps1` and `scripts/smoke-container.ps1` from current source. Update the narrated
Playwright walkthrough and inspect its rendered local video, keeping generated MP4s untracked.
Record local application URLs and exact evidence/coverage limits; automated checks do not establish
collector usability. Update living documentation, review the integrated result, commit scoped
changes, and open a ready-for-review PR. Merging remains separately authorized.

## Verification record

`scripts/verify.ps1 -SkipDependencyInstall` passed end to end after explicit locked npm installs:
451 server tests, 92 client tests, all 34 browser scenarios, formatting, generated-contract drift,
current-source builds, all four migration drills, and the published release-unit probe.
`scripts/smoke-container.ps1` also passed with the final navigation-corrected source: hardened
non-root/read-only runtime, SQL readiness, local Compose TLS, session persistence after app
replacement, forwarding-header controls, and worker telemetry. The temporary browser URL was
`http://127.0.0.1:4179`, published probe `http://127.0.0.1:50964`, and final container probe
`http://127.0.0.1:60264`; harnesses removed their disposable instances afterward.

The full browser suite includes retained photographs, separate Grid/List/search traversals,
320px/desktop layouts, both appearances, reduced-motion/transparency preferences, keyboard focus,
44px controls, two sessions, same-token retry after a lost committed response, failed recovery
reads, and renewed confirmation after re-archive. Archive and detail screenshots were inspected.

Focused HTTP/SQL tests cover
archived-only literal search and pagination, preserved edited creation replay and photographs,
authorization and foreign archived identifiers, private/no-store responses, malformed versions,
explicit transactions, and rollback retaining the original archived version. Independent restricted
SQL connections race restore/restore and restore against archived-version text/photo commands.
Delayed restore after re-archive remains a conflict and leaves the current row unchanged.

Fresh creation, upgrade from the actual H5 base (with edited snapshots, archived items and retained
photo-operation history), data-preserving H6 downgrade, and restored-authentication sanitation
passed the four migration drills. The new migration has no item-table/model-shape change; all base
migrations remain unchanged. API declarations were regenerated and the drift check passed.

Five targeted manual mutation probes were detected: removing stale-version classification in SQL,
removing the explicit-transaction requirement, sending the wrong expected token, omitting archive
traversal invalidation, and omitting uncertain-navigation protection. Original source was restored
and affected tests rerun successfully. No automated mutation score or exhaustive mutation coverage
is claimed.

A bounded sparse-archive sample used 5,000 records with 100 archived. Final-shape HTTP observations
were 227.6 ms for the first page including cold query compilation, 38.5 ms for a later page, and
10.2 ms for an absent match. These are local synthetic observations, not load testing or an
unbounded scale guarantee. The retained tenant/chronology index needs no migration for H6.

Independent implementation review found two navigation cases: browser history replacement could
reuse another record's archive origin, and native keyboard fragment navigation could lose the
origin after restoration and an appearance change. Focused App tests reproduced both. Each new
history navigation now has a unique non-private entry identity, and native fragments retain the
logical page's identity; distance indices still govern the existing pending-work guards. The
reviewer rechecked the complete base/head range and reported no remaining actionable findings.

The [narrated walkthrough](../demos/h6/README.md) records synthetic data against the real API and
SQL. Generated MP4s remain untracked. Local automated verification does not establish collector
usability, production deployment, hosted backup acceptance, public CA issuance, or SMTP delivery.
