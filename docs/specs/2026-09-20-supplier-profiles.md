# Supplier social handles

Issue [#132](https://github.com/mcunille/Workbench/issues/132), refined by the owner's
feedback, records reference handles under user-defined labels. The earlier fixed-platform
URL design is superseded.

## Scope and interaction

The supplier editor offers **Add social**, then an editable **Platform** and **URL / Handle** per row. On desktop, both fields share a row with a compact remove button at the right; mobile stacks the fields and keeps removal right-aligned below them.
Examples include Discord / gemdealer, Mastodon / @gems@stones.example, or a marketplace name
and seller identifier. Users define their own labels, rename them, edit values, or remove rows.
All entries remain optional. Each added row needs both fields; duplicate labels are rejected
without losing input. Errors identify and focus the affected field.

Handles are plain reference text. There is no URL requirement, domain ownership check,
external-link action, automatic handle parsing, or third-party request. URL-looking values
are stored and rendered as text. Website behavior remains unchanged. Supplier conflict
comparisons include these pairs; purchase-order contact snapshots do not acquire them.

## Contract and validation

Supplier content accepts optional `socialProfiles`, an ordered list of `{ label, handle }`.
It supports up to 20 entries, with labels up to 100 characters and handles up to 2048.
Both values are trimmed and must be nonempty single-line text without control characters.
Labels must be unique ignoring case. Punctuation, internal spaces, `@` handles, and URL-like
text are accepted. The list avoids object-key hazards and preserves the user's row order.

Null, omitted, and empty lists normalize to absence. PUT replaces the supplied list; removing
all rows clears it. Empty profiles are omitted from canonical serialization so original
six-field supplier requests retain their exact retry fingerprints. Nonempty pairs participate
in the existing request fingerprint and optimistic concurrency checks. The restricted SQL
writer validates the same bounds and retains tenant and actor authority.

## Migration and compatibility

The release contains one migration, `20260921041331_MakeSupplierProfilesCustom`, directly
following `HardenPurchaseOrderDocumentAuthority`. It adds the JSON collection and restricted
supplier writer without creating temporary fixed-platform columns. Existing suppliers, row
versions, and immutable receipt bytes remain unchanged.

The owner requested consolidation of the unmerged development migrations. The isolated preview
already held the final schema; its obsolete intermediate history entry was removed in a guarded
transaction with the app stopped and supplier/receipt preservation checks. This was an explicit
local development reconciliation, not a production migration or a general history-rewrite policy.
Other databases carrying an earlier development history require their own verified transition.

Readiness and backup markers identify the final schema. Released predecessor backup markers
remain accepted. Down is guarded to preserve retained supplier information and retry evidence.
Deploy the matching API/client after migration; superseded fixed-field preview clients must refresh.

## Acceptance

- Add, rename, edit, remove, save and reload arbitrary label/handle pairs independently of website.
- Require no predefined platform or web URL; render handles as text without opening actions.
- Preserve input and focus actionable feedback for incomplete, duplicate or oversized entries.
- Preserve legacy suppliers, six-field retry compatibility, tenant isolation, restricted writes,
  and current-profile concurrency/replay behavior.
- Verify one migration from the PR base, fresh schema, and upgrade preserving supplier values and receipts.
- Exercise desktop/mobile forms, comparisons and the repository verification/container gates.
