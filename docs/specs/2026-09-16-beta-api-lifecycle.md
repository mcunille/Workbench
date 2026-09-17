# Beta API lifecycle and purchasing consolidation

**Status:** Implemented — the owner explicitly approved this design on 2026-09-16.
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
   reload guidance rather than redirecting or silently translating writes. Keep narrowly
   scoped receipt-only replay handling as described below; it is not a supported old API.

## Browser and deployment boundary

Deploy server and built frontend as one unit. Add a beta contract revision checked on API
requests before business handling; change it when beta changes incompatibly, not for every
build. Missing or mismatched revisions cannot perform business writes. Bootstrap endpoints
needed to discover the revision and authenticate require explicitly tested exceptions.

The bundled client surfaces a reload-required state, prevents new writes, and preserves
unsaved edits for user recovery instead of automatically reloading or resubmitting. A browser
from before this mechanism may only show its existing error UI; its obsolete requests must
still fail without mutations. Test that transition separately from future beta mismatches.

An uncertain save retains its request ID and exact payload until resolved. Never convert an
uncertain old save into a new beta request with a new ID. Document reload/recovery instructions
for older browsers that cannot display the new state.

## Stored data and successful retries

Preserve existing drafts, supplier and purchase identities, numbering, row versions, and
receipts. Retain read-only schema 1/2/3 projection while those records exist. Use one forward
migration to retire old write procedures and establish any required receipt lookup authority;
do not rewrite base migrations or re-fingerprint stored receipts.

For requests to retired purchasing write routes, a restricted compatibility adapter may
return an existing successful receipt only after matching tenant, actor, operation, target,
expected version, and the exact historical canonical fingerprint. It must never execute a
new write or accept merely a known request ID. Missing receipts return unsupported-contract;
different input using an existing request ID remains a conflict. Preserve authorization,
antiforgery, size limits, and tenant isolation for this path. Exclude these adapters from
OpenAPI and generated clients.

Retain historical canonicalizers only for receipt matching, with frozen compatibility tests;
they no longer implement evolving business rules. Their lifetime is tied to retained receipts,
not releases. No receipt expiry is introduced by this change. Removing them later requires an
explicitly approved retention/transition policy. Internal storage reader types may retain
format numbers where those identify persisted data rather than public API versions.

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
- Verify historical replay, collisions, actor/tenant isolation, concurrency, and prevention
  of field loss against real SQL. Assess affected behavior with mutation tooling if available;
  otherwise state the limitation.
- Test fresh installation and upgrade from the PR base schema with schema 1/2/3 drafts and
  receipts for all four fingerprints. There is no released schema yet; do not invent one.
- Regenerate OpenAPI/client declarations and confirm only beta business contracts are exposed.
- Run the required verification and container gates, refresh this checkout's isolated preview,
  and inspect purchasing and reload/retry workflows in the browser. Update living architecture,
  deployment, and purchasing documentation to describe verified behavior.
- Commit and open a ready-for-review PR after implementation and verification. Do not claim
  issue #120 complete based only on this proposal or the policy wording.
