# GEM-02 shared gem reference persistence and reads

**Status: Specification and implementation plan approved on 2026-10-01; native implementation verification in progress.**

This implements only GEM-02 from the
[gemological reference library roadmap](2026-09-29-gemological-reference-library.md).
The owner approved the implementation plan and selected native execution.

## Purpose and scope

Authenticated tenant members need a stable, source-attributed shared reference that every
tenant reads identically before tenant additions and overrides exist. Success means representing
mineral and non-mineral gems, preserving retired identities, and providing bounded search/detail
reads without exposing tenant data or enabling writes.

Included: shared entries, aliases, field-linked source assertions, locality assertions,
retirement metadata, validation, normalized duplicate identity, read APIs, SQL permissions,
one migration, generated API declarations, and verification.

Excluded: pilot population (GEM-04), service-admin identities (GEM-03), drafts/publishing
(GEM-05), tenant additions/overrides (GEM-06), browser library/editor screens, numerical
properties, and inventory links. Fresh installations have an empty catalog. Synthetic test
entries are not production seed content.

## Approach and boundaries

Use normalized tables in a dedicated `Gemology` schema and a focused server feature folder.
Extend the existing EF model through a partial configuration file. Shared entities have no
tenant ID and do not implement `ITenantOwned`; existing filters, interceptors, and SQL RLS
remain intact. Reads use the existing web connection without disabling tenant filters or
requiring an operator/migration credential.

Normalized tables make assertion ownership and foreign-key integrity explicit. A single JSON
document would weaken these checks. Dedicated read procedures are unnecessary for shared,
read-only records; narrowly granted SELECT supports bounded EF projections. Future service-admin
reads use their own authorized boundary, not tenant endpoint reuse.

## Persistence model

Entry IDs are nonempty GUIDs retained through corrections and retirement.

| Table | Contents and integrity |
| --- | --- |
| `Gemology.Entries` | ID, material kind, common name, optional group/species/variety/description, normalized identity key, retirement state/explanation/redirect ID, SQL rowversion. |
| `Gemology.Aliases` | Entry ID, display name, normalized name; unique normalized alias per entry. |
| `Gemology.SourceAssertions` | Assertion ID, entry ID, supported field, title/publisher, URL or publication citation, accessed date and reviewed date. Multiple sources may support one field. |
| `Gemology.LocalityAssertions` | At most one per entry: place, precise claim scope, reviewed date, and source assertion reference belonging to the same entry and supporting locality. |

The supported-field vocabulary is `materialKind`, `commonName`, `aliases`, `group`, `species`,
`variety`, `description`, and `notableLocality`. Each assertion supports one field; a source
supporting several fields produces separate assertions. This keeps later field overrides and
their attribution independent. No record-wide bibliography replaces field attribution.

Foreign keys restrict destructive deletion. Redirects reference existing shared entries and
cannot reference themselves. Retirement never deletes an entry or its claims. SQL constraints
enforce known material kinds, nonblank required scalar fields, mineral species, retirement
coherence, source identification, and entry/claim linkage. Application validation additionally
handles URLs, dates, collection limits, source coverage, and redirect cycles. Later publish
commands must use these rules and supply their own atomic SQL enforcement; GEM-02 does not
implement their write protocol.

Published content requires a source assertion for each populated substantive field, including
material kind, common name, taxonomy, description, and a populated alias list. Locality has its
own supporting assertion and review date. Absent fields have no supporting assertions. Sources
store concise independent facts and bibliographic metadata.

## Validation and identity

- Material kinds are `mineral`, `mineraloid`, `organic`, and `rockAggregate`. Mineral species is
  mandatory. Other kinds do not require invented taxonomy; applicable taxonomy may be present.
- Common name, taxonomy names, aliases, and locality place/scope use a 200-character limit;
  description and retirement explanation use 2,000. At most 20 aliases and 64 source assertions
  belong to an entry.
- Source title/publisher use 200 characters; URL/publication citation use 2,000. Title,
  publisher, and reviewed date are required. A nonblank publication citation or absolute
  HTTP/HTTPS URL identifies the source. URL credentials and unsafe schemes are rejected.
  Dates cannot exceed the current UTC date; accessed date is optional.
- Trim display text, reject control characters, and normalize optional blanks to absence.
  Identity comparison additionally uses Unicode Form KC, invariant uppercase, and collapsed
  whitespace. Preserve display spelling. Aliases use the same comparison normalization.
- Identity comprises material kind, group, species, variety, and common name, with explicit
  absent components and unambiguous encoding. Store a deterministic SHA-256 identity key
  under a unique SQL index; reject duplicate active shared identities. Retired entries may
  retain an identity reused by an active replacement.
