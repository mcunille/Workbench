# API lifecycle

Workbench has no stable release yet. All application HTTP APIs use `/api/beta/...`; OpenAPI's
document name and version are `beta`. They are expected to change. The first release establishes
`/api/v1/...`. Subsequent releases support at most the current stable API and an optional beta
for the next release. Individual development iterations do not become permanent public versions.

Prefer non-breaking changes where they keep code simple. When a breaking change is necessary,
state why, the simpler compatible alternatives considered, affected callers and persisted data,
and rollout/recovery behavior. Obtain explicit approval before implementation, including during
beta. See [design principles](DESIGN-PRINCIPLES.md#13-evolve-apis-deliberately).

## Generated contracts and API errors

GEM-06 extends the authenticated tenant-session GET routes at `/api/beta/gem-reference` and
`/api/beta/gem-reference/{id}` with effective tenant content. Existing shared response fields and
the shared meaning of `rowVersion` remain; additive metadata includes `origin`, `effectiveVersion`,
field attribution/state/sources, sparse choices, archive state, and review reasons. Detail accepts
`origin=tenant|workbench`; without it a retained tenant addition takes precedence, including when
archived. This keeps both identities addressable if later shared publication reuses a tenant GUID.
Top-level detail citations retain ordinal field then .NET Guid ID ordering.

Browse searches effective literal names/aliases/taxonomy and material-kind/group filters, with
50 entries per page. Ordinary browse excludes retirement and archive; `includeArchived=true`
adds tenant archives. Effective v2 cursors bind filters, archive visibility, and the final origin
tie-breaker after SQL name/uniqueidentifier ordering. Existing v1 cursors remain valid for ordinary
effective browsing with workbench origin; they cannot enable archive browsing. Service-admin reads
retain shared-only content and v1 cursors. Invalid effective entries remain searchable with review
reasons. All tenant read/write responses are private/no-store, including binding failures.

Tenant-session writes add POST `tenant-entries`, PUT `tenant-entries/{id}`, POST
`tenant-entries/{id}/archive` and `/restore`, PUT `{id}/overrides`, and POST `{id}/reset` under the
same reference prefix. Writes require existing member authorization and antiforgery. Additions
use caller-generated IDs; all subsequent writes require both eight-byte base64 components of
`effectiveVersion`, with null preserving absence. Override PUT supplies the complete desired
sparse map; inherit removes a choice, clear removes optional content, and reset accepts an
optional field (null resets all). The route selects origin: addition writes target tenant entries,
override/reset target workbench entries. No caller-supplied tenant context is accepted.

Success returns current effective detail. Typed validation returns 400 and field errors; absent
or inaccessible targets return 404; stale versions, duplicate identities, and unreconciled state
return 409 with current accessible detail when available. Rejections preserve stored choices;
inaccessible IDs reveal no other tenant data. An uncertain save is resolved by rereading, and
replaying an old token conflicts. Request bodies are bounded to 1 MiB before parsing. These are
backend contracts; tenant and service-admin editing screens remain separate GEM milestones.

Server-generated OpenAPI owns the client API declarations. Regenerate the checked-in TypeScript
declarations with server contract changes; do not hand-edit them or maintain duplicate handwritten
response interfaces. Handwritten copies can compile after the server changes, concealing drift.
The generation/drift gate in [Contributing](../CONTRIBUTING.md) makes that mismatch visible.

Unknown API routes return API Problem Details rather than the React shell. Production error responses
retain stable status/title/type and a trace identifier without exception messages, stack traces,
paths, configuration, or secrets. SPA fallback is for unmatched non-API GET/HEAD navigation only.
Keep these boundaries intact in both the test host and published output; client navigation must
never turn an API failure into a successful HTML response.

## Callers and rollout

The bundled frontend and server are deployed together against one evolving `/api/beta`
contract. There is no client revision header, response revision, system revision field, or
global mismatch lock. Authentication writes retain their antiforgery protection.

A stale open browser is not globally detected or blocked. Requests follow ordinary endpoint
validation, authorization and optimistic-concurrency handling; incompatible input may require
a manual reload after preserving edits. A shape-compatible old request may still succeed.
Do not assume that every stale browser request will be rejected before mutation.

Endpoint recovery preserves local input on validation/conflict failures and keeps the original
request ID and exact payload for uncertain submissions. Frozen editors expose keyboard-selectable,
read-only recovery text. Copy unsaved changes before reloading, which discards in-memory edits.
For an uncertain save, keep the page open and inspect the saved record before starting new work;
never replace its request identifier merely to force another attempt. No automatic reload or
resubmission is introduced by this policy.
Release notes must state when a beta becomes stable, which contract is retired, how pending
requests are resolved, and the compatible application/schema combinations. Stop old instances
before applying a migration that removes their commands. Do not run a mixed fleet across that
boundary. Promotion does not authorize deletion of stored business data or retry evidence.

## Purchasing compatibility inventory

| Surface | Current purpose and lifetime |
| --- | --- |
| `/api/beta/purchase-order-drafts` | Draft calculation, browse, read, create, update, delete and explicit commitment |
| `/api/beta/suppliers` | Current supplier identity operations |
| `/api/beta/purchase-orders` | Unified draft/ordered browse and read, ordered amendments, and immutable revision history |
| `/api/beta/purchase-orders/{id}/documents` | Private files on ordered purchases; versioned upload/rename/removal, downloads and exact-operation status/retry |
| Generated OpenAPI and TypeScript | Only beta business endpoints and current public DTOs; regenerated together |
| Retired `/api/purchase-order-drafts` and `/api/v2`, `/api/v3`, `/api/v4/purchase-order-drafts` | Unsupported, including retries of previously successful requests; no replay adapters or historical request DTOs |
| Content schema 1/2/3/4 readers | Project retained drafts into current content without rewriting on read; retained while such data exists |
| `CreateDraftOrder` / `UpdateDraftOrder` / private `SaveDraftOrder` | Single active SQL business implementation; persisted fingerprint 4 and content schema 4 are independent of API naming |
| `DeleteDraftOrder` | Current protected deletion with immutable receipts and tombstones |
| Historical migrations and SQL definitions | Immutable upgrade history, not supported public writers; a forward migration retires obsolete procedures |

Current beta retries return the original result even after subsequent edits or deletion. A reused
identifier with different input is a conflict. Historical development contracts have no replay
support: all requests to their retired routes receive `api_contract_unsupported`, even when an old
receipt exists. The owner explicitly approved this boundary because no version has been released.

PO-04 extends the single beta contract: draft lists exclude ordered records, direct draft
reads of ordered records return a state conflict, and new draft edits/deletes cannot change an
ordered purchase. Existing successful draft retries still return their original receipts after
commitment. Commit and amendment requests use actor-bound immutable receipts; exact retries remain
successful after later amendments. Stale clients may need to reload after preserving unsaved/uncertain input as
described above. Stop old writers before migrating; deploy matching frontend and server together.
For an uncertain historical save, inspect the current record before starting new work; do not assume
the old save failed or replace its request identifier to force another write. Existing receipt rows
remain intact, but retaining evidence does not imply support for replaying an obsolete contract.

The command-retirement and replay-removal migrations preserve draft content, identities, numbering,
row versions, and stored receipts. Their down migrations are deliberately blocked. Application rollback
requires compatible commands and schema; otherwise follow [database restore](operations/database-backup-restore.md)
and account for writes since the backup. See the [migration runbook](operations/database-migrations.md).

File formats, cookie formats, health probes, and persistence schema/fingerprint numbers have their
own compatibility rules. They are not renamed when the public API changes.

Separate revision negotiation was rejected because it would add another contract-versioning
mechanism to the evolving beta; a response-only revision would still require maintenance without
ensuring safe stale-client behavior. Retaining parallel historical writers was rejected because
older payloads cannot express newer fields and would add repeated validation and field-loss risk.
