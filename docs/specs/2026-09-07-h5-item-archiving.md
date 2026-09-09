# H5: put a record aside safely

**Status: Implemented — owner approved this design on 2026-09-07.**
Implements [issue #53](https://github.com/mcunille/Workbench/issues/53).

## Purpose and boundaries

Remove an unwanted record from everyday collection browsing without losing its identity,
description, creation replay evidence, or current photograph. Archive is record management;
it does not mean sold, consumed, disposed of, or no longer possessed. Preserve the accepted
[inventory foundation](2026-09-06-inventory-domain-foundation.md),
[editing contract](2026-09-07-h4-item-editing.md), and
[photo contract](2026-09-07-h2-item-photographs.md).

H5 provides confirmed single-record archiving and read-only access to archived records by their
existing links. It does not add an archive browser, unarchive command, permanent deletion, bulk
operations, or export. Recovery here means determining the saved outcome and safely retrying an
unconfirmed operation; it does not reverse a confirmed archive. The confirmation must disclose
that restoring a record to ordinary browsing is not available in this increment. H7/H8 will settle
export inclusion separately without changing archived identity or tenant ownership.
The subsequent [H6 design](2026-09-07-h6-archive-recovery.md) adds archive browsing and restoration;
H5-only limitations below describe that original increment.

## Existing implementation evidence

`InventoryEndpoints` exposes the shared SQL rowversion in item details, implements tenant-scoped
creation replay, and queries current item text for browsing/search. `UpdateItemDetails` performs
the checked text update and atomically captures immutable creation fields on the first edit.
`ItemPhotoService` maintains durable request outcomes and publishes a photo through the checked
`SetItemPhoto` command. Photo preparation occurs before the final item update, so an application
precheck alone cannot prevent a photo/archive race. The client already permits one local editor
at a time and invalidates collection memory after descriptive edits.

## Persistence and atomic commands

Add nullable `ArchivedAtUtc` (`datetimeoffset`) to `Inventory.Items`; null means active. Set it
only through a new restricted `Inventory.ArchiveItem` command, using server UTC time. Existing
rows stay active. Add an active-row filtered browsing index on `(TenantId, CreatedAtUtc, Id)`;
retain tenant-qualified identity and creation-request uniqueness across all records.

Archive updates only the archive timestamp and SQL-generated rowversion. It must not rewrite
descriptive fields, creation metadata, snapshots, photo references, or attachment retention.
Require an explicit transaction, a valid expected version, and an atomic predicate on item ID,
rowversion, and active state. Return the saved detail under the update lock and commit before
reporting success. Missing/other-tenant rows are indistinguishable. An already archived row is
a conflict, including an identical retry; archive time and version remain unchanged.

Extend both `UpdateItemDetails` and `SetItemPhoto` with an active-state predicate at the SQL
mutation boundary. This blocks writes even if a caller has obtained the archived row's current
version. Keep caller RLS, ownership chaining, existing runtime direct UPDATE/DELETE denial, and
narrow EXECUTE grants; do not introduce owner impersonation or a global archive query filter.
Creation replay, direct reads, and photo reads must still see authorized archived rows.

Text/photo/archive requests sharing one version can have at most one successful new mutation.
If editing or photo publication wins, stale archive must conflict and require renewed confirmation.
If archive wins, subsequent text/photo publication fails without changing the retained record or
photo. A pending photo upload losing this race follows existing conflict retention/cleanup rules.

## API and replay contract

Add authenticated, antiforgery-protected `POST /api/items/{id}/archive` accepting exactly
`expectedVersion`. Require base64 decoding to eight bytes, reject unknown properties, and return
field-keyed 400 validation Problem Details for invalid input. Never accept a client timestamp.

Return 200 with the committed `ItemDetailResponse`; add nullable `archivedAtUtc` to that response.
Use private, no-store caching and existing authorization/session handling. Return 404 for missing
or other-tenant IDs. For visible archived rows return 409 with stable code `item_archived`;
otherwise a stale token returns 409 with `item_version_conflict`. Do not claim an old request
succeeded merely because the current state is archived. Extend descriptive and new photo mutation
failures to identify archived state so their editors can recover truthfully.

Ordinary `GET /api/items` excludes archived rows before text matching, ordering, and pagination
in both views; no new include-archived query option is introduced. Direct item GET remains 200
for the authorized tenant and includes archive state. Current photo GET remains available under
the same authorization rules. No record or photo becomes public through archival.

Original normalized creation requests replay as 200 with the current item, including archive
state, using existing creation snapshots after editing. A different original payload still returns
409. Never reactivate or insert a replacement record for an archived creation request ID.
The client must render such a replay as an existing archived record, not a newly added active item.

Preserve completed photo-operation replay: an exact retry of a completed operation returns its
recorded result even after archive, without mutating the item. The client then reloads current
details and presents archive state. A new or pending photo operation cannot publish on an archived
record. A mismatched photo request replay retains the existing 409 contract.

## UI, cancellation, and recovery

Add Archive record to active item details. Opening confirmation captures the displayed version
and names the affected record. Explain that it leaves browsing/search, keeps its details and
photograph accessible through its link, and currently cannot be restored to browsing. Provide
Cancel and explicit Archive record confirmation. Cancel before submission makes no request.

Use one local workflow: finish or cancel descriptive/photo work before entering archive
confirmation, and block editing/photo changes while confirming or submitting archive. Disable
duplicate submissions during the request and use the existing navigation protection while pending
or uncertain. Preserve confirmation/recovery state across appearance changes, but do not persist
private state in browser storage. Existing authentication loss clears private state.

On confirmed success show the archived details and retained photo with an Archived label and
timestamp; omit mutation controls. Keep the existing direct link and provide Back to collection.
Invalidate cached pages, retaining search text and Grid/List preference and reloading from the
beginning. Ordinary read-only navigation continues to preserve H3 state. Reloaded collections
must not retain archived cards, stale matches, or pagination boundaries.

On transport failure or server error, state that archiving could not be confirmed. Keep the
original token and offer Retry and Review current record. Retry uses the same token, never a
silently refreshed version. Invalidate collection memory because the operation may have committed.
Leaving this state must not imply that cancellation reversed a submitted request.

On conflict, load current details. If archived, show that current saved state without attributing
it to this particular request. If active, show the changed details/photo and require an explicit
new archive confirmation against that newly loaded version. If reload fails, keep recovery state
and offer another read attempt; do not enable a fresh archive with an unknown version. Missing
records and authentication failures use existing unavailable/session handling.

If another session archives while a text/photo editor is open, retain its recoverable draft while
showing the current read-only record. Do not offer reconciliation that saves against an archived
version. Provide an explicit action to discard the draft and view the archived record. A completed
photo replay does not justify replacing the latest displayed archive state with an older version.

Use existing visual tokens and semantic controls. Verify keyboard focus into confirmation and
back to its trigger on cancellation, announced pending/error/saved states, 44 CSS-pixel targets,
readable 320px layouts, both appearances, and reduced-motion/transparency preferences.

## Migration and operational recovery

Deliver one additive migration for the column/index, archive command, and changes to existing
mutation commands. Preserve all base migrations. Update readiness/schema markers, provisioning
checks, model snapshot, and generated API declarations. Require migration before the new release.
Reject destructive down migration that discards archive state; use a forward correction or the
established paired offline SQL/blob recovery procedure. Archive never retires its current blobs.

Verify fresh creation and upgrade from the PR base with active items, edited creation snapshots,
photos, and completed/pending photo operations. Document compatibility and recovery in the
[migration runbook](../operations/database-migrations.md). No production operation is included.

## Alternatives and tradeoffs

- Read-only direct access preserves entrusted data and bookmarks without expanding H5 into archive
  browsing/restoration. Adding unarchive would require a second checked transition and discovery UX;
  deferring it means the confirmation must clearly disclose that limitation.
- A separate archive request ledger could prove which request succeeded. Shared checked versions
  and explicit outcome review meet safe retry requirements with less durable schema, at the cost
  of a conflict after a lost successful response.
- Separate text/photo/archive versions reduce conflicts but complicate interactions. The existing
  shared rowversion gives a conservative, atomic boundary across all three commands.

## Acceptance and verification

Follow [CONTRIBUTING](../../CONTRIBUTING.md) for verification gates and the
[development workflow](../development-workflow.md) for implementation and delivery.

Cover confirmation cancellation, preserved metadata/photo, active-only search and pagination, direct archived reads,
creation replay before/after edits and archive, validation, archived-current-version write refusal,
tenant isolation through API and direct SQL, runtime permissions, and unchanged authentication.

Use real independent SQL connections to race archive/archive, archive/text, and archive/photo
publication from a shared version. Verify one winner and preserved state, including a pending
upload and completed photo replay. Test lost responses, repeated stale retries, failed recovery
reads, renewed confirmation, and an open editor encountering another session's archive.

Exercise two authenticated browser contexts, desktop/mobile Grid/List/search, direct bookmarks,
appearance/navigation state, keyboard/focus, and retained photo access. Update the narrated
Playwright walkthrough with success, cancellation, uncertainty, and conflict recovery. Automated
checks do not establish collector usability.

## Verification record

Historical evidence from the 2026-09-07 implementation; not current verification.

Implementation passed 439 server tests, 82 client tests, generated-contract drift, formatting,
typechecking, builds, EF model consistency, clean/upgrade/rollback/recovery drills, published-output
probes, and the SQL-backed container/Compose gate. The full browser run passed 29 scenarios; a focused
rerun passed all 10 archive/authentication scenarios after correcting a test selector. The other 20
scenarios were unchanged. No application authentication behavior changed.

Four targeted manual mutants were detected: removing the SQL archive version predicate, changing
the client expected token, omitting collection invalidation, and omitting uncertain-navigation
protection. Restored source passed affected tests. No automated mutation score is claimed.
Historical walkthrough footage was inspected at delivery; see the [demonstration
index](../demos/README.md) for its retirement. These local checks do not establish collector usability.
