# H1: durable collection notebook

**Status: Implemented** — the owner approved implementation of H1 and the inventory foundation
after reviewing the schema, GemInv comparison, and delivered-action boundary.

## Scope and evidence

Implement [H1](2026-09-06-first-hobbyist-scenario.md): save a name, optional notes and storage
location, browse the collection, and reopen readable details across authenticated sessions.
Apply the accepted [UI guidance](2026-09-06-ui-design-guidance.md), including appearance on public
and authenticated surfaces. Photos, search, editing, deletion, accounting, and taxonomy remain
outside H1. A neutral missing-photo placeholder is sufficient.

The current `App.tsx` displays session and user administration after sign-in. The server already
provides authenticated tenant context, EF tenant filters and ownership interception, SQL tenant
isolation, antiforgery protection, and generated API declarations. Extend these boundaries;
retain React, semantic HTML, ordinary CSS, and the existing authentication behavior.

## Record and authority

The accepted [inventory domain foundation](2026-09-06-inventory-domain-foundation.md) refines
this design after GemInv inspection. Its physical schema and domain invariants take precedence:
add immutable `TrackingKind = Individual` and `RowVersion`, use `StorageLocation` internally
(API `location`), and paginate chronologically by `(CreatedAtUtc, Id)`. H1 still creates only
individual objects; quantities and stock operations are separate later workflows.

Add an `Inventory.Items` table with a server-assigned UUID `Id`, `TenantId`, `Name`, nullable
`Notes` and `StorageLocation`, server UTC `CreatedAtUtc`, and a creation request UUID `CreationRequestId`.
Names are not unique. Each record represents one individually tracked piece.

- Name: required, at most 200 UTF-16 code units after trimming outer whitespace.
- Location: optional, at most 200 UTF-16 code units after trimming outer whitespace.
- Notes: optional, at most 4,000 UTF-16 code units; preserve nonblank text and line breaks.
- Normalize absent or whitespace-only optional values to null. Reject over-limit values without
  truncation. Render all values as plain text; preserve readable note line breaks.
- Any enabled authenticated tenant member may create and read the tenant's collection. User
  administration continues to require its existing permission. H1 introduces no per-item owner
  or separate collection role.
- Derive tenant identity exclusively from the validated session. Use `ITenantOwned`, the existing
  EF ownership controls, tenant foreign keys, and SQL RLS filter/block predicates. Scope database
  grants to the runtime operations needed by H1. Missing and foreign-tenant IDs both return 404.

## API contract and duplicate protection

All endpoints require authentication, return private/non-cacheable responses, and follow existing
Problem Details conventions. POST requires the existing antiforgery protection.

| Endpoint | Contract |
| --- | --- |
| `POST /api/items` | Body: `creationRequestId`, `name`, optional `notes`, optional `location`. Return 201 with saved detail and Location header for a new record. |
| `GET /api/items` | Return summaries (`id`, `name`, `location`, `createdAtUtc`) and nullable next cursor, in ascending `(createdAtUtc, id)` order, 50 rows per page. |
| `GET /api/items/{id}` | Return `id`, `name`, `notes`, `location`, `createdAtUtc`; 404 if inaccessible or absent. |

The list cursor contains the last returned creation timestamp and UUID; use the same SQL ordering and comparison for each
page. Invalid cursors return 400. The UI offers Load more so every existing record is browsable;
this is not a snapshot or a search contract. Refresh starts a new traversal.

Generate one nonempty creation request UUID per draft and reuse it for explicit retries. A unique
`(TenantId, CreationRequestId)` constraint makes competing identical submissions create exactly one
record. A replay with identical normalized fields returns 200 with that record; reuse with different
fields returns 409 and does not alter it. Resolve unique-key races by reading the committed record
in the same tenant. Different request IDs may create identically named pieces intentionally.

Freeze the submitted payload while pending. Disable duplicate submission immediately. After an
uncertain transport/server outcome, retain the submitted payload and request ID and offer an explicit
retry to resolve that operation before editing it; do not silently submit again. A definitive
validation rejection permits correction. Never display success before an authoritative response.
If the user abandons an uncertain save, explain that it may already have completed and that the
collection can be checked; cancellation cannot undo an accepted server request.

## User experience

The signed-in entry point is Inventory with a Collection heading, an inviting empty state and
Add item action. Use a simple responsive list with neutral placeholders, readable names, locations,
and access to details. Details expose the complete stable identifier and all saved text. After
successful creation, open the saved detail; returning to the collection reloads its first page.
Expose account/session management separately and administration only to authorized users.

For this three-destination slice, navigation stays directly visible and wraps on narrow screens.
The UI guidance's drawer becomes useful as navigation grows; H1 avoids adding a menu interaction
to these few destinations. Both layouts retain ordinary links and browser history behavior.

The short add form has persistent labels, optional-field labels, examples, Save item, and Cancel.
Associate validation messages with inputs, focus the first invalid field, and preserve all input
on validation or recoverable failure. Distinguish loading, empty, unavailable, and retry states.

