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

The bundled frontend and server are deployed together. API writes require
`X-Workbench-Api-Revision: beta-1`. Increment the revision for an incompatible beta change;
clients must send the revision they were built against, never automatically adopt one returned
by a server. Read requests without the header are allowed for bootstrap, images, and downloads;
an explicitly mismatched revision is rejected even on reads. `/api/beta/system` advertises
`apiRevision`, and beta responses include the revision header. Authentication writes also require
the revision and keep their antiforgery protection.

An incompatible request receives the `api_contract_unsupported` problem code. The current client
stops further writes and displays reload guidance while retaining the mounted editor and uncertain
submission. Copy unsaved changes before reloading; a reload discards in-memory edits. Never replace
an uncertain save's identifier with a new one just to bypass a contract rejection. Old browsers
predating this UI may show their existing error message; reload manually after preserving edits.

Release notes must state when a beta becomes stable, which contract is retired, how pending
requests are resolved, and the compatible application/schema combinations. Stop old instances
before applying a migration that removes their commands. Do not run a mixed fleet across that
boundary. Promotion does not authorize deletion of stored business data or retry evidence.

## Purchasing compatibility inventory

| Surface | Current purpose and lifetime |
| --- | --- |
| `/api/beta/purchase-order-drafts` | The single current purchasing business contract, including calculation, browse, read, create, update, delete |
| `/api/beta/suppliers` | Current supplier identity operations |
| Generated OpenAPI and TypeScript | Only beta business endpoints and current public DTOs; regenerated together |
| Retired `/api/purchase-order-drafts` and `/api/v2`, `/api/v3`, `/api/v4/purchase-order-drafts` | Authenticated POST/PUT/DELETE receipt lookup only, excluded from OpenAPI; no new writes |
| Historical canonicalizers and receipt DTOs | Frozen fingerprint formats 1–4, retained while matching receipts exist; do not evolve old business rules |
| Content schema 1/2/3 readers | Project retained drafts into current content without rewriting on read; retained while such data exists |
| `CreateDraftOrder` / `UpdateDraftOrder` / private `SaveDraftOrder` | Single active SQL business implementation; persisted fingerprint 4 and content schema 3 are independent of API naming |
| `ReplayDraftOrderReceipt` | Restricted read-only lookup matching tenant, actor, operation, target, expected version, fingerprint version and canonical bytes |
| `DeleteDraftOrder` | Current protected deletion with immutable receipts and tombstones |
| Historical migrations and SQL definitions | Immutable upgrade history, not supported public writers; a forward migration retires obsolete procedures |

Receipt replay returns the original result even after subsequent edits or deletion. A reused
identifier with different input is a conflict. A retired request with no matching receipt cannot
create or modify a draft. Request limits, authentication, antiforgery, and tenant isolation apply
to these compatibility adapters. No receipt expiry is introduced; removing a reader or adapter
requires a separately approved retention/transition policy.

The command-retirement migration preserves draft content, identities, numbering, row versions,
and stored receipts. Its down migration is deliberately blocked. Application rollback requires
compatible commands and schema; otherwise follow [database restore](operations/database-backup-restore.md)
and account for writes since the backup. See the [migration runbook](operations/database-migrations.md).

File formats, cookie formats, health probes, and persistence schema/fingerprint numbers have their
own compatibility rules. They are not renamed when the public API changes.

The [approved design](specs/2026-09-16-beta-api-lifecycle.md) records the alternatives and acceptance
criteria for this transition.
