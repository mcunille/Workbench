# Supplier social handles

Issue [#132](https://github.com/mcunille/Workbench/issues/132), refined by the owner's
feedback, records reference handles under user-defined labels. The earlier fixed-platform
URL design is superseded.

## Scope and interaction

The supplier editor offers **Add social**, then an editable **Label** and **Handle** per row.
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

The prior fixed-platform migration was already applied to a retained local preview, so its
history and SQL remain immutable. A forward migration replaces the three fixed fields with
JSON-backed reference pairs. Previously entered values are retained verbatim under their
platform labels, including complete URLs; no handle is guessed from a URL. Original receipt
bytes remain unchanged. Empty-profile records need no content backfill.

The updated API/client replace the unmerged fixed-field contract together. Tabs or callers
using that superseded preview contract must refresh. Original suppliers without profile
fields remain compatible. Readiness and backup markers advance; prior backup markers remain
accepted. Down remains guarded to preserve retained supplier information and retry evidence.
The two migrations remain separate solely because the first is applied to the retained preview.

## Acceptance

- Add, rename, edit, remove, save and reload arbitrary label/handle pairs independently of website.
- Require no predefined platform or web URL; render handles as text without opening actions.
- Preserve input and focus actionable feedback for incomplete, duplicate or oversized entries.
- Preserve legacy suppliers, six-field retry compatibility, tenant isolation, restricted writes,
  and current-profile concurrency/replay behavior.
- Verify fresh schema and upgrade from the fixed-field preview with retained values and receipts.
- Exercise desktop/mobile forms, comparisons and the repository verification/container gates.
