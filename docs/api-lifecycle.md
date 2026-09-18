# API lifecycle

Workbench has no stable release yet. All application HTTP APIs use `/api/beta/...`; OpenAPI's
document name and version are `beta`. They are expected to change. The first release establishes
`/api/v1/...`. Subsequent releases support at most the current stable API and an optional beta
for the next release. Individual development iterations do not become permanent public versions.

Prefer non-breaking changes where they keep code simple. When a breaking change is necessary,
state why, the simpler compatible alternatives considered, affected callers and persisted data,
and rollout/recovery behavior. Obtain explicit approval before implementation, including during
beta. See [design principles](DESIGN-PRINCIPLES.md#13-evolve-apis-deliberately).

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

The [approved design](specs/2026-09-16-beta-api-lifecycle.md) records the alternatives and acceptance
criteria for this transition.
