# H8 collection package with photographs

**Status: Accepted — owner approved implementation on 2026-09-08.**

Implements the accepted product scope of [issue #56](https://github.com/mcunille/Workbench/issues/56).
Extends [H7](2026-09-08-h7-collection-export.md) and the existing H2 photo/provider boundary.

## Purpose and boundaries

Let a collector keep current records and their stored photographs together in an ordinary ZIP,
readable without Workbench or an authenticated session. Preserve the standalone H7 CSV contract.
Include the normalized full detail WebP photograph; thumbnails are redundant display derivatives,
and camera originals are not stored. Exclude retired photo versions, history, credentials, internal
storage paths and provider identifiers. This is a portable collection copy, not database recovery
or an import format. No import, permanent deletion, background jobs or public sharing is introduced.

## User interaction

Extend the existing export page with a format choice: Records (CSV), initially selected for H7
compatibility, or Records and photographs (ZIP). Keep the explicit, initially unset scope choice:
Active records, or Active and archived records. Search and loaded pages do not restrict either export.
Show format-specific limits and explain that ZIP contains stored photographs, not camera originals.

Reuse preparing, ready, empty and failed states, Cancel, Retry and Download started wording.
Only offer Download ZIP after the complete response has been read and verified. Changing scope or
format discards the prepared file. Duplicate preparation is disabled; cancellation discards late
responses. Retain scope, format and prepared file across ordinary navigation and appearance changes
in private application memory. Clear bytes/object URLs on identity change, sign-out, reload or ten
minutes after preparation. Retry creates a new snapshot, which may contain changed records/photos.

Preserve keyboard access, visible focus, live feedback, 44px targets, 320px layouts, both appearances
and reduced-motion/transparency preferences. Never imply that starting a download confirms disk save.

## Package version 1

The authoritative [collection package contract](../collection-export.md#records-and-photographs-zip)
owns ZIP entries, manifest fields, photo mapping and offline-reader guidance. It preserves the
[CSV contract](../collection-export.md#format-version-1). Fixed ASCII entry names and generated
UUIDs prevent user-entered paths from controlling extraction destinations. The offline README
must explain schema/layout version, scope, timestamp, CSV decoding and backup exclusion; no
executable HTML or external resources are required.

Missing, corrupt, recovery-unavailable or unreadable required photographs fail the entire package.
Never turn a retrieval failure into `none`, omit a required entry, or return a partial-success ZIP.
No-photo records are successful ordinary records. An empty selected scope returns H7's 204 outcome.

## API, authorization and delivery

Add authenticated, antiforgery-protected `POST /api/items/export-package` accepting H7's
`{ "scope": "active" | "all" }`. Keep `POST /api/items/export` unchanged. Package requests accept
neither tenant nor export/download/blob identifiers. Any member allowed to read the collection may
export it, including archived records in the all scope. Preserve tenant-filtered EF queries,
tenant-qualified relationships, SQL RLS and the restricted web credential.

Prepare and finalize the entire ZIP before starting a successful response. Revalidate the current
session, user and tenant authoritatively immediately before releasing bytes, as H7 does. Return
`application/zip`, exact Content-Length, attachment disposition and `Cache-Control: private, no-store`.
Filename: `workbench-package-v1-<scope>-<UTC timestamp>.zip`. The client validates status, media type,
safe filename, bounded declared length and complete body length before exposing the file.

There is no server download identifier, saved artifact URL or separate download endpoint to guess
across tenants. Tests must prove tenant separation through contents, archived rows, captured revision
ownership, and rejection of unsupported identifier-based requests. The browser checks identity on
late responses and download activation. Already delivered bytes cannot be revoked.

## Consistent photo selection and retention

Materialize a bounded projection of items, current photo links, detail attachment/revision metadata
and recovery availability in one SQL SERIALIZABLE transaction. Preserve H7's ordering and scope
predicate. Capture the timestamp after the reads and before commit. Any item with a current photo
must resolve to a live, available, correctly owned detail revision with valid size, digest, media type
and matching provider binding; an unavailable recovery entry fails preparation. Use left joins or
equivalent validation so broken required metadata cannot silently remove a record or photograph.

Release SQL locks before reading blob bytes. Read only the captured immutable revision IDs through
an internal export path, verifying exact length and digest through EOF. Do not re-resolve the current
photo through `ItemPhotoService.DownloadAsync`: replacement/removal would select a different state
or hide the captured attachment. The internal path accepts only server-captured tenant-owned metadata;
it is not a general historical-revision download API.

Existing `RetirePhotoAsync` retains removed/replaced attachments for seven days, and the deletion
worker rechecks retention under a SQL lock. Because selected attachments are live at capture, this
grace covers the bounded two-minute preparation without export leases or schema changes. Preserve
that dependency explicitly in storage documentation and tests. Replacements/removals after capture
may proceed and the successful package still contains the old captured photograph. Provider migration
and restore continue to require all web replicas and workers offline under the existing runbook.
Unexpected missing bytes or provider changes cause safe failure, never a substituted photograph.

## Bounds, cleanup and recovery

Proposed package limits are 10,000 records, 32 MiB encoded CSV, 16 MiB manifest, 128 MiB aggregate
uncompressed entry content and 128 MiB final ZIP, with a 120-second preparation deadline. Count all
entries, including README, toward the aggregate bound; enforce limits during writes and stream reads,
not just metadata preflight. Bound per-photo reads to H2's existing 10 MiB maximum. Use no compression
for already-compressed WebP; account for ZIP overhead separately. Read at most 10,001 item rows.

Share H7's two preparation slots per application instance across CSV and ZIP. Avoid unbounded
parallel blob reads, intermediate strings or per-photo copies. Build into bounded process memory;
no temporary disk file or persisted export artifact is created. Dispose archive/read streams and
release buffers/capacity on every exit path. Process termination leaves no export file to clean up;
managed memory is reclaimed, without a promise of secure memory erasure. Document the larger memory
budget, including response copies and retained browser blobs. Capacity bounds preparation, not slow
downloads; response-delivery load still requires ordinary host admission/resource controls.

Return 422 `export_limit_exceeded` for bounds, 429 `export_busy` with Retry-After for capacity, and
503 `export_preparation_failed` for deadlines, database, provider or integrity failures. Preserve
401/403 authentication outcomes and validation Problem Details for invalid scope. Errors disclose no
record text, paths, SQL or provider details. Offer Active records when the all scope exceeds limits;
otherwise explain the limit and offer the separate text CSV export. Do not suggest search reduces scope.
Transient failure offers retry of a new snapshot; persistent missing/corrupt photos require operator
storage investigation through the existing recovery runbook. Never automatically omit them.

Client cancellation aborts preparation/body reading and exposes no file. Server abort/deadline
releases resources. Interrupted delivery cannot be accepted as complete even if HTTP headers already
reported 200. Retrying is read-only and cannot duplicate collection mutations.

## Alternatives and tradeoffs

A bounded in-memory response extends H7 without jobs, download authorization records, leases or disk
cleanup. It deliberately limits photo-heavy collections and uses more memory than CSV. Durable async
exports with retained artifacts would support larger collections but need new persistence, expiry,
retry and recovery contracts; defer them. Streaming a ZIP before verification cannot give the same
all-or-nothing preparation outcome. Holding SQL locks during provider reads would block collection
writes for up to two minutes; captured immutable revisions and existing retention avoid that cost.

## Implementation and acceptance

Follow [CONTRIBUTING](../../CONTRIBUTING.md) for verification gates and the
[development workflow](../development-workflow.md) for implementation and delivery.

- Cover both scopes, empty/no-photo/mixed collections, exact CSV
  compatibility, manifest mapping, Unicode/path-like names and archive completeness beyond one page.
- Exercise missing/truncated/corrupt/recovery-unavailable blobs, bounds including ZIP overhead,
  cancellation, deadline, capacity release, revoked sessions and interrupted response bodies.
- Use independent SQL connections and synchronization barriers for item edit/insert/archive/restore
  and photo replacement/removal during capture and during blob copying. Assert one complete snapshot
  and prove retention prevents worker purge of newly retired captured revisions.
- Prove API and restricted-SQL cross-tenant isolation, including malicious identifiers and archives.
- Cover client format/scope changes, retry, cancellation, navigation/appearance preservation, stale
  responses, identity loss, expiry and object URL disposal. Inspect desktop and mobile workflows.
- Download and extract a real package and update the narrated Playwright walkthrough.
- Update collection-export and provider/recovery documentation. No schema migration is planned;
  revisit the design if persistence becomes necessary and then apply fresh/upgrade verification gates.

Automated checks do not establish collector usability. See the
[H8 verification record](../demos/h8/verification.md) for implementation evidence and coverage limits.
