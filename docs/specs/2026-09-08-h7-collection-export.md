# H7 collection records export

**Status: Implemented** — approved by the owner on 2026-09-08 and delivered for issue #55.

## Purpose and boundaries

Let a collector retrieve every item in an explicitly chosen scope as documented, usable CSV.
Export includes current text records, including archived records when selected. It excludes photos,
creation replay payloads, session information, history, and financial or ownership interpretations.
This is not an application backup and cannot restore Workbench. No import workflow is introduced.

## Interaction

Provide Export records from Collection and Archive, opening a shared `/inventory/export` page.
Require an explicit radio selection, initially unset: Active records, or Active and archived records.
Explain that export ignores search, loaded pages, and the originating screen. Show scope, format,
limits, spreadsheet guidance, and the backup exclusion before Prepare export.

Use states idle, preparing, ready, empty, and failed. Preparation disables duplicate submission;
Cancel aborts the request. Once the entire response has arrived and been checked, expose Download CSV.
Say Download started after activating the download; do not claim the operating system saved it.
Retain the selected scope and prepared file in private application memory across ordinary navigation
and appearance changes. Changing scope discards the prepared file. Sign-out, identity change, reload,
or ten minutes after preparation clears the file and any object URL. Never use browser storage.
Retry prepares a new snapshot and explains that records may have changed.

Provide keyboard operation, visible focus, accessible radio labels and live status, 44px targets,
readable 320px layout, light/dark appearances, and reduced-motion/transparency support.

## API and delivery lifecycle

Add authenticated `POST /api/items/export`, using existing antiforgery protection, with JSON
`{ "scope": "active" | "all" }`. Missing or unknown scope returns validation Problem Details.
Any current tenant member allowed to read the collection may export that same collection.
Never accept a tenant identifier from the caller. Preserve EF tenant filtering, SQL RLS, and the
restricted web principal; no elevated SQL credential participates.

Successful preparation returns a fully buffered `text/csv; charset=utf-8` attachment, with
Content-Length and `Cache-Control: private, no-store`. Use a server-generated filename containing
`workbench-records-v1`, selected scope, and a UTC timestamp; exclude user text from filenames.
Revalidate the authoritative session and tenant after preparation and before releasing the bytes.
The UI also checks the current authenticated identity before accepting a late response and before
offering a prepared download. Previously delivered bytes cannot be revoked, like other read data.

There is no durable job, polling endpoint, shared download URL, server file, or blob retention policy.
Only offer a download after fetch and body reading complete successfully. A disconnected request,
cancelled preparation, or failed body read must never expose a partial file as complete.

Bound preparation to 10,000 records and 32 MiB of encoded CSV including BOM and header, with a
30-second preparation deadline. Read at most 10,001 rows to detect overflow. Bound buffering and
encoding allocations rather than constructing an unbounded intermediate CSV string. Allow two
concurrent preparations per application instance; reject excess requests with 429 and Retry-After.
Release capacity on every exit path. These limits apply equally to hosted and self-hosted installs.

Return 204 for an empty selected scope; the UI says no records were exported. Return 422 with
`export_limit_exceeded` for either size bound, reporting the supported limits and suggesting Active
records if archived records were included. Do not suggest search will reduce the export. Return
503 with `export_preparation_failed` for timeout or transient database preparation failure, with
retry guidance; preserve normal 401/403 session outcomes. Do not include SQL or record text in errors.

## Consistency

Materialize a bounded projection of item fields in one SQL Server SERIALIZABLE transaction,
ordered by `(CreatedAtUtc, Id)`. Include the archive predicate within that transaction. Do not
traverse the existing paginated endpoints or issue separately timed count and data queries.
Capture the export timestamp after the read and before committing: the returned records describe
one serializable committed collection state, rather than an exact historical time-travel query.
Release database locks before CSV encoding and network delivery.

Serializable range locks prevent concurrent insert, edit, archive, or restore from creating a
mixed successful result. Such writes may wait briefly; a deadlock or timeout fails preparation
without a partial file. Verify this with independent SQL connections and synchronization barriers.
No database option or schema migration is expected. Row-versioned snapshot isolation is an
alternative if measured blocking proves unacceptable; enabling it is a separate database contract
decision. Ordinary read-committed paging is rejected because it cannot provide the required snapshot.

## CSV version 1

UTF-8 with BOM, comma delimiter, CRLF record separators, one header, no preamble or `sep=` line.
Quote every field with double quotes and double embedded quotes. Preserve embedded newlines and
Unicode. Fixed columns, in order:

`schema_version,exported_at_utc,scope,item_id,tracking_kind,name,notes,location,is_archived,created_at_utc,archived_at_utc`

Use schema version `1`, scope `active` or `all`, canonical hyphenated UUIDs, existing tracking-kind
values, lowercase `true`/`false`, and UTC ISO 8601 timestamps with seven fractional digits and `Z`.
Repeat export metadata per row so a record remains interpretable when separated from the file.
An empty nullable field means absent; current item normalization does not retain empty optional text.
Archival state and timestamp come from the same row. Do not invent an updated timestamp.

For all present user-entered name, notes, and location values, prefix exactly one ASCII apostrophe
before CSV quoting, including values already beginning with an apostrophe. This deliberately changes
the serialized text to prevent formula-leading characters from appearing at the beginning of these
cells. Consumers recover the original value by removing exactly one prefix apostrophe after CSV
parsing; null optional values remain empty. Document this transformation beside the download and
in the CSV contract. IDs and system metadata use constrained server formats and no such prefix.

Quoting alone is not formula protection. Do not promise identical automatic interpretation by all
spreadsheet software: an apostrophe may remain visible, and re-saving or removing the prefix can
remove protection. Recommend importing columns as Text for exact IDs, timestamps, and user text.
Test commas, quotes, CR/LF, Unicode, leading whitespace/control characters, `=`, `+`, `-`, `@`,
and existing apostrophes, including parse-and-decode equality. Version any later contract change.

## Alternatives and tradeoffs

A synchronous bounded export avoids durable job authorization, expiry, orphan cleanup, and new
persistence for this increment. Larger collections receive an explicit failure rather than a
truncated export; supporting larger exports would require revisiting the lifecycle. Apostrophe
prefixing provides reversible safety encoding at the cost of visible prefixes in some readers.
Raw CSV preserves literal text but is unsuitable for the stated spreadsheet-safety requirement.

## Implementation and acceptance evidence

Follow [CONTRIBUTING](../../CONTRIBUTING.md) for verification gates and the
[development workflow](../development-workflow.md) for implementation and delivery.

- Cover both scopes, more than one browsing page, empty results, limits, cancellation, capacity,
  preparation failure, authentication loss, and response completeness.
- Prove cross-tenant API and restricted SQL isolation, including archived records; recheck session
  revocation during preparation. Exercise real concurrent insert/edit/archive/restore preparation.
- Cover client navigation/appearance state, scope changes, stale responses, object URL disposal,
  accessible feedback, keyboard use, and mobile downloads.
- Inspect the running export workflow and update the narrated Playwright walkthrough.

Automated evidence does not establish collector usability. No migrations are planned; if a schema
change becomes necessary, revisit this design and follow fresh/upgrade and consolidation gates.