Keep drafts only in memory. For dirty drafts, Cancel, app navigation, history navigation, and
explicit sign-out offer Keep editing or Discard changes before leaving. A clean cancel creates
nothing. Warn on browser unload where supported. Involuntary authentication loss clears protected
data through existing session handling; it cannot promise draft recovery after sign-out.

Use the accepted compact stag/Workbench branding and semantic bronze, warm neutral, and charcoal
tokens. Appearance offers System/Light/Dark on public surfaces and in account settings. Persist
only this preference locally, safely falling back to System for inaccessible/corrupt storage.
Resolve it before first paint under the existing CSP; follow OS changes only in System mode.
Update color-scheme and browser theme color without remounting forms or losing focus and scroll.
Provide visible focus, 44px touch targets, wrapping identifiers, readable long notes, and no page
overflow at 320 CSS pixels. Verify title contrast in light/dark and hover/selection states.

## Migration and recovery

Deliver one additive migration for the new table, constraints, indexes, RLS, runtime grants, and
required readiness/schema changes. Preserve every base migration. Verify both clean creation and
upgrade from the PR base schema with real SQL Server and the actual restricted principals.
Reject a destructive down migration that would erase saved collection records. Recovery uses a
reviewed forward correction or the existing offline backup/restore and sanitation runbook.
Document the resulting schema compatibility requirements in the migration runbook; never let the
web process migrate. Regenerate and commit OpenAPI and TypeScript declarations.

## Alternatives and tradeoffs

An unbounded list is simpler but grows without a resource boundary; fixed-size traversal keeps all
records reachable. Button disabling alone cannot handle a lost response or competing requests;
creation request uniqueness provides durable duplicate protection. A new collection permission
could support read-only members later but adds setup requirements without an H1 role requirement.
Browser-persisted drafts would improve recovery but store private collection data beyond the
authenticated session. Separate creation request identity keeps item identifiers server-assigned.

## Verification and delivery

Write focused failing tests first, with GIVEN/WHEN/THEN comments. Cover normalization and limits,
HTTP authentication/antiforgery, validation, real SQL persistence, cross-tenant direct reads and
writes, pagination, identical-name acceptance, and concurrent replay/mismatched-request behavior.
Verify database failures and uncertain-save retry without duplicate creation.

Client tests cover the full create/list/detail flow, input preservation, immediate duplicate-click
prevention, dirty navigation choices, and appearance persistence/fallback without state loss.
Exercise the running application with real authenticated persistence after reload and another
session, desktop and 320px layouts, keyboard navigation, both themes, loading/error/retry, and
cancel/discard. Record local URLs and distinguish browser evidence from integration assertions.

Run affected mutation testing, investigate meaningful survivors, and report unavailable tooling
or coverage limits. Run `scripts/verify.ps1` and `scripts/smoke-container.ps1`, including migration
and permission drills. Perform an internal implementation review, fix in-scope findings, update
living documentation, then commit, push, and open a ready-for-review PR. Merge remains separate.

Human hobbyist usability validation remains external evidence, not something automated checks
can establish. Completing H1 delivers a text collection notebook and does not complete H2–H4.

### Development evidence

Focused tests first demonstrated missing creation (HTTP 404 instead of 201) and incorrect
prior-schema readiness (200 instead of 503). Integration subsequently exposed the principal
provisioning allowlist gap; its success test failed before the narrow inventory grant entry was
added. Tests retain rejection of UPDATE, DELETE, and grant-option authority.

Independent source review found a dirty-form guard failure during native fragment navigation.
Regressions at initial and later history positions failed before the fix; same-page fragment
navigation now preserves the draft while ordinary route changes still require confirmation.

Six targeted manual mutations were detected: accepting an oversized name, bypassing replay
conflict checks, removing the immediate duplicate-submit guard, changing the retry request ID,
using the wrong corrupt-storage fallback, and bypassing dirty navigation. Every mutation was
restored and the relevant tests passed afterward. These are bounded mutation experiments, not a
comprehensive mutation-tool run or a repository-wide mutation score.

Final verification completed 305 server tests, 30 client tests, and all four migration drills
(clean creation, upgrade, reversible rollback, and restore rollback). `scripts/verify.ps1`
passed its restore, formatting, generated-contract, build, unit/integration, and migration stages;
its browser stage exposed an appearance-selector mismatch and narrow-screen footer overflow.
After those fixes, the full 13-test browser suite and `scripts/test-publish.ps1` passed separately.
The final `scripts/smoke-container.ps1` run also passed, including authenticated inventory
create/replay/read/list using the restricted runtime principal. Independent implementation
review found no remaining actionable findings after the fragment-navigation correction.

Browser workflows ran at `http://127.0.0.1:4179`; the published release probe ran at
`http://127.0.0.1:64264` and the final hardened container at `http://127.0.0.1:56391`.
These disposable verification instances were cleaned up afterward. Desktop dark and mobile light
screenshots were visually inspected; browser assertions covered both appearances at 320px and
1280px, keyboard submission, contrast, touch targets, and page overflow. Container checks also
covered local Compose TLS and durable sessions; public CA issuance and SMTP delivery were not tested.
