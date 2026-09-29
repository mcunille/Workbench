# Gemological reference library

**Status: Proposed** — design reviewed with the owner on 2026-09-29; written-spec review and implementation planning remain pending.

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
2. Ship a small, reviewed Workbench catalog spanning important structural cases: corundum/ruby and sapphire; beryl/emerald and aquamarine; garnet/grossular/tsavorite; zoisite/tanzanite; and opal, amber, pearl, and lapis lazuli. Each released entry has source links and a review date. This is a representative starting set, not a claim of exhaustive gem coverage.
3. Allow signed-in tenant members to add, edit, archive, and restore tenant-owned entries, and to override or reset fields of Workbench entries for their tenant. Existing tenant authorization and anti-forgery patterns apply. The tenant boundary is enforced by the server and SQL, not by filtering in the browser alone.
4. Make the source layer visible: **Workbench reference**, **tenant entry**, or **Workbench reference customized for this tenant**. Display source links and last review date; identify tenant-written assertions separately from Workbench assertions.
5. Keep the library independent of collection items, purchase lines, lab reports, exports, and pricing in this release. Existing workflows and item names remain untouched.

## Reference-entry model

Every entry has a stable ID, `materialKind`, preferred `commonName`, zero or more search aliases, an optional short description, and source assertions. Initial material kinds are **mineral**, **mineraloid**, **organic**, and **rock/aggregate**. These are reference classifications, not statements that an individual object is natural. Natural versus laboratory-grown, imitation, and assembled material need a separate item or material-design treatment before such claims are introduced.

For mineral entries, `species` is required; `group` and `variety` are optional. For non-mineral entries, the applicable taxonomy fields may be absent, and the detail view explains the material kind. `commonName` is always present and may equal the species or variety name. Aliases are search terms, not alternate taxonomic identities. A classification entry describes one recognizable gem identity; names that designate a genuinely different variety get separate entries.

An optional **notable-locality assertion** records a place, the exact scope of the claim (for example, “only known commercial source”), a citation, and review date. It can be added only when the source supports that scope. The claim can change as discoveries change and never pre-fills or proves the origin of an inventory item. General source regions are future editorial content, separate from the exceptional single-locality assertion.

Sources belong to assertions rather than to an unqualified record-wide bibliography: classification and locality claims can cite different material. A Workbench entry must cite its substantive claims. Tenant entries may remain unsourced for personal reference, visibly marked as such; a tenant locality assertion or future numerical property requires a source URL or identifiable publication. A source record includes title, publisher, URL or citation, date accessed/reviewed, and the fields it supports. An overridden field inherits neither the Workbench field's citation nor its review date; it has tenant attribution and any tenant-supplied source. External links are inert content, validated to safe schemes, and displayed without embedding remote media.

## Shared catalog, tenant additions, and tenant overrides

Workbench ships curated entries with stable IDs and controlled revisions. A tenant cannot mutate those rows. Tenant entries have tenant-qualified IDs and are visible only in that tenant. A tenant override refers to a curated entry ID and stores only deliberately overridden fields. For each field, the state is **inherit**, **replace**, or, where optional, **clear**. A blank text input does not silently mean “inherit.” Alias lists and source assertions are replaced as a whole when overridden; untouched lists inherit.

The server resolves a tenant's effective entry by applying that tenant's overrides to the current curated revision. An upstream correction reaches inherited fields; replacement and explicit-clear fields remain tenant choices. Search, filters, detail, and edits use that same effective projection. The UI marks changed fields, shows their Workbench values, and allows a single-field reset or whole-entry reset. Tenant edits carry a row version so concurrent changes require review. A curated update does not silently delete a tenant override or change its source attribution.

Normalize identity text for duplicate detection without changing display spelling. Identity comparison uses material kind plus applicable group, species, variety, and common name; aliases do not define identity. Reject creation of a tenant entry that exactly matches another visible entry's effective identity, and reject an override that creates an exact collision at edit time. A later Workbench catalog revision may create a collision with existing tenant data; retain both stable IDs, flag the conflict for review, and never silently merge or erase either entry. A tenant can resolve it by editing, resetting, or archiving its own data. An archived tenant entry disappears from ordinary search but remains restorable with its ID and revision history. Curated entries are retired by a catalog revision with a redirect or explanation, never physically removed during an update.

The owner approved shared reference plus tenant additions and field-level overrides. The initial write permission follows the current collection model for signed-in tenant members; a separate role policy can narrow this later if real collaboration needs warrant it.

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

## Compatibility, release, and recovery

The first release adds a new reference domain and navigation route. It does not rewrite existing items, names, exports, or purchasing records. Seed the curated catalog with deterministic IDs and an explicit revision; updates modify that catalog through reviewed releases, preserving ID continuity. Database changes must follow the migration runbook and verify both fresh creation and upgrade from the merged base. Export/import of tenant entries and overrides is deferred; the existing backup/recovery contract must include their tables. Tenant archive/restore and row versions provide routine correction; a deployment rollback must not delete tenant-authored data. A curated revision that cannot be safely applied fails the release step rather than partially updating entries.

## Verification and success criteria

- Source review checks every seeded classification and locality assertion against linked primary references, spelling, material kind, and date. Review the rendered attribution and avoid copied prose/media.
- API/SQL tests cover shared visibility, tenant isolation, additions, per-field inheritance/replacement/clear/reset, upstream revision behavior, duplicate conflict handling, archive/restore, concurrent edits, and inaccessible IDs. Component/browser tests cover search and source labels, source-link safety, draft preservation, keyboard/mobile use, and the Tanzanite/tsavorite locality distinction. Follow repository TDD, Gherkin-comment, mutation, and delivery gates when implementation is authorized.
- A user can find each pilot example by common name and at least one relevant classification term; see why a field is absent on non-mineral entries; add and later correct a tenant entry; override Tanzanite for their tenant; and reset one field to receive the current Workbench value. Another tenant sees no change.
- A later property phase is successful only when a reader can tell a sourced type range from an observation on a particular stone and can see the limits of the published value.

## Decisions reserved for later work

Inventory linking, reports, property population, exhaustive coverage, public contribution/review, multilingual names, and bulk import are separate delivery decisions. The first release does not claim a complete GIA database or use GIA branding. Before expanding the catalog substantially, measure editorial review cost, source availability, and the accuracy of searches against real collector terminology.
