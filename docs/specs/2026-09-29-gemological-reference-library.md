# Gemological reference library

**Status: Proposed** — design reviewed with the owner on 2026-09-29, including service-admin curation; written-spec review and implementation planning remain pending.

## Purpose and audience

Workbench should provide a useful, source-backed gemological reference for collectors and gemstone businesses. People should be able to find a gem by a familiar name, understand its classification, and curate additional knowledge for their own tenant. This first release is a reference library: it does not classify inventory items, identify a specimen, or infer its geographic origin. A later workflow may link an inventory item to a reference entry without replacing the item's user-entered name.

The owner requested group, species, variety, common name, and a locality indication for gems known from one commercial source. The owner clarified that refractive index (RI), birefringence, optical character, specific gravity (SG), and similar values are **reference values for a gem type**, not measurements of a particular inventory item. Those properties are a later phase, for which this spec establishes semantics and sourcing rules.

## Research and terminology

GIA defines a mineral **group** as related species, a **species** principally by composition and crystal structure, and a **variety** by traits such as color, transparency, or phenomenon. Group and variety apply when appropriate; they are not compulsory levels in every identification. GIA gives ruby as a corundum variety and aquamarine as a beryl variety. [GIA Gem Identification introduction](https://elearning-samples.gia.edu/Gem_Identification/Page5.html); [GIA Gem Project data model](https://www.gia.edu/about-gia-gem-project-gubelin-collection).

The mineral ladder does not cover every gem. GIA describes opal as commonly treated as a mineraloid, lapis lazuli as a rock made of several minerals, and pearls as organic products of mollusks. The library must represent these without inventing a mineral species. [GIA on opal](https://www.gia.edu/gems-gemology/summer-2025-phenomenal-gemstones); [GIA on lapis lazuli](https://www.gia.edu/lapis-lazuli-description-v1); [GIA on pearls](https://www.gia.edu/gems-gemology/summer-2021-pearl-classification-the-gia-7-pearl-value-factors).

Tanzanite is the violet-blue to blue-violet variety of zoisite and is commercially mined in the Merelani Hills of Tanzania. This is a sourced statement about the gem type, not proof of an individual stone's provenance. In contrast, tsavorite is a grossular garnet found in more than one country and must not receive a single-locality default merely because its name recalls Tsavo. [GIA tanzanite description](https://www.gia.edu/tanzanite-description); [GIA on tsavorite sources](https://www.gia.edu/birthstones/january-birthstones); [GIA on origin determinations](https://www.gia.edu/gems-gemology/winter-2019-analytical-methods-geographic-origin-determination-gemstones).

GIA's public gem pages publish example RI, birefringence, SG, and hardness values; its research collection also records optic character and specimen measurements. The two types of values must remain distinguishable. For example, GIA's tanzanite overview gives RI 1.691–1.700, birefringence 0.008–0.013, and SG 3.35, while an individual GIA collection specimen has its own results. [GIA tanzanite overview](https://www.gia.edu/tanzanite); [GIA Gem Project fields](https://www.gia.edu/about-gia-gem-project-gubelin-collection); [GIA specimen example](https://www.gia.edu/doc/zoisite-tanzanite-35038.pdf).

“GIA aligned” here means following cited terminology where applicable. It does not mean GIA approved, certified, or endorsed Workbench or its entries. Store concise, independently written facts and links, not copied GIA prose, tables, photographs, or a bulk reproduction of its database. [GIA copyright and trademark guidance](https://www.gia.edu/copyrights-trademarks).

## Current product and GemInv comparison

Workbench currently saves an individually tracked collection item with a required user-entered name and optional notes/location. Its inventory foundation reserves optional typed gemstone details for a later change and explicitly preserves the owner's name beside any future calculated description. The current collection flow requires no formal taxonomy. [Collection guide](../collection.md); [inventory domain foundation](2026-09-06-inventory-domain-foundation.md).

GemInv offers a reference list of editable `GemTaxonomyMapping` rows with group, species, optional variety, common name, and origin. Inventory and report records can refer to a mapping. Its group/species validation and flat rows are a useful interaction reference. Workbench needs a shared curated layer, tenant additions and overrides, source provenance, support for non-mineral materials, and a deliberate distinction between type reference properties and specimen observations. The inspected GemInv checkout is the local `C:/Users/mcuni/git/geminv` source; no GemInv data migration is in scope.

## First-release scope

1. Provide an authenticated reference-library page with searchable entries, material-kind and group filters, concise result rows, and an entry detail page. Search covers preferred common name, aliases, group, species, and variety. The detail view explains missing levels rather than displaying a fictitious value.
2. Ship a small, reviewed Workbench catalog spanning important structural cases: corundum/ruby and sapphire; beryl/emerald and aquamarine; garnet/grossular/tsavorite; zoisite/tanzanite; and opal, amber, pearl, and lapis lazuli. Each published entry has source links and a review date. This is a representative starting set, not a claim of exhaustive gem coverage.
3. Allow signed-in tenant members to add, edit, archive, and restore tenant-owned entries, and to override or reset fields of Workbench entries for their tenant. Existing tenant authorization and anti-forgery patterns apply. The tenant boundary is enforced by the server and SQL, not by filtering in the browser alone.
4. Make the source layer visible: **Workbench reference**, **tenant entry**, or **Workbench reference customized for this tenant**. Display source links and last review date; identify tenant-written assertions separately from Workbench assertions.
5. Keep the library independent of collection items, purchase lines, lab reports, exports, and pricing in this release. Existing workflows and item names remain untouched.
6. Give Workbench service admins a dedicated way to stage, review, and publish shared entries. Service-admin authority is separate from tenant membership and does not provide tenant-data access.

## Reference-entry model

Every entry has a stable ID, `materialKind`, preferred `commonName`, zero or more search aliases, an optional short description, and source assertions. Initial material kinds are **mineral**, **mineraloid**, **organic**, and **rock/aggregate**. These are reference classifications, not statements that an individual object is natural. Natural versus laboratory-grown, imitation, and assembled material need a separate item or material-design treatment before such claims are introduced.

For mineral entries, `species` is required; `group` and `variety` are optional. For non-mineral entries, the applicable taxonomy fields may be absent, and the detail view explains the material kind. `commonName` is always present and may equal the species or variety name. Aliases are search terms, not alternate taxonomic identities. A classification entry describes one recognizable gem identity; names that designate a genuinely different variety get separate entries.

An optional **notable-locality assertion** records a place, the exact scope of the claim (for example, “only known commercial source”), a citation, and review date. It can be added only when the source supports that scope. The claim can change as discoveries change and never pre-fills or proves the origin of an inventory item. General source regions are future editorial content, separate from the exceptional single-locality assertion.

Sources belong to assertions rather than to an unqualified record-wide bibliography: classification and locality claims can cite different material. A Workbench entry must cite its substantive claims. Tenant entries may remain unsourced for personal reference, visibly marked as such; a tenant locality assertion or future numerical property requires a source URL or identifiable publication. A source record includes title, publisher, URL or citation, date accessed/reviewed, and the exact fields or claims it supports. Each effective field resolves its value, attribution, supporting source assertions, and review date together. An inherited field always uses the current Workbench value and its current sources; a replaced field uses tenant attribution and only its tenant-supplied sources, if any; a cleared field has tenant attribution and no supporting assertion. An overridden field never retains a citation or review date for the displaced Workbench value. A notable-locality assertion is one claim with its own citation and review date. External links are inert content, validated to safe schemes, and displayed without embedding remote media.

## Shared catalog, tenant additions, and tenant overrides

Workbench ships curated entries with stable IDs and maintains them through service-admin publishing. A tenant cannot mutate those rows. Tenant entries have tenant-qualified IDs and are visible only in that tenant. A tenant override refers to a curated entry ID and stores only deliberately overridden fields. For each field, the state is **inherit**, **replace**, or, where optional, **clear**. A blank text input does not silently mean “inherit.” An alias list is replaced as a unit when overridden; otherwise it inherits. Source assertions are attached to the field or claim they support, not overridden as an independent record-wide list. Replacing or clearing a field replaces or removes that field's effective source assertions; resetting it restores the current Workbench value and sources together.

The server resolves a tenant's effective entry by applying that tenant's overrides to the current published curated entry. An upstream correction reaches inherited fields and their sources; replacement and explicit-clear fields remain tenant choices. Search, filters, detail, and edits use that same effective projection. The UI marks changed fields, shows their Workbench values, and allows a single-field reset or whole-entry reset. Tenant edits carry a concurrency token so competing saves require review. A curated update does not silently delete a tenant override or change its source attribution.

After a shared publication, validate affected effective entries against the current material-kind and required-field rules when they are read or written. Publication is not blocked by existing tenant overrides. If an inherited change makes retained overrides invalid (for example, a cleared species becomes required when a shared entry changes from non-mineral to mineral), preserve the entry ID and every override, and mark the tenant's effective entry **Needs review** with field-level reasons. Keep it visible in the tenant library and searchable by name, with a warning rather than presenting its conflicting classification as validated. Show current Workbench values beside tenant values and allow an explicit repair, reset of conflicting fields, or whole-entry reset. Reject unrelated edits and further invalid saves until the tenant reconciles the entry; never silently drop a clear or replacement. Validation on reads and writes must catch the state even if the tenant has not visited the library since publication. Other tenants' entries remain unaffected.

Normalize identity text for duplicate detection without changing display spelling. Identity comparison uses material kind plus applicable group, species, variety, and common name; aliases do not define identity. Reject creation of a tenant entry that exactly matches another visible entry's effective identity, and reject an override that creates an exact collision at edit time. A later shared-entry publication may create a collision with existing tenant data; retain both stable IDs, flag the conflict for review, and never silently merge or erase either entry. A tenant can resolve it by editing, resetting, or archiving its own data. An archived tenant entry disappears from ordinary search but remains restorable with its ID. Curated entries are retired by a service-admin publish action with a redirect or explanation, never physically removed during an update.

The owner approved shared reference plus tenant additions and field-level overrides. The initial write permission follows the current collection model for signed-in tenant members; a separate role policy can narrow this later if real collaboration needs warrant it.

## Service-admin identity and shared curation

Workbench currently has tenant administrators and a narrow operator CLI, but no service-admin account or web console. The existing tenant user and session require a tenant ID. The operator principal is reserved for named maintenance commands; it is not a browser identity and its credential must not enter the web process. Introducing shared-catalog editing therefore requires a separate service-admin authority, not a special tenant role or a tenant ID bypass. [Data and tenancy design](2026-09-01-data-identity-tenancy.md); [current architecture](../ARCHITECTURE.md#tenant-isolation).

Operator-only commands provision and disable service-admin identities, reset a lost credential, and revoke sessions. The first release has no public service-admin registration or tenant-admin invitation path. A service admin signs in through a dedicated session and authorization scheme that has no tenant claim. Every service-admin request revalidates the account and session so disablement ends access; its cookie and routes are separate from tenant sessions. Possessing both a service-admin account and a tenant account requires separate sign-ins and grants no authority from one context to the other. Service-admin APIs cannot use tenant data contexts or accept a tenant ID to browse tenant entries, overrides, items, or users. Restore sanitation must invalidate all service-admin sessions and rotate any restored authentication state that could validate a pre-restore cookie or token. This includes sessions that predate a backup and cases where the restore rolls back an account disablement or session revocation; a restored account can access the admin UI only after a fresh sign-in, subject to its restored account state and operator review.

The dedicated admin UI lists the **shared** entries and their sources. It supports creating, editing, and retiring entries, including aliases and notable-locality assertions. Changes are staged as drafts visible only to authorized service admins. A review screen shows the combined field and source diff, validation results, and affected entry IDs. One service admin may review and publish their own selected batch; a second approver is not required in this release. A batch may contain changes to several gems and publishes atomically: either every selected change becomes visible or none does. Drafts outside the selected batch remain drafts. Published entries appear to tenants on subsequent reads, with untouched override fields inheriting the new values.

Publishing requires citations for substantive shared claims, checks material-kind and taxonomy compatibility, validates links and names, and rejects duplicate shared identities, including duplicates within the selected batch. Every edited entry carries a database concurrency token. If any selected entry changed after the draft was based on it, the entire batch remains unpublished; the editor shows the conflict and preserves all draft changes for review. A publish request has a durable request identity and outcome so an exact retry after a lost response cannot apply twice; changed input under that identity conflicts. A successful publish or retirement writes an append-oriented system audit record with actor, time, affected IDs, and changed-field summary in the same transaction as the data change. Rejected attempts write a separate failure audit record. Corrections are ordinary later edits. There is no catalog-wide revision number, version-history rollback feature, or revert UI in this release. The concurrency token and audit record serve different purposes from historical content versions.

The serving application receives only narrow catalog read/write commands needed for these routes, with authorization checked before invocation. It receives no operator or migration credential and no general tenant-data bypass. The database permission matrix and negative tests must prove that a tenant session cannot publish and a service-admin session cannot access tenant records, even by substituting IDs or calling tenant APIs directly. The admin UI is a prerequisite for maintaining the shared catalog after initial seeding; later reference-property editing uses the same curation authority and review/publish flow when that phase is designed.

## Interaction and failure behavior

Entry creation and editing are explicit saves with field-level validation and a visible success state. Required fields, material-kind compatibility, name length, alias limits, safe source links, and duplicate identity are validated server-side. A failed save preserves the draft. Conflict responses show the current effective entry beside the user's draft; the user chooses whether to revise and retry. An inaccessible entry gives a normal not-found response without revealing another tenant's data. The page distinguishes an empty catalog, no search matches, and a failed load with retry.

The detail page presents taxonomy first, then locality and sources. It must not show an unsourced numerical value, a specimen measurement as a type range, or a generic “origin: Tanzania” field that appears to certify an inventory item. Reference properties receive a section when the later property phase supplies reviewed data. Keyboard, mobile, and both appearance modes follow current Workbench UI guidance.

## Later reference-property phase

The property phase will add typed, source-backed assertions attached to reference entries. At minimum it must accommodate RI minimum/maximum (dimensionless), birefringence minimum/maximum (dimensionless), SG value or range (dimensionless), Mohs hardness value or range, and optical character with distinctions such as isotropic, uniaxial positive/negative, biaxial positive/negative, and aggregate/not applicable. “Doubly refractive” can be a plain-language display summary, not the only stored classification. The phase should also evaluate crystal system, chemistry, pleochroism, fluorescence, cleavage, toughness, and diagnostic cautions against actual source coverage and user need.

Each value must state whether it is a **published type reference**, an **observed specimen value**, or a **derived value**; this library accepts published type references only. A range is not an acceptance test for identification. Unknown, not researched, not applicable, and a measured zero are distinct states. An assertion stores its source, review date, units or scale, and any qualification such as material variety or locality. Conflicting reputable sources may coexist with attribution; the UI must not silently average them. Numeric bounds and optic-character applicability require focused scientific review before schema and seed-data implementation. Collection-item measurements, if later added, live in the item domain and remain separate.

## Alternatives considered

- **GemInv-style flat mapping rows:** fastest initial CRUD, but repeated group/species text, no curated-versus-tenant provenance, and difficult shared updates and source review. Reuse the familiar terminology, not the unqualified flat table.
- **Fully normalized mineral hierarchy:** stronger referential taxonomy for minerals, but forces an ill-fitting ladder on organic and aggregate gems and adds curation overhead before the reference is useful. Stable reference entries with explicit applicable fields are the first-release boundary.
- **One mutable global catalog:** edits by one tenant would affect others and undermine the curated source layer. Tenant entries and sparse overrides preserve both shared corrections and private choices.
- **Copy the entire curated entry on first tenant edit:** simple resolution but freezes untouched fields and hides later Workbench corrections. Field-level overrides keep inheritance explicit.
- **Make a tenant administrator global:** the tenant identity and session model requires one tenant, and a global flag would blur the platform/tenant authority boundary. Use separate service-admin identities.
- **Manage all shared data through deployments or an operator CLI:** appropriate for the initial pilot seed, but routine research corrections would require an operator or code release. Use the dedicated editor for ongoing curation.

## Compatibility, release, and recovery

The first release adds a new reference domain, tenant route, and service-admin route and identity. It does not rewrite existing items, names, exports, or purchasing records. Seed the pilot curated entries with deterministic IDs on first installation; later deployments must not overwrite service-admin edits. Ongoing shared-data changes use the publish workflow, preserving stable IDs. Database changes must follow the migration runbook and verify both fresh creation and upgrade from the merged base. Export/import of tenant entries and overrides is deferred; the existing backup/recovery contract must include shared entries, tenant data, service-admin drafts, and audit records. Restore sanitation must cover the new admin identity and session store as well as tenant sessions before serving requests. Tenant archive/restore and concurrency checks provide routine correction; deployment rollback must not delete tenant-authored data. A failed publish leaves the published catalog intact and drafts available for correction. No historical-content rollback UI is promised.

## Verification and success criteria

- Source review checks every seeded and newly published classification and locality assertion against linked primary references, spelling, material kind, and date. Review the rendered attribution and avoid copied prose/media.
- Service-admin tests cover provisioning and disablement, session isolation, restore sanitation after revoked or disabled access, draft visibility, source and duplicate validation, one- and multi-entry atomic publication, concurrent-edit rejection without partial publication, audit outcome, and denial of tenant-data access. Tenant sessions must fail against every service-admin route. Exercise the dedicated editor in a running preview with non-sensitive sample entries.
- API/SQL tests cover shared visibility, tenant isolation, additions, per-field value-and-source inheritance/replacement/clear/reset, behavior after a shared-entry publication, invalid-effective-entry reconciliation, duplicate conflict handling, archive/restore, concurrent edits, and inaccessible IDs. Component/browser tests cover search and source labels, source-link safety, draft preservation, keyboard/mobile use, and the Tanzanite/tsavorite locality distinction. Follow repository TDD, Gherkin-comment, mutation, and delivery gates when implementation is authorized.
- A user can find each pilot example by common name and at least one relevant classification term; see why a field is absent on non-mineral entries; add and later correct a tenant entry; override Tanzanite for their tenant; and reset one field to receive the current Workbench value. Another tenant sees no change.
- A later property phase is successful only when a reader can tell a sourced type range from an observation on a particular stone and can see the limits of the published value.

## First-release milestones and user stories

This roadmap covers the first **reference-library release**: shared and tenant entries, field-level overrides, service-admin curation, pilot content, and the two user interfaces. The later reference-property phase is design direction, not a story or completion condition here. Collection-item linking and the other reserved decisions below are also outside this roadmap. When **GEM-01 through GEM-10** are implemented, integrated, and verified against current source, the first-release requirements in this spec are met and its status can become **Implemented**. A future property release needs its own reviewed scope and stories.

Each story is an independently assignable handoff, not permission to skip design or implementation-plan review. An assignee receives this spec, prerequisite decisions and merged PRs, current repository guidance, and the story ID; they confirm the current code and follow the applicable superpowers handoffs. A predecessor means its behavior or decision has been reviewed, integrated, and verified, not merely mocked or proposed. Story owners provide a focused PR, affected tests and mutation evidence, and updated contracts or documentation. Milestone labels group outcomes; a story may start as soon as its own hard predecessors are complete. Coordinate edits to shared migrations, generated API contracts, navigation, and database permission scripts even when stories otherwise run in parallel.

| Milestone | Outcome and exit evidence | Stories |
| --- | --- | --- |
| M0 — Establish trusted foundations | The pilot claims are sourced, the shared reference read contract exists, and service-admin identity is separate from tenant authority. | GEM-01–03 |
| M1 — Make the data usable and curatable | The pilot catalog is installed, admins can publish shared changes, and tenants can store additions and overrides through protected APIs. | GEM-04–06 |
| M2 — Deliver both user workflows | Service admins can stage/review/publish in the UI; tenant members can browse, add, customize, and reset entries in the UI. | GEM-07–09 |
| M3 — Prove the integrated release | Cross-role and cross-tenant journeys pass from current source, product guidance is current, and the spec's first-release acceptance criteria are satisfied. | GEM-10 |

### M0 — Establish trusted foundations

#### GEM-01 — Curate the pilot claim set

**Story:** As a collector, I want familiar mineral and non-mineral examples backed by identifiable sources so that the reference is useful and its claims can be checked.

- **Scope:** Prepare the exact pilot entries named in this spec, including classifications, common names, aliases, applicable absent fields, Tanzanite's narrowly worded locality assertion, and claim-level source records. Review the distinction from tsavorite and avoid copied source prose or media.
- **Acceptance:** Every substantive Workbench claim has a supporting primary citation and review date; the set exercises group/no-group, species/no-species, variety/no-variety, and mineral/non-mineral cases. Another reviewer can reproduce the classification decisions from the linked sources.
- **Evidence:** A reviewed, machine-readable seed-content package or equivalent source dossier plus a claim-to-source checklist. Do not put unsourced numerical reference properties into this release.
- **Predecessors:** None. **Parallel:** GEM-02 and GEM-03; align field names with GEM-02 before finalizing the seed package.

#### GEM-02 — Persist and read the shared reference

**Story:** As a reader, I want a stable shared gem reference so that every tenant sees the same curated identity before personal additions or overrides.

- **Scope:** Add the shared entry, alias, source-assertion, locality-assertion, and retirement storage; stable IDs; validation and duplicate rules; bounded search/detail read APIs; and explicit SQL grants. Define the read contract consumed by tenant and service-admin work. No tenant write or admin publish behavior belongs here.
- **Acceptance:** Searches find common names and taxonomy terms; non-mineral entries need no invented species; retired entries retain identity and redirect/explanation; source claims are attributed; malformed queries fail predictably. Authenticated tenant members can read shared entries, unauthenticated callers are denied, and shared reads expose no tenant data.
- **Evidence:** Current-source API and SQL tests, fresh/upgrade migration checks, generated contract updates, and database-principal allow/deny checks.
- **Predecessors:** None. **Parallel:** GEM-01 and GEM-03. Integrate its schema and API contract before GEM-04, GEM-05, GEM-07, or GEM-08 consumes them.

#### GEM-03 — Give service admins a separate identity

**Story:** As a Workbench operator, I want to provision and revoke service-admin access so that shared-gem curation has accountable authority without tenant-data access.

- **Scope:** Separate service-admin accounts, sign-in/session validation, operator-only provision/disable/reset/revoke commands, protected service-admin route policy, and narrow database authority. No catalog editor or publishing workflow belongs here.
- **Acceptance:** Service-admin and tenant sessions cannot substitute for each other; disablement or revocation ends authority on the next request; restore sanitation invalidates pre-restore service-admin sessions even when the restore rolls back a disablement or revocation; no service-admin request obtains tenant context or tenant records; operator and migrator credentials remain absent from the web process.
- **Evidence:** Real HTTP/SQL negative tests for route and ID substitution, session revocation, restore sanitation with a formerly disabled or revoked admin, actual-principal permission tests, and operator-command tests without credential disclosure.
- **Predecessors:** None. **Parallel:** GEM-01 and GEM-02; coordinate shared identity/security migrations and grants with GEM-02 before integration.

### M1 — Make the data usable and curatable

#### GEM-04 — Install the pilot catalog safely

**Story:** As a new Workbench installation, I want a sourced starter library so that browsing is useful before any service admin creates an entry.

- **Scope:** Load GEM-01's reviewed content into GEM-02's schema with deterministic IDs on first installation. Existing service-admin edits must survive later deployments and upgrades.
- **Acceptance:** Fresh databases get the exact reviewed pilot set once; an upgrade does not duplicate entries or overwrite a published edit; source records and locality wording match the reviewed package.
- **Evidence:** Fresh-install, rerun, and upgrade tests from the merged base schema, plus a source-to-seed comparison.
- **Predecessors:** GEM-01, GEM-02. **Parallel:** GEM-05 and GEM-07 once their own prerequisites are met; serialize any shared migration edits.

#### GEM-05 — Stage and publish shared changes

**Story:** As a service admin, I want to review and publish a selected batch of gem changes so that the shared library can improve without a code deployment.

- **Scope:** Admin-only draft, review-diff, validation, atomic multi-entry publish/retire, durable publish request identity/outcome, concurrency rejection, and success/failure audit APIs. One admin may review and publish. No historical-content rollback feature.
- **Acceptance:** Drafts are not visible to tenants; a valid batch becomes visible together; one invalid, duplicate, or stale entry prevents all selected changes; the draft survives failure; successful changes and audit commit together. An exact retry after an uncertain response yields the original outcome without publishing twice, while a changed retry conflicts. Tenant sessions and ordinary web writes cannot reach the publish command.
- **Evidence:** Real SQL/HTTP tests for one- and multi-entry batches, races, lost-response retries, audit outcome, source validation, direct unauthorized calls, and denial of broad database write grants.
- **Predecessors:** GEM-02, GEM-03. **Parallel:** GEM-04 and GEM-06; its admin UI follows in GEM-07.

#### GEM-06 — Store tenant additions and field-level overrides

**Story:** As a tenant member, I want to add my own gem entries and replace selected Workbench fields so that my library reflects my knowledge without changing anyone else's reference.

- **Scope:** Tenant-owned additions, archive/restore, sparse inherit/replace/clear overrides, effective-entry projection for read/search/detail, field-attributed sources, duplicate checks, reset, and conflict-safe writes. No inventory-item classification belongs here.
- **Acceptance:** A tenant's changes appear only in that tenant; inherited values and their citations/review dates update together after a shared publication while overridden fields keep only tenant attribution and sources; reset receives the current shared value and sources; explicit clear differs from inherit; cross-tenant IDs and stale writes fail without data leakage. A newly conflicting shared name is flagged rather than merged or deleted. If a shared material-kind change makes a retained clear or replacement invalid, the entry remains visible with its ID and overrides, reports field-level **Needs review** reasons, rejects unrelated edits, and accepts only a valid reconciliation or reset.
- **Evidence:** Tenant-isolated SQL/HTTP tests with two tenants, synthetic shared value-and-source corrections, a non-mineral-to-mineral transition with cleared species, concurrency and archive/restore cases, and actual-principal permission checks.
- **Predecessors:** GEM-02. **Parallel:** GEM-04 and GEM-05; coordinate the effective read contract with GEM-08 and migrations/grants with GEM-05.

### M2 — Deliver both user workflows

#### GEM-07 — Edit the shared library in the service-admin UI

**Story:** As a service admin, I want to stage several entries, inspect their source and field changes, and publish them together so that shared curation is understandable and controlled.

- **Scope:** Dedicated sign-in entry, shared-only list/detail/editor, draft persistence, combined review screen, publish/retire actions, conflict recovery, and clear published/draft states. No tenant-data browsing controls.
- **Acceptance:** An admin completes create, edit, and multi-entry publish from the browser; failed validation or stale data preserves drafts; retiring an entry requires an explanation or redirect. A tenant account cannot open the admin workflow or invoke its APIs directly.
- **Evidence:** Browser journeys in both appearance modes and narrow layout, keyboard and error-state checks, plus direct unauthorized API attempts against the running app.
- **Predecessors:** GEM-05. **Parallel:** GEM-08 and GEM-09 after their respective prerequisites; coordinate navigation and shared UI components before editing them.

#### GEM-08 — Browse the effective library

**Story:** As a tenant member, I want to search and inspect gems by familiar name or classification so that I can use the shared reference and see my tenant's effective values.

- **Scope:** Library navigation, search and material-kind/group filters, result list, detail view, source and layer labels, locality wording, missing-field explanation, loading/empty/error states, and safe external links. Use the effective API; no editing controls belong here.
- **Acceptance:** Pilot examples are findable; tenant additions and overrides appear with correct attribution; an invalid effective entry remains findable by name with a **Needs review** warning and no validated classification claim; another tenant's values never appear; locality statements never read as specimen-origin proof; keyboard/mobile behavior works.
- **Evidence:** Component and browser checks against the integrated effective API, including Tanzanite, tsavorite, opal, pearl, and a tenant override.
- **Predecessors:** GEM-02, GEM-06. **Parallel:** GEM-07; GEM-09 may begin after this screen's shared interaction contract is integrated.

#### GEM-09 — Add and customize entries in the tenant UI

**Story:** As a tenant member, I want to add entries and adjust or reset Workbench fields so that I can maintain a personal reference without altering the curated catalog.

- **Scope:** Add/edit/archive/restore tenant entries; edit inherited, replaced, and cleared fields on shared entries; show Workbench values; single-field and whole-entry reset; source attribution; draft preservation and conflict reconciliation.
- **Acceptance:** A member can add an entry, customize Tanzanite, see that the shared value remains available, reset one field with its current Workbench sources, and recover from failed or concurrent saves. After a shared correction invalidates a retained override, the member sees the **Needs review** reason and both values, can reconcile or reset explicitly, and cannot save unrelated edits while the entry is invalid. Another tenant remains unaffected. The UI distinguishes tenant-authored unsourced claims from sourced Workbench claims.
- **Evidence:** Browser journey across refresh, shared correction and source change, invalid-override reconciliation, conflict, archive/restore, and both appearance modes; direct API isolation tests remain owned by GEM-06.
- **Predecessors:** GEM-06, GEM-08. **Parallel:** GEM-07; coordinate shared UI files and browser fixtures.

### M3 — Prove the integrated release

#### GEM-10 — Accept the end-to-end library

**Story:** As the product owner, I want the shared and tenant workflows verified together so that the reference library can be treated as a supported Workbench capability.

- **Scope:** Reconcile every first-release requirement above with implemented code; exercise operator provisioning, service-admin publish, tenant browse/add/override/reset, another-tenant isolation, backup inclusion, and source accuracy. Update living product and architecture guides and mark this spec implemented only after the checks pass.
- **Acceptance:** All GEM-01–09 stories are integrated; their checks and applicable repository delivery gates pass from current source; the running application is inspected at its local URL; no unauthorized cross-role or cross-tenant path remains; representative source-backed pilot entries and limits are visible. A restore drill proves that pre-restore service-admin sessions stay invalid even when disablement or revocation is rolled back. Record any residual coverage limits honestly.
- **Evidence:** Integrated API/SQL, browser, migration, permission, restore, mutation, full verification, container smoke, and preview evidence as required by `CONTRIBUTING.md`, plus the ready-for-review PR and updated living docs.
- **Predecessors:** GEM-04, GEM-07, GEM-09, and therefore their transitive predecessors. **Parallel:** No independent implementation lane; this story integrates and verifies the delivered range.

### Parallel delivery order

| Wave | Work that may proceed together | Integration boundary |
| --- | --- | --- |
| A | GEM-01, GEM-02, GEM-03 | Agree seed fields, read contract, and separate identity/security boundaries before dependent stories branch. |
| B | GEM-04, GEM-05, GEM-06 after their stated predecessors | Serialize common schema/grant changes; publish, seed, and effective-entry tests use the same integrated shared contract. |
| C | GEM-07 and GEM-08 after their stated predecessors; GEM-09 after GEM-06 and GEM-08 | Coordinate navigation and test fixtures; UI work may prototype against approved contracts, but mocked endpoints do not complete prerequisites. |
| D | GEM-10 | Integrate all prior PRs, then run full current-source acceptance and update the spec status. |

## Decisions reserved for later work

Inventory linking, reports, property population, exhaustive coverage, public contribution/review, multilingual names, and bulk import are separate delivery decisions. The first release does not claim a complete GIA database or use GIA branding. Before expanding the catalog substantially, measure editorial review cost, source availability, and the accuracy of searches against real collector terminology.
