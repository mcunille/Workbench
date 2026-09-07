# First hobbyist scenario: remember and find my collection

**Status: Proposed** — a product scenario and story proposal, not approval to implement or a
committed sprint forecast.

## Direction and evidence

Start with a hobbyist who owns individually identifiable gemstones or jewelry and currently
remembers them through boxes, photographs, or informal notes. Their immediate question is:
“What is this piece, and where did I put it?” They may not know its formal classification.
This is a working persona to validate with collectors, not a claim from user research.

Hobbyist-first follows the [product vision](../VISION.md) and
[progressive-capability principle](../DESIGN-PRINCIPLES.md). Build a useful collection tool with
durable records and room to grow; do not require accounting, a business profile, or a permanent
“hobbyist mode.” Professionals should eventually extend the same records and workflows.

Apply the accepted [UI guidance](2026-09-06-ui-design-guidance.md): bronze branding, warm neutral
surfaces, readable item identity, short forms, responsive navigation, accessible controls, and
System/Light/Dark appearance. The [mockup](../design/README.md) is a visual reference; its filters,
metrics, financial examples, and other navigation destinations are not sprint requirements.

The current application has account/session administration and infrastructure foundations, but
the collection experience is not implemented. A tenant and authorized account must already be
available through existing setup or invitation flows. Public registration, tenant provisioning,
and production deployment are separate scenarios.

## Sprint outcome

**A hobbyist can record a real item on their phone, recognize it by its photograph, return later
on desktop or mobile, find it, and keep its description and storage location accurate.**

Example: Alex brings home a blue stone, saves “Blue stone from the September fair” with location
“Tray A, slot 3,” and adds a photograph. On a later visit Alex searches for “September,” opens the
matching item, and checks the photograph and location. After moving it, Alex changes its location
to “Display box” and can retrieve that updated record after signing in again.

Success means completing that loop with persisted user data, without developer intervention or
configuring purchasing, bookkeeping, commerce, or a taxonomy. A static demonstration or separate
unconnected screens does not complete the scenario.

## Minimum product decisions

- One record represents one individually tracked collectible. Bulk stock, parcels, quantities,
  sets, and component relationships are deferred; this is not a final model for those concepts.
- A descriptive name is the only required user-entered field. Notes and storage location are
  optional plain text. Users can record uncertainty in their own words.
- The system assigns a stable item identifier. Names need not be unique; similar pieces are normal.
- Offer one optional photograph per item. A missing photograph has an honest placeholder.
- Location describes where the item is stored; it does not create a location-management subsystem.
- Do not collect price, currency, valuation, measurements, or formal classification in this slice.
  Acquisition and domain-specific attributes need their own requirements, including financial rules.
- Collection records and photographs remain private to the authorized tenant. No public sharing.

These decisions are proposed boundaries for the first scenario. API/schema details, input limits,
photo formats/size limits, and migration design must be settled before implementation. Existing
identity, tenancy, blob-provider, and API-contract conventions remain authoritative.

## Value-delivering stories

Each story includes its UI, API, persistence, authorization, and verification as needed. Technical
tasks belong under the story; a database, endpoint, or component library alone is not a delivered
user story. Deliver in this order, with each increment usable in the running application.

### H1 — Keep a record of an item

**As a hobbyist, I want to save a name, notes, and location for a piece so that I do not have to
remember its identity and whereabouts.**

Deliver a collection entry point, an inviting empty state, a short add form, a basic collection
list, and readable item details. The list is sufficient to reopen an item before search exists.

- **Given** an empty collection, **when** I save a name with optional notes and location,
  **then** I see the saved item and can reopen it after reload or a new authenticated session.
- **Given** only a name, **when** I save, **then** the item is accepted without extra setup.
- **Given** an empty or whitespace-only name, **when** I submit, **then** an associated error
  explains what to fix and preserves the other entered values.
- **Given** a failed save, **when** the form reports the failure, **then** recoverable input
  remains available and success is not shown. A duplicate click must not create two records.
- **Given** unsaved changes, **when** I cancel or navigate away within the app, **then** I can
  choose to keep editing or discard them; no item is created by canceling.

**Value at completion:** a durable, browsable collection notebook.

### H2 — Recognize a piece from its photograph

**As a hobbyist, I want to attach a photograph to a saved item so that I can distinguish it from
similar pieces.**

Add a photograph from the device's file/photo picker on item details; the native picker may offer
camera capture where supported. Display it in the list and details without altering its colors.
Permit replacement and removal of the attachment without removing the item.

- **Given** a saved item, **when** I upload a supported photograph, **then** its thumbnail and
  detail image remain available after reload and in another authorized session.
- **Given** a missing photo, **when** I view the item, **then** its name and location remain
  fully useful alongside a neutral placeholder.
- **Given** an unsupported/oversized file or failed upload, **when** it is rejected,
  **then** I receive an actionable error and the item and any previous photo remain intact.
- **Given** an existing photo, **when** replacement succeeds or I explicitly remove it,
  **then** the displayed attachment changes accordingly and the item remains saved.

**Value at completion:** visual identification of similar items. Depends on H1.

### H3 — Find an item when I need it

**As a hobbyist, I want to search my collection by words I remember so that I can quickly find
the right piece and its location.**

