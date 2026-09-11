# Take acquisition history with the collection

**Status: Accepted — owner approved implementation on 2026-09-11.**

Remaining implementation increment [H12 / #77](https://github.com/mcunille/Workbench/issues/77)
of [scenario #73](https://github.com/mcunille/Workbench/issues/73). H9–H11 are merged through
`052333a`. Their accepted contracts remain unchanged. This proposal completes the focused export
design required by the [acquisition specification](2026-09-09-acquisition-context.md#h11-and-h12-extension-boundaries).

## Outcome and scope

A collector downloads current acquisition facts and supporting paperwork alongside selected
collection records, then understands their relationships using a CSV reader, ZIP extractor,
text editor and ordinary document viewers without signing into Workbench.

Retain active or active-and-archived scope, independent of search and pagination. Preserve item
identity and optional acquisition context. Do not introduce monetary fields, orders, ownership
states, import/restore, public sharing, new storage providers or production operations. Notes
and documents remain collector-recorded information, not verified provenance.

## Current behavior and compatibility

`ItemExportEndpoints` produces CSV version 1 without acquisition fields. `ItemPackageSnapshot`
captures item/photo references under a serializable transaction; `ItemPackageArchive` produces
package version 1 and verifies immutable photograph bytes after releasing SQL locks. H11 stores
current acquisition documents separately from item photographs and retains retired bytes for
seven days. No new schema or migration is expected.

Upgrade both existing export endpoints to version 2, reflected in filenames, CSV schema_version,
manifest package_version and the export guide. Requests and authentication remain unchanged.
There is no version-selection parameter or parallel legacy producer. Previously downloaded v1
files remain valid; retain their decoding documentation. This is an explicit output-contract
change requiring approval, rather than claiming that added columns are invisible to consumers.

## CSV version 2

Preserve all v1 columns and their ordering, then append acquisition_id, acquisition_method,
acquisition_source, acquisition_date_precision, acquisition_year, acquisition_month,
acquisition_day and acquisition_notes. Keep the existing BOM, quoting, CRLF and reversible
apostrophe safety encoding; apply the same encoding to source and acquisition notes. Method
and precision use fixed system values. IDs use canonical hyphenated UUIDs.

All acquisition columns are empty for an unlinked item. A linked acquisition has its stable ID,
method (Purchase, Gift, Inheritance, Trade, Other or Unknown) and precision (unknown, year,
month or day). Unknown date components and absent source/notes remain empty. Never synthesize
January 1 or a current date. Shared descriptive fields repeat consistently across included rows;
acquisition_id makes the shared event explicit. CSV contains no document bytes or document
inventory; the UI and guide explain that ZIP is needed for paperwork.

## Package version 2

Keep records.csv, manifest.json, README.txt and photos/<item_id>.webp. Add documents under
documents/<acquisition_id>/<document_id>.<validated-extension>. Only server UUIDs and H11's
fixed validated extension mapping form paths. Copy exact stored document bytes, not thumbnails
or generated previews. Never use labels or original filenames as paths.

The manifest retains v1 top-level fields and adds acquisition_count, document_count and an
acquisitions array. Each item retains its photo description and adds nullable acquisition_id.
Each acquisition appears exactly once and includes acquisition_id, method, nullable source,
date_precision, nullable year/month/day, nullable notes, included_item_ids and documents.
Each document supplies document_id, literal label, created_at_utc, path, media_type,
byte_length and sha256. JSON user text is literal and needs no CSV prefix decoding.

Include only acquisitions referenced by selected items. included_item_ids contains only selected
items, in their export order. Include every current committed document of each such acquisition
once, even if the acquisition also has excluded archived items. Do not export excluded item IDs,
names, counts, links or photos; do not include orphan acquisitions, pending uploads, removed
documents, retired photos, command evidence or edit history. The README explicitly says these
are scoped relationships and shared documents may describe pieces outside the chosen scope;
it does not claim to redact user-written text or document contents.

An empty documents array means no current documents in the snapshot. A required unavailable,
missing, corrupt or unreadable document fails the entire ZIP; it never becomes an empty array
or a successful partial package. README explains IDs, partial dates, CSV decoding, scoped
relationships, byte checksums and that the package is not a Workbench backup/import format.

## Consistency, privacy and resource limits

Capture selected items, their current acquisition links, shared facts and committed document
metadata/revision references within one tenant-filtered serializable SQL transaction. Bound
each projection; tenant-qualified relationships and existing RLS apply to all tables. CSV uses
the same acquisition snapshot semantics without reading blobs. Set one UTC export timestamp
for all outputs. Acquire no provider streams while SQL locks are held.

Validate referenced attachment/revision ownership, association, availability, provider, type,
length and digest, including recovery-unavailable dispositions. Read only captured immutable
revisions after commit; verify length and SHA-256 before offering the completed response.
Concurrent edits, relinks, archive/restore, renames and removal yield one coherent successful
snapshot or a truthful failure. Existing seven-day retirement protects references throughout
the two-minute package deadline. New upload reservations are not documents.

Keep 10,000 items, 32 MiB CSV, 16 MiB manifest, 128 MiB total uncompressed content and 128 MiB
final ZIP, including metadata and archive overhead. Add a 10,000-current-document package cap
and retain H11's 10 MiB per-document bound. Bound acquisition count by selected item count.
Enforce aggregate byte bounds before provider copying and final size during encoding. Preserve
shared two-slot admission, 30-second CSV and 120-second ZIP deadlines, cancellation and disposal.
No background jobs, retained server export artifacts or new download identifiers are introduced.

Revalidate the authenticated session before returning a completed file. Retain antiforgery,
private no-store responses and tenant-scoped provider keys. Other-tenant rows and blobs must
never enter a snapshot, including archived and direct-identifier cases. Do not disclose provider
paths or private metadata in errors. Local downloaded files remain under the collector's control.

Retain empty-scope, busy, limit, preparation-failure and authorization outcomes. Error guidance
names photographs or documents as possible storage failures and offers retry of a new snapshot
and operator recovery guidance. Limit guidance offers active scope or CSV, with an explicit
paperwork exclusion for CSV. Never advise deleting entrusted files to make export succeed.
Browser cancellation/partial delivery never marks a file ready. Preserve in-memory ten-minute
availability and cleanup on format/scope change, sign-out, identity change and reload.

## UI and verification

Name the ZIP choice “Records, photographs and acquisition documents (ZIP)” and explain shared
document scope before preparation. CSV explains that acquisition facts are included but files
require ZIP. Reuse current progress, retry, navigation and accessibility behavior; wrap copy
and actions at 320px. Keep feedback truthful and preserve appearance/navigation state.

Use focused failing tests before implementation. Cover optional acquisition, each method/date
precision, formula-leading source/notes, Unicode and multiline text, three shared pieces,
one shared document copy, active/all scope, no excluded identities, and exact stored bytes.
Exercise real SQL races for context edits, linking, archive/restore and document changes;
successful outputs must match one selected state. Test pending/removed/unavailable documents,
missing/corrupt blobs, cancellation, limits, revoked sessions, cross-tenant rows and direct SQL
RLS. Read CSV and ZIP outputs with independent standard parsers in acceptance tests.

Run affected mutation checks (or report unavailable tooling), the full verify and container
gates, and the isolated current-source preview. Exercise the integrated H9–H12 journey on
mobile/desktop, both appearances, keyboard, 320px and reduced-motion/transparency preferences.
Record a narrated Playwright walkthrough with sample data and export/failure evidence; keep
media outside Git and attach with supported GitHub CLI tooling, reporting upload limitations.
Update the living export/collection guides and generated API declarations if needed. Perform
independent implementation review, then commit/push and open a ready-for-review PR for H12.

Scenario #73 also requires an uncoached collector trial. Record actual completion/hesitation
and downloaded-history comprehension only when a participant is available; automated tests
cannot establish those outcomes. Keep scenario acceptance pending until that evidence exists.
Do not close #73 merely because H12 ships; track H12 delivery separately.

## Alternatives and rollback

Keeping CSV v1 while extending ZIP would leave the records export without requested acquisition
facts. A separate acquisitions CSV adds multiple-file handling without helping the single-file
records workflow; repeated consistent facts plus IDs are sufficient. Per-item document copies
waste space and obscure shared ownership. Including excluded sibling IDs breaks selected scope.
A background export service adds lifecycle and deployment obligations unnecessary under current
bounds. Keeping old and new producers doubles the format surface without an identified consumer
requirement; explicitly version the replacement instead.

This change writes no new persistent state. Reverting application changes restores v1 production
without a data migration; already downloaded v2 files remain independently readable. Format
documentation must continue to describe files already issued. Production rollback/deployment
is separate from implementation approval.
