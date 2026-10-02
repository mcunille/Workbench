# Private gem reference content

GEM-01 supplies an owner-held sample of Diamond, Sapphire, Emerald, and Ruby. The owner
reduced the original ten-entry pilot to these four minerals on 2026-10-02. Non-mineral,
group, alias, and exceptional-locality examples remain future editorial expansion; numerical
reference properties remain outside this release. GEM-02's validation and storage support
broader cases independently of this small sample.

## Package location and ownership

Keep the curated package and its claim-to-source checklist in the ignored
`.private/gemological-reference/` directory, or in a separately managed private location.
The public repository contains the contracts and this handoff procedure, not the curated
records, source dossier, or content-review evidence. Do not attach those files to a public
pull request, copy them into test fixtures, or include them in CI artifacts or logs.

An ignored directory is a local delivery location, not a backup or an access-control mechanism.
The owner must preserve a private copy before archiving or removing this worktree. A fresh
public clone has no sample package. Git ignore does not prevent deliberate force-addition.

Keeping the package out of Git does not make installed shared references confidential from
authenticated application users. Shared references follow GEM-02's read authorization.

## Content contract

`sample.json` is a UTF-8 JSON array of the merged
[`GemReferenceContent`](../src/Workbench.Server/Gemology/GemReferenceContent.cs) records,
using camel-case property names. Include all constructor fields, using explicit `null` for
absent optional fields and empty arrays for unselected aliases. IDs for entries and source
assertions are assigned once and retained across handoffs; do not regenerate IDs on import.

Each source record supports one populated field through its `field` value. A single publication
may support several fields through separate assertion records with distinct IDs. Use primary
URLs, publication titles, publishers, access dates, and actual review dates. Store concise
original descriptions, not copied prose or media. Do not invent search aliases to fill an
empty list. An absent optional group means the sample does not assert that field, not that the
mineral cannot belong to any broader mineralogical grouping.

The private `claim-to-source-checklist.md` maps each populated field to the primary source and
records the editorial reasoning for absent fields, source locators, review date, scope limits,
and review outcome. Scientific source review and machine validation are distinct checks:
valid JSON and a well-formed citation do not establish that a source supports a claim.

## Validation and GEM-04 handoff

Deserialize the package into `GemReferenceContent` using camel-case names and strict unknown-field
and missing-constructor-field rejection. Validate each record with
[`GemReferenceInput.Normalize` and `Validate`](../src/Workbench.Server/Gemology/GemReferenceInput.cs)
using the current date and the package's redirect map. Check entry and source IDs for
package-wide uniqueness and effective identities with `GemReferenceInput.IdentityKey`.
Reject an empty package and any unknown property, including premature numerical properties.
Report counts, entry positions, and validation field names rather than private content or raw
deserialization exceptions. Validation must not write to the database.

The owner supplies the reviewed package directly to GEM-04's installation work. GEM-04 must
define the explicit private-package input and distribution procedure; no loader, database seed,
deployment attachment, or automatic download is introduced by GEM-01. Installation must preserve
these stable IDs and source records, seed once, and retain later service-admin changes. Public
CI uses synthetic content and cannot certify the existence or source accuracy of an owner-held
package. Do not silently recreate or substitute the private sample when the supplied file is
missing.

M0 is complete for the revised four-mineral sample when that package has passed content review
and current-contract validation and GEM-02 and GEM-03 are merged. This does not mark the complete
reference-library specification or M1 implemented.

The 2026-10-02 local handoff passed merged GEM-02 validation and independent AI-assisted
primary-source review for the revised sample. It has four entries and nineteen field-level
source assertions. The detailed checklist and verification evidence remain with the private
package. No installation or rendered-attribution verification is claimed; those belong to
later stories. No new validation logic was introduced, so no new behavior tests or mutation
run were needed for this content-and-documentation change.