Add simple, case-insensitive text search across name, notes, and location. Use one ordinary text
query without advanced syntax; show all items when it is empty. Start with one responsive list
with thumbnails, not simultaneous gallery, table, and configurable-view systems.

- **Given** several saved items, **when** I search for a phrase in a name, note, or location,
  **then** matching items appear and I can open their details.
- **Given** no matches, **when** I clear the search, **then** the collection returns without
  changing or deleting records.
- **Given** a selected search result on mobile, **when** I return from its details,
  **then** my query and list position are preserved.
- **Given** a collection loading failure, **when** the page renders, **then** it distinguishes
  failure from an empty collection and offers retry.

**Value at completion:** reliable retrieval as the collection grows. Depends on H1; includes
thumbnails when H2 is available.

### H4 — Keep the record accurate

**As a hobbyist, I want to correct an item's name or notes and update its location so that my
collection remains useful after I learn more or move a piece.**

- **Given** a saved item, **when** I edit and save its fields, **then** the same item identifier
  remains and the new values appear in details and subsequent searches after reload.
- **Given** an edit, **when** I cancel, **then** the previously saved record remains unchanged.
- **Given** a save failure, **when** I return to editing, **then** my recoverable edits remain
  available with a clear retry path.
- **Given** the item changed in another session after I opened it, **when** I save stale edits,
  **then** I am told about the conflict and can recover my edits without silently overwriting
  the newer record. Real concurrent-session verification is required.

**Value at completion:** records stay trustworthy through ordinary use. Depends on H1; verify
search reflects edits with H3.

## Shared completion criteria

Apply these to every story, rather than leaving quality and integration to a final sprint story:

- Desktop and mobile layouts support the actual task, including keyboard operation, visible focus,
  labeled inputs, accessible status/errors, and touch targets of at least 44 CSS pixels.
- System/Light/Dark appearance follows the UI spec, preserves task state when switched, and persists
  the preference as specified. Normal text meets 4.5:1 contrast; item titles remain legible on white,
  pale bronze selection, hover, and dark surfaces. Verify at 320 CSS pixels without page overflow.
- Another tenant cannot list, search, read, change, or retrieve photographs of these items, including
  direct identifier requests. Authorization is server-enforced; hidden navigation is not a control.
- Save and upload outcomes are truthful. Loading, empty, error, validation, and retry states are
  exercised. Use existing session behavior when authentication expires.
- New behavior has focused tests written first, including negative and cross-tenant cases. Verify
  meaningful concurrency and storage failures where affected. Apply repository mutation guidance
  and report coverage limits; run the application gates in [CONTRIBUTING](../../CONTRIBUTING.md).
- Required schema/API changes include generated client contracts, migration/upgrade evidence, and
  documented recovery consistent with existing runbooks. Photo delivery uses the accepted provider
  boundary; removal follows its retention rules rather than promising immediate physical erasure.

## Sprint demonstration and planning boundary

Demonstrate H1 through H4 as one journey with a real saved item and several distinguishable fixtures.
Start on a narrow-screen device, save the item, and attach its photo. Reload or sign in on desktop,
search, open the record, update its location, then verify the new value from mobile. Repeat relevant
states in both themes. Separately demonstrate failed saves/uploads, an edit conflict, and cross-tenant
denial through the appropriate UI or integration evidence.

Try the journey with a hobbyist without coaching. Record whether they can complete it, where they
hesitate, and whether the returned record helps them locate the physical piece. This validates the
persona hypothesis; a passing automated test alone does not establish usability.

The four stories are the proposed scenario scope, not a promise that they fit an unknown sprint.
Estimate against team capacity and the existing authentication/blob integration before committing.
H1 includes the first production collection screen and theme/shell work, so it is likely the largest
slice. Execute sequentially with integrated demonstrations instead of postponing integration.

If capacity is insufficient, explicitly reframe the sprint goal before commitment to a **text-only
collection notebook**, retaining save, browse/search, and update while deferring H2. Do not count
the photo-based scenario as complete with its photograph story unfinished. Cut secondary presentation
options before cutting persistence, authorization, accessibility, or recovery.

## Deliberately later

No dashboards, public sharing, bulk import/edit, saved views, configurable columns, multiple photos,
formal gem taxonomy, valuations, purchase orders, bookkeeping, work orders, or commerce in this sprint.
Record archival/deletion and collection export also need subsequent explicit scenarios; photo removal
does not imply item deletion. The initial release should be evaluated as a limited collection pilot.

Grow through further complete scenarios, selected from observed needs rather than a fixed roadmap:

1. **Care for my growing collection:** safely archive mistakes and export records, then add grouping
   or richer identification where collectors need it. Portability should not be reserved for experts.
2. **Remember an acquisition:** connect items to seller, date, provenance, and supporting documents;
   define financial semantics before adding amounts or accounting effects.
3. **Manage many items efficiently:** import, batch operations, denser views, and saved searches with
   validation, partial-failure recovery, and the same underlying records.
4. **Operate a business:** add roles, work orders, financial controls, and commerce as separately
   accepted end-to-end scenarios. Do not expose empty destinations in the hobbyist experience.

The alternative of beginning with a broad inventory dashboard or a fully classified gem form would
make the initial interaction heavier without completing this collector's immediate task. Conversely,
a disposable local-only notebook would fail the durability and growth requirements. This proposal
keeps the interaction small while using the established application foundations.
