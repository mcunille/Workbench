# H4: edit an item's descriptive record

**Status: Implemented** — the owner approved the API, database command,
and recovery behavior below on 2026-09-07. Implements [issue #42](https://github.com/mcunille/Workbench/issues/42).

## Purpose and scope

Let a collector correct name, notes, and descriptive storage location while retaining the same
item identifier. Preserve recoverable edits and prevent stale saves from overwriting newer data.
Category, tracking kind, stock, money, item deletion, and photo behavior changes are out of scope.

The accepted [domain foundation](2026-09-06-inventory-domain-foundation.md) fixes these boundaries.
H3 search is present at inspected commit `b7bf3ef`; it queries current item text directly.
`ItemDetailResponse.Version` already exposes the SQL rowversion as base64. Photo publication
advances that same version through `Inventory.SetItemPhoto`. Runtime SQL cannot directly update
or delete items. Extend that restricted command pattern rather than granting general updates.

## API and validation

Add authenticated, antiforgery-protected `PUT /api/items/{id}` accepting exactly
`expectedVersion`, `name`, `notes`, and `location`. This replaces all three descriptive fields;
omitted optional text means null. Reject unknown properties. The version is required and must
decode from base64 to exactly eight bytes; treat it as opaque, never as a timestamp or counter.

Reuse creation normalization and bounds: trim name and require 1–200 characters; whitespace-only
notes become null, otherwise preserve their text up to 4,000 characters; trim location and convert
blank to null, with a 200-character maximum. Return field-keyed 400 validation Problem Details.
Never accept tenant, identity, tracking kind, creation metadata, or photo changes in this request.

Return 200 with the complete saved `ItemDetailResponse`, including its new version, only after
commit. A missing or other-tenant ID returns the same 404. A stale valid version returns 409
Problem Details with a stable `code` of `item_version_conflict`; do not return another tenant's
data. Existing session and authorization responses remain unchanged. Responses use private,
no-store caching. Regenerate the OpenAPI client declarations.

## Database command and concurrency

Introduce `Inventory.UpdateItemDetails` in one additive migration. Grant only EXECUTE to the
web role, retain direct UPDATE/DELETE denial, and execute under caller tenant RLS using ownership
chaining, without owner impersonation. The command accepts the ID, expected binary(8) version,
and normalized text and updates only Name, Notes, and StorageLocation. Enforce required name and
length bounds at the SQL command boundary as well, without silently truncating oversized input.

The SQL UPDATE predicate checks ID and RowVersion atomically under tenant isolation. A prior
application read alone is never the concurrency guard. Read the resulting detail in the same
transaction while holding the update lock, then commit before returning success. A zero-row update
is a conflict if the item is visible, otherwise not found. A valid unchanged-value save still
checks the token and advances it; a stale identical payload remains a conflict.

Text saves and photo changes share the item version. Thus either can invalidate an open editor,
including across two real connections. This conservative conflict boundary protects the existing
photo command and avoids introducing a second version model for three descriptive fields.

## Retry and uncertain outcomes

Creation retries retain the H1 contract after editing: the original normalized creation payload
returns 200 with the current item, while a different payload with that creation request ID returns
409. The first successful descriptive edit captures the pre-edit name, notes, and location in
`Inventory.ItemCreationSnapshots` in the same transaction. Subsequent edits cannot replace this
tenant-qualified, read-only replay evidence. Until the first edit, the item fields themselves are
the unchanged creation payload. Replay reads the item before checking its snapshot so concurrent
capture cannot pair changed item fields with a missing snapshot. A failed capture rolls back the edit.

Do not introduce an edit operation ledger or promise exactly-once acknowledgments. The checked
version makes resending the same request safe: after a successful commit its old token cannot
overwrite a subsequent update. Disable duplicate submissions while a request is pending.

On transport or server failure, keep the draft and original token, report that the save could not
be confirmed, and offer Retry and Review current record. Retry resends the same token and payload;
never silently fetch a new token and retry. If the first save committed but its response was lost,
retry returns a conflict and enters the review flow. Even when current text equals the draft,
describe it as the current saved record, not proof that this particular request succeeded.
Known validation failures preserve the editable draft and focus the relevant error.

## Editing and conflict recovery

Add Edit details within existing item details, with labeled name, notes, and location fields,
Save changes, and Cancel. Use one active editing workflow per item: do not permit a local photo
mutation while descriptive editing is active, or begin descriptive editing with unsaved photo work.
Opening the editor captures the displayed values and version. Cancel before submission changes
no persisted data. After an uncertain save, returning to details reloads the server record and
does not imply cancellation reversed a request already sent.

On conflict retain the draft unchanged and fetch current details for comparison. Show saved values
alongside the draft, stacked at narrow widths, with an explanation that another session changed
the record. If this fetch fails, retain the draft and offer retry; do not unlock a fresh save.
Provide Use saved record (explicitly discard the draft) and Review my edits. The latter opens a
reconciliation form against the newly loaded version, initialized from current saved values with
the old draft still visible for copying. The user chooses the desired text and explicitly saves.
Never automatically replace current values with all stale draft fields. A further concurrent
change repeats conflict recovery. No force-save or last-write-wins option exists.

Use the existing navigation/unsaved-work guard, including uncertain-save state. Preserve drafts
through theme changes and ordinary failed requests within the mounted authenticated session.
Do not store private drafts in localStorage or sessionStorage. Existing authentication-loss handling
clears private state; recovery across logout, browser closure, or confirmed navigation is not promised.

Successful saves update details and invalidate cached collection pages while preserving query and
Grid/List preference. Reload matching results from the beginning so obsolete names, locations,
matches, and page boundaries cannot survive in memory. Exact prior scroll position is sacrificed
after a mutation that can change result membership; ordinary read-only H3 navigation stays intact.

## Migration, compatibility, and recovery

Keep all shipped migrations unchanged. The additive migration adds the command and its grants,
advances readiness/schema markers, and updates provisioning checks. Its tenant-qualified creation
snapshot table preserves H1 replay identity; it adds no item columns, edit history, or blob objects.
Existing rows need no backfill because their original payload is captured atomically by their first
edit. Direct INSERT/UPDATE/DELETE of snapshots is denied to the runtime; only the checked command
can capture them through ownership chaining under caller RLS. Rollback refuses to discard captured
creation evidence. Require the migration before running the new release. Verify fresh
creation and upgrade from the PR base schema with retained items/photos. Follow the existing
[migration runbook](../operations/database-migrations.md) for release and rollback evidence;
prefer a forward correction, with established SQL/blob restore for recovery when necessary.
Do not assume old binaries accept the new schema version. No production operation is authorized.

## Alternatives and tradeoffs

- HTTP ETag/If-Match is viable, but a required body token and 409 align with existing photo requests
  and reuse the already generated detail version. Keep one convention for this increment.
- An edit-request ledger could prove replay outcomes, but adds durable schema and retention rules.
  Safe conditional retry plus explicit reconciliation meets H4 without that extra contract.
- Automatic field merging can preserve disjoint changes, but introduces merge semantics and hidden
  overwrites. Explicit review handles the three small fields with visible decisions.
- Separate photo/text versions reduce conflicts but widen the concurrency model. Shared rowversion
  deliberately favors correctness; photo-only conflicts remain an accepted usability cost.

## Acceptance and verification

Cover normalized save/reload/search, stable identity and metadata, cancellation, invalid/missing versions, unknown
fields, boundaries, cross-tenant and unauthenticated requests, and direct SQL permission denial.
Test stale unchanged-value requests and response-loss retries without overwriting a later save.

Use actual SQL connections and two authenticated browser contexts to load the same version, save
one session, reject the other, preserve its draft, reconcile, and verify both sessions after reload.
Race text/text and text/photo writes from a shared version and prove at most one wins. Exercise
failed saves and failed conflict reloads, repeated conflicts, and search membership after edits.

Check desktop and 320 CSS-pixel layouts, keyboard/focus, accessible errors/status, 44-pixel targets,
System/Light/Dark persistence, draft preservation during theme changes, and required text contrast.
Extend the narrated Playwright walkthrough with edit, cancel, failure/retry, and two-session conflict.
Follow [CONTRIBUTING](../../CONTRIBUTING.md) for verification gates and the
[development workflow](../development-workflow.md) for implementation and delivery.

## Verification record

Historical evidence from revision `e176204` on 2026-09-07; not current verification.
The approximately 76-second recording decoded, with desktop, mobile retry, conflict comparison
and reconciliation frames inspected. Four manual mutants were detected: SQL version predicate,
client checked token, stale reconciliation initialization and late cache invalidation. No automated
mutation score was claimed. The scenario used synthetic data and disposable infrastructure with
authentication off camera. These checks do not establish collector usability or production acceptance.
See the [demonstration index](../demos/README.md#historical-h1h5-recordings) for media retirement.
