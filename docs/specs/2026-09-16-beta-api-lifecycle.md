# Beta API lifecycle and purchasing consolidation

**Status:** Implemented — initially approved on 2026-09-16; the owner explicitly approved removing
historical replay support on 2026-09-17. The updated requirements below supersede the original
receipt-adapter design.
Current usage and retained compatibility components are documented in [API lifecycle](../api-lifecycle.md).

Tracks [issue #120](https://github.com/mcunille/Workbench/issues/120).

## Problem and evidence

Workbench has no released stable API. Frontend and server ship together, but `Program.cs`
registers four purchasing surfaces: `/api/purchase-order-drafts` and `/api/v2`, `/api/v3`,
and `/api/v4/purchase-order-drafts`. The first is effectively V1. The client uses V4;
V4 also reuses older shared response types and deletion code. Other application routes are
unversioned `/api/...`. OpenAPI and its generated TypeScript expose the historical contracts.

`DraftOrderInputV4.ReadEntries` projects persisted content schemas 1/2/3. Request receipts
store fingerprint versions 1–4, the actor, request and draft identities, expected/result row
versions, and completion time. Removing HTTP versions must not erase these separate storage
contracts. Existing migration files on the base branch are immutable.

## Policy

All application APIs are beta until the first release. Use `/api/beta/...` and an OpenAPI
document explicitly identified as beta. At the first release, promote that contract to
`/api/v1/...`. Thereafter support at most the current stable version plus an optional
`/api/beta/...` for the next release. Development iterations update beta rather than adding
V2/V3/V4 public contracts. A later stable promotion retires its predecessor in a coordinated
release; its migration and deployment notes must state that boundary before release.

Prefer non-breaking changes when they do not complicate code. Every necessary breaking
change requires an explicit rationale, alternatives, caller/data impact, transition plan,
and explicit owner approval before implementation. Beta means change is expected, not that
silent field loss or unapproved breaks are acceptable.

Health probes, SPA routes, file-format versions, cookie formats, database migrations, and
receipt fingerprints are not public API release numbers and do not get renamed to beta.

## Proposed breaking changes and rationale

1. Move all application HTTP routes under `/api/beta`, including authentication, system,
   recovery, inventory, tenant administration, and purchasing. Update generated declarations,
   the bundled frontend, verification scripts, and deployment probes together. Old application
   paths stop accepting new work. This makes the unreleased status uniform and avoids giving
   purchasing a separate lifecycle. Marking only OpenAPI metadata would avoid path churn but
   retain misleading public V2/V3/V4 identities.
2. Collapse purchasing to the current V4 capabilities with unversioned implementation type
   names. Remove historical public DTOs, write handlers, and SQL write commands once their
   shared helpers and compatibility readers have been extracted. Keeping parallel old writers
   adds repeated validation and risks overwriting fields older payloads cannot express.
3. Reject obsolete routes with a machine-readable `api_contract_unsupported` problem and
   reload guidance rather than redirecting or silently translating writes. No
   historical replay support is retained, including for previously successful requests. Current beta
   retries remain supported. No version has been released, and preserving development-only request
   contracts would retain the complexity this cleanup is intended to remove.

## Browser and deployment boundary

The owner approved removing beta revision negotiation during PR #126 feedback on 2026-09-17.
This supersedes the earlier revision-header and global reload-lock design. Deploy server and
built frontend as one unit against the single evolving `/api/beta` contract.

The change removes `X-Workbench-Api-Revision`, the `apiRevision` system field, mismatch middleware,
and the client's global write freeze/reload notice. It does not replace them with another manual
version. Retaining negotiation was considered but rejected because it adds a second contract
versioning mechanism to the deliberately evolving beta. Keeping only a response revision would
still require maintenance and would not guarantee safe behavior of older clients.

This is an approved public-contract break: consumers of the removed field/header must stop
expecting it. Old revision headers are ignored. `/api/beta`, retired-route rejection,
authentication, antiforgery, endpoint input validation, optimistic concurrency, and successful
exact-request replay remain. No stored content, receipt, or schema version changes because of
this API change. Generated OpenAPI JSON and TypeScript remain tracked and regenerated together.

Stale browsers are not globally detected or blocked. Ordinary validation or conflicts may reject
incompatible requests, while shape-compatible requests may still succeed. Preserve unsaved input
before manually reloading to load the current client. An uncertain save retains its request ID
and exact payload until resolved; keep its page open and inspect the saved record before starting
new work. Never replace an uncertain request ID solely to force another attempt. Frozen editors
retain keyboard-selectable recovery text for normal uncertain/conflict recovery. There is no
automatic reload or retry. Deploy the matching frontend/server together and stop old writers at
schema boundaries; rollback still requires an artifact compatible with the retained schema.

Acceptance: header-free bootstrap exposes only application identity; header-free writes still
require authentication and antiforgery; normal endpoint validation/conflict handling preserves
input; exact retries retain their body/identity; a retired-route error does not globally freeze
later beta requests. Retired routes remain unsupported without advertising a replacement revision.
## Stored data and successful retries

Preserve existing drafts, supplier and purchase identities, numbering, row versions, and
receipts. Retain read-only schema 1/2/3 projection while those records exist. Retire old write
procedures through a forward migration; do not rewrite base or applied migrations or re-fingerprint
stored receipts. The original consolidation migration has already been applied to a retained
preview, so a second forward migration removes its replay procedure and permissions. This is an
applied-schema boundary, not a reason to keep multiple migrations for disposable test databases.

Every request to a retired purchasing route is unsupported, regardless of whether its receipt
exists. Remove historical request DTOs, canonicalizers, receipt adapters and their contract-only
tests. Move normalization and validation still needed by beta into the current implementation,
preserving its behavior and coverage. Historical schema readers exist only to project stored
drafts, not to accept obsolete requests.

The current beta contract retains immutable successful-request replay, collision detection,
authorization, antiforgery protection, request limits and tenant isolation. Receipt rows from
earlier development iterations remain stored, but callers cannot replay them through retired
routes. This explicit boundary is approved because Workbench has no released consumers; retaining
all development request formats would add complexity without a supported compatibility obligation.

Rollback is allowed only to an artifact compatible with the resulting schema and available SQL
commands. Removing old procedures prevents assuming that the previous application can simply
be restarted. Back up before migration; otherwise use the documented restore procedure and
account for writes since the backup. This cleanup does not authorize deleting retained data.

## Implementation and acceptance

After approval, keep a temporary implementation plan and execute sequentially because routing,
generated contracts, request protection, and purchasing persistence are coupled.

- Inventory every route, DTO, generated consumer, SQL command, content reader, and receipt
  fingerprint before deletion; record each retained compatibility component and purpose.
- Use TDD for beta routing/OpenAPI, rejected unsupported requests, stale browser behavior,
  safe reload handling, and exact uncertain-save replay without duplicate effects.
- Verify rejection of historical routes, current beta replay, collisions, actor/tenant isolation,
  concurrency, and prevention
  of field loss against real SQL. Assess affected behavior with mutation tooling if available;
  otherwise state the limitation.
- Test fresh installation and upgrade from the PR base schema with schema 1/2/3 drafts and
  retained receipt rows. Also verify upgrade from the already-applied consolidation migration,
  removal of replay-only SQL authority, and current beta retries. There is no released schema yet;
  do not invent one.
- Regenerate OpenAPI/client declarations and confirm only beta business contracts are exposed.
- Run the required verification and container gates, refresh this checkout's isolated preview,
  and inspect purchasing and reload/retry workflows in the browser. Update living architecture,
  deployment, and purchasing documentation to describe verified behavior.
- Commit and open a ready-for-review PR after implementation and verification. Do not claim
  issue #120 complete based only on this proposal or the policy wording.