- Retirement requires an explanation or redirect. Active entries cannot carry retirement
  metadata. Redirect validation rejects cycles. Reads return the explicit target ID without
  following a chain or concealing the original entry.

Reusable input/validation code produces field-level errors for later seed and publishing
consumers. There are no public create/update/retire endpoints in this story.

## Read contract

Both routes require the existing authenticated tenant session and a valid tenant context.
Responses use `Cache-Control: private, no-store`. Missing authentication returns the existing
API authentication failure rather than HTML. Responses contain no tenant identifiers or records.

`GET /api/beta/gem-reference` accepts optional `query`, `materialKind`, `group`, and `cursor`.
Omitted or whitespace-only query means browse. Search is case-insensitive literal substring
matching across common name, aliases, group, species, and variety, never SQL wildcard syntax.
Group is an exact case-insensitive filter; material kind uses the vocabulary above.
Query/group are limited to 200 characters and reject controls. Unknown material kinds,
malformed/excessive cursors, and changed-filter cursor reuse return HTTP 400 Problem Details
with stable codes `invalid_query`, `invalid_filter`, or `invalid_cursor`.

Return `{ entries, nextCursor }`, at most 50 entries and one lookahead row. Order by common
name using an explicit case-insensitive SQL collation, then ID. Cursor boundaries use the
same ordering and bind normalized search/filter values. Cursor decoding is bounded to
4,096 characters and uses parameterized values. Empty catalog/no matches returns HTTP 200
with an empty array and null cursor. Retired entries are excluded from browse. Pagination
is a live view: later publications can move entries between pages; no catalog revision exists.

List entries contain ID, material kind, common name, group/species/variety, and
`layer: workbenchReference`. Descriptions and source collections are detail-only.
These DTOs describe shared reads; GEM-06 defines the distinct effective projection.

`GET /api/beta/gem-reference/{id}` returns active or retired detail. Unknown valid IDs return
HTTP 404 Problem Details with `gem_reference_not_found`. Detail includes list fields, aliases,
description, Base64 rowversion, and retirement metadata. Assertions include supported field,
source metadata, reviewed date, and Workbench attribution. Locality includes place, precise
scope, reviewed date, and supporting assertion ID. Missing taxonomy is null, not fabricated.
No generic specimen-origin field or inferred specimen fact is returned.

Detail projects one coherent entry and its children, avoiding independent interleaved queries
that mix pre-publication values with post-publication sources. Source URLs are inert strings;
the server does not fetch them or embed remote content. OpenAPI owns the public DTOs; regenerate
the checked-in TypeScript declarations.

## Database permissions and migration

Grant `workbench_web` SELECT on the four shared tables only. Explicitly deny INSERT, UPDATE,
and DELETE to web and worker roles. Worker, operator, and maintenance receive no shared read
grants. No schema-wide SELECT, bypass role, command EXECUTE, or service-admin authority is
introduced. Update password/Entra principal provisioning allowlists where applicable to accept
precisely these read grants and reject broader authority.

One additive migration creates the schema, tables, indexes, constraints, and permissions,
updates the model snapshot/readiness marker, and preserves existing tenant data. Verify fresh
creation and upgrade from the actual merged base schema. Down is guarded to preserve identities
and provenance; recovery uses reviewed forward correction or protected restore. Matching
binaries/schema are required under the existing readiness policy.

## Verification and delivery

Use focused TDD with Gherkin comments for normalization, material validation, source coverage,
safe links, duplicate identities, retirement, malformed queries, and cursor/filter boundaries.
Confirm initial failures reflect missing behavior rather than setup or compilation.

Real SQL/HTTP tests cover two authenticated tenants receiving identical shared data,
unauthenticated denial, every searchable field, filters/multiple pages, literal wildcard
characters, absent non-mineral taxonomy, attributed sources/locality, retired detail, and
no tenant-data leakage. Exercise SQL constraints/indexes directly. Actual restricted principals
prove allowed web reads and denied writes/other-role reads. Permission/provisioning tests prove
rejection of broad grants.

Migration tests cover fresh installation, upgrade with preserved records, readiness, and
guarded rollback. Mutation testing starts with validation/cursor rules; investigate survivors
and report justified exclusions or unavailable tooling. Run full `verify.ps1` and
`smoke-container.ps1` gates from current source. Refresh this checkout with `dev-up.ps1`,
inspect authenticated reads against the running app, and report its local URL. No new browser
UI is implied by this API story.

Update architecture, API, database-principal, migration, and schema-version documentation.
Leave the overall roadmap unfinished. After integrated internal review and passing verification,
commit scoped changes, push a `codex/` branch, and open a ready-for-review PR. Production
migration and merging remain separately authorized.
