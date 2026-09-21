# Supplier social and marketplace profiles

Issue [#132](https://github.com/mcunille/Workbench/issues/132) requests independent,
optional Instagram, X and GemRockAuctions links on supplier records.

## Scope and design

Extend the existing supplier content with nullable, optional `instagram`, `x`, and
`gemRockAuctions` strings, each limited to 2048 characters. Three named fields match
the requested platforms without introducing a platform registry or child-resource API.
A generic list would permit arbitrary platforms and duplicates but adds editing and
validation complexity beyond this request.

The editor retains the existing contact section and adds an optional profile section.
Clearing a field removes its value when saved. Saved destinations appear as platform-labeled
links opening a new tab with `noopener noreferrer`; editing retains the last saved link
until the save completes. Conflict comparisons include all profile values as text.

The server trims surrounding whitespace and maps blank input to absence. HTTP and HTTPS
absolute URLs are accepted; credentials, backslashes, control characters, whitespace,
relative URLs, other schemes, and oversized values are rejected with field-specific feedback.
Platform labels describe the user's classification; hostname ownership is not verified or
restricted. No server fetch, profile lookup, or third-party integration is performed.
The client independently prevents unsafe saved values from becoming links.

## Contracts and retained data

The additive fields may be omitted by existing callers. As with existing supplier fields,
PUT replaces submitted supplier content, so omitted or null profiles are cleared. Null
profile properties are omitted from canonical serialization, preserving exact pre-change
request fingerprints. Non-null profiles participate in fingerprints and version checks.
Existing receipts remain immutable; retry, archive, actor checks, RLS, and restricted command
authority retain their current behavior. Purchase-order snapshots and website semantics do
not change. Suppliers with no profiles need only their existing required name.

One forward migration adds nullable columns and installs the extended restricted supplier
command. No supplier values or receipts are backfilled. Readiness and backup markers advance;
old backup markers remain accepted. Deploy the matching API/client after migration. Downgrade
is blocked to preserve data and retry evidence; use forward correction or guarded recovery.

## Acceptance and verification

- Create, edit, clear, reload and open independently labeled links for all three platforms.
- Keep website and unaffected profiles intact, including suppliers with no profiles.
- Reject unsafe links at the API and restricted SQL boundary with no persisted changes.
- Preserve legacy receipt replay, tenant isolation, direct-write restrictions, and concurrent-edit conflicts.
- Verify fresh migration and upgrade from the PR base with retained supplier/receipt bytes.
- Exercise desktop and mobile forms, validation focus, and safe links; run the repository
  verification and container gates. This extends the existing Tanzanite form design.
