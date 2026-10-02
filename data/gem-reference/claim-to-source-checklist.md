# GEM-01 sample claim review

Accessed and editorially reviewed: **2026-10-02**. Owner-selected scope: Diamond, Sapphire,
Emerald, Ruby. The owner authorized this four-entry sample and checklist for the public repository. No source prose, photographs,
numerical property tables, or specimen data are reproduced.

## Claim-to-source checklist

| Entry | Asserted field | Decision | Primary source / locator | Review |
| --- | --- | --- | --- | --- |
| Diamond | materialKind | mineral | [GIA Diamond Description](https://www.gia.edu/diamond-description), opening mineral/chemical-composition discussion | 2026-10-02 |
| Diamond | commonName | Diamond | Same page title and opening discussion | 2026-10-02 |
| Diamond | species | Diamond | Same page distinguishes diamond from graphite by crystal structure | 2026-10-02 |
| Diamond | description | Original summary: mineral form of carbon | Same opening composition and structure discussion | 2026-10-02 |
| Sapphire | materialKind | mineral | [GIA Sapphire Description](https://www.gia.edu/sapphire-description), opening definition of mineral species corundum | 2026-10-02 |
| Sapphire | commonName | Sapphire | Same page title and opening definition | 2026-10-02 |
| Sapphire | species | Corundum | Same opening definition | 2026-10-02 |
| Sapphire | variety | Sapphire | Opening definition and caption on ruby/sapphire varieties in [GIA Ruby Description](https://www.gia.edu/ruby-description); package citation uses the sapphire definition | 2026-10-02 |
| Sapphire | description | Original summary includes non-blue sapphires and excludes corundum classified as ruby | Sapphire opening definition and fancy-sapphire discussion | 2026-10-02 |
| Emerald | materialKind | mineral | [GIA Emerald Description](https://www.gia.edu/emerald-description), opening mineral-species definition | 2026-10-02 |
| Emerald | commonName | Emerald | Same page title and opening definition | 2026-10-02 |
| Emerald | species | Beryl | Same opening definition | 2026-10-02 |
| Emerald | variety | Emerald | Same opening definition | 2026-10-02 |
| Emerald | description | Original summary: green to bluish green gem variety of beryl | Same opening definition; following paragraphs distinguish emerald from lighter green beryl | 2026-10-02 |
| Ruby | materialKind | mineral | [GIA Ruby Description](https://www.gia.edu/ruby-description), opening mineral-species definition | 2026-10-02 |
| Ruby | commonName | Ruby | Same page title and opening definition | 2026-10-02 |
| Ruby | species | Corundum | Same opening definition | 2026-10-02 |
| Ruby | variety | Ruby | Same opening definition | 2026-10-02 |
| Ruby | description | Original summary: red gem variety of corundum | Opening definition and following discussion of red coloration | 2026-10-02 |

Each row corresponds to a separate source assertion in the JSON. An assertion's `field` is its
claim binding. Reusing a source URL across claims does not turn it into a record-wide bibliography.
The same source can support the classification and an independently worded summary.

## Absent fields and interpretation

- `group` is null for all four: the sample cites species/variety definitions and makes no broader
  mineral-group claim. This is not a statement that a group is scientifically impossible.
- Diamond `variety` is null: this entry names the species, not a selected color variety.
- Sapphire is the broad sapphire identity, not a synonym for blue sapphire. Color-specific
  identities are not invented as aliases or separate entries in this sample.
- All alias lists are empty. No researched alternate names are needed for these familiar names;
  species terms are already searchable through GEM-02's taxonomy search.
- All locality assertions are null. No geographic origin is inferred. The original
  Tanzanite/Tsavorite contrast is deferred with those entries, not generalized to this sample.
- All entries are active, with no retirement explanation or redirect.
- No RI, SG, hardness, optical character, natural/laboratory-grown assertion, or numerical
  identification threshold is included. Reference identity does not classify an inventory item.

## Handoff and limits

The sample uses merged `GemReferenceContent` constructor fields and claim bindings exactly.
IDs were assigned deterministically once for this package and must be preserved, not re-derived
after renaming or reordering. The four descriptions are original factual summaries.

Reviewing source meaning is an editorial step; validation does not prove source accuracy or
GIA endorsement. This review is AI-assisted, not a credentialed gemological certification.
Independent internal review on 2026-10-02 retrieved all four primary pages and found support
for the classification and description assertions. Diamond's species identity is an editorial
inference from its mineral identity, composition, and crystal-structure discussion rather than
an explicit use of the word “species” on that page. The remaining species/variety definitions
are explicit. No unsupported sample content claims were identified.
M0 completion applies to the owner-revised mineral sample,
not the original ten-entry structural set. GEM-04 owns database installation and sample
distribution. The committed package is the canonical GEM-04 sample input; future owner-curated packages may remain private.

## Verification evidence

- Source contract: merged main `7301d7d` (includes GEM-02 and GEM-03).
- Current-source validation on 2026-10-02 passed: 4 entries and 19 field-level assertions,
  no duplicate package IDs or identities. The ignored verification harness links the two
  current GEM-02 content/input files; it does not use a stale server binary or write SQL.
- Validation used `GemReferenceInput.Normalize`, `Validate`, and `IdentityKey` against the package, including package-wide ID and identity checks. See the [handoff procedure](../../docs/gem-reference-content.md#validation-and-gem-04-handoff).
- The public package is byte-identical to the reviewed local sample; IDs and source records
  are unchanged. No database installation or app-rendering verification occurred in GEM-01.
