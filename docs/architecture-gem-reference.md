# Gem reference architecture

[Return to the main entry](ARCHITECTURE.md).

## Shared gem reference foundation

GEM-02 stores tenant-independent shared entries, aliases, field-linked source assertions,
locality claims, and retirement metadata in `Gemology`. Stable IDs survive retirement;
normalized identity keys prevent duplicate active shared entries. Mineral species is required,
while non-mineral materials can omit inapplicable taxonomy. The reusable content validator
enforces safe source links, source coverage, and redirect validity for later seed/publish callers.

Tenant-authenticated `/api/beta/gem-reference` GET routes provide bounded literal search and
coherent attributed detail. Shared entities have no tenant ID. Runtime direct
catalog writes are denied. GEM-04 installs the reviewed Diamond, Sapphire, Emerald, and Ruby
pilot with nineteen field-level source assertions through a one-time data migration. The frozen
seed ships inside the migration assembly; deployments require no content download. Existing
pilot IDs (including retired entries) retain their values and provenance. Conflicting active
identities under other IDs reject the transaction. See the [installation procedure](operations/database-migrations.md#pilot-catalog-distribution-and-installation).

GEM-06 adds tenant-owned additions and sparse field overrides in `Gemology.TenantEntries` and
`TenantOverrides`, protected by tenant SQL RLS. One effective projection resolves values,
attribution, supporting citations and review dates together. Missing choices inherit; replacement
uses only tenant sources; explicit clear removes optional values and displaced sources. Reset
receives current shared content and retains an empty override row with an advancing rowversion
to prevent an old token being reused. Shared publication never waits for tenant reconciliation:
invalid effective entries retain their identities and choices, remain searchable, and expose
field-level review reasons. Effective Unicode identity collisions flag each visible entry;
archive can remove an addition collision, and other writes must leave a valid final candidate.

Tenant reads acquire the publication lock then the tenant lock and materialize one catalog-sized
effective snapshot. Browse sends scalar candidates to SQL for native collation matching and
uniqueidentifier pagination, returning at most 50 rows. Snapshot materialization, duplicate
derivation and scalar JSON size grow with the catalog; no load benchmark or performance gain is
claimed. Detail citation arrays preserve ordinal field and .NET Guid ID ordering independently
of SQL browse ordering. Tenant writes acquire exclusive locks in the same order, recheck active
membership and SQL tenant proof, then compare both components of `effectiveVersion`, including
absence, before the narrow command commits. Validation and conflicts preserve all stored choices.

Identity is qualified by `(id, origin)`: `tenant` additions and `workbench` references can share
a GUID after later publication. Detail accepts explicit origin; its default selects the tenant
entry first, including archived entries. Addition update/archive/restore target tenant origin;
override/reset target workbench origin. Service-admin readers and curation remain shared-only.
GEM-08 provides authenticated effective-library browsing at `/gem-reference`, with origin-qualified
details, field-level attribution and citations, and classification withheld for entries needing review.
GEM-09 adds tenant entry creation/editing and archive/restore, plus shared-field customization with
explicit inherit, replace, clear and reset choices. The editor keeps current Workbench values and
sources visible beside tenant choices, preserves drafts after failed or concurrent saves, and
requires explicit reconciliation of uncertain saves or invalid retained overrides. Needs-review
entries expose their reasons and block unrelated saves until reconciled. GEM-10's integrated
release evidence and coverage limits are recorded in the [acceptance guide](gem-reference-acceptance.md).

GEM-05 adds service-admin-only draft, combined review, publish, outcome, and audit APIs at
`/api/beta/service-admin/gem-reference`. Drafts may be incomplete; publishing requires valid
claim-level sources and a valid final shared catalog. A selected batch contains 1–50 distinct
drafts and uses both draft and base published rowversions. Draft saves explicitly rebase only
when the caller supplies the current published version. Admin reads use only shared catalog
tables and private command-owned curation storage, without a tenant EF context.

The web principal executes six named curation procedures and still cannot write catalog tables
directly or read/write raw drafts, receipts, and audit tables. Commands revalidate current
service-admin account/session authority; draft saves and publication serialize through a
transaction-owned application lock and recheck authority after acquiring it. Publication
validates final identities/redirects and selected versions, updates every selected entry and
claim atomically, removes only selected successful drafts, and commits its request receipt and
success audit together. Rejections preserve drafts and record a durable failure outcome/audit.
Infrastructure rollback leaves no completed receipt and attempts a separate failure audit.

The internal publication command trusts C# to derive Unicode-normalized identity and alias
keys. SQL binds raw content and alias names/positions/count to the saved draft and checks
uniqueness using the supplied identity key; it does not independently recompute semantic
identity. Current admin authority alone does not make arbitrary direct SQL payloads safe.
Callers must use the application normalization and validation path.

GEM-07 mounts an independent service-admin React application at `/service-admin` before the
tenant authentication provider. Its dedicated sign-in, shared catalog/detail, persisted draft
editor and combined review use generated curation contracts and admin-only CSRF tokens.
No tenant context or navigation destination is exposed. Failed saves retain local editorial
content; stale versions require explicit comparison and reconciliation. Same-account session
recovery keeps the editor mounted and refreshes its versions before further writes.

An uncertain publication stores only its request ID and immutable draft/version selection
in session storage scoped to the authenticated admin account. Reload, outcome lookup and
identical retry recover the server receipt; a terminal rejection requires fresh review and
confirmation. Passwords and unsaved editorial content are not persisted in that storage.
The [curation guide](gem-reference-curation.md) owns the current operator and user workflow.
Session resolution explicitly selects Read Committed isolation on its independent SQL
connection: pooled connections previously used for serializable catalog detail reads must
not carry that isolation into session renewal. The existing procedure's account/session
locks and disablement, expiry and revocation checks remain authoritative.

Draft saves, combined reviews, and publications load and validate the whole catalog while
holding the exclusive publication lock. Lock-held duration grows with catalog size and has
not been measured. Before expanding the catalog, measure that duration; any narrower locking
design must preserve coherent final-catalog validation and atomic publication.

Request identity is global and tied to its original actor. Exact retries compare a canonical
selection of draft IDs and versions (selection order is insignificant), then return the stored
outcome before loading drafts. Changed retries conflict. Receipt reads are restricted to their
actor; shared drafts and publication audit are visible to authorized service admins. Writes
require dedicated admin antiforgery and a bounded 1 MiB JSON body. No rollback/history UI,
second approver, new database credential, or tenant-data bypass is introduced.

Durable outcomes are deserialized into the current response contract. Future contract changes
must preserve historical receipt readability and verify representative older receipt fixtures.
Deleting receipts would weaken delayed-retry protection and requires an explicit replay-expiry
contract. Receipts retain field summaries and errors, not before/after content history.

**Status:** Implemented

This document is the authoritative living description of Workbench's current technical
architecture. Update it when the accepted architecture changes. The dated
[base-architecture specification](specs/2026-08-31-base-application-architecture.md) preserves the
decision context, alternatives, and acceptance criteria behind this direction. The
implementation is tracked by [GitHub issue #8](https://github.com/mcunille/Workbench/issues/8) and
its linked work items.
