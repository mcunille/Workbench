# Gem reference sample and private content

GEM-01 supplies a reviewed sample of Diamond, Sapphire, Emerald, and Ruby. The owner reduced
the original ten-entry pilot to these four minerals on 2026-10-02 and subsequently authorized
this sample for inclusion in the public repository. Non-mineral, group, alias, and locality
examples remain future editorial expansion; numerical reference properties remain outside this
release. GEM-02 supports broader cases independently of this sample.

## Package location and ownership

The committed [sample package](../data/gem-reference/sample.json) and
[claim-to-source checklist](../data/gem-reference/claim-to-source-checklist.md) are the canonical
GEM-04 sample input. A fresh clone includes both files. IDs, source assertions, and review dates
are unchanged from the reviewed local package.

Future owner-curated packages can remain in the ignored `.private/gemological-reference/`
directory or a separately managed private location. Permission to publish this sample does not
publish those packages. Do not copy private content into public fixtures, pull requests, CI
artifacts, or logs. An ignored directory is neither a backup nor access control: preserve a
private copy before removing its worktree, and do not force-add its files. Installed shared
references follow GEM-02's authenticated read authorization.

## Content contract

`sample.json` is a UTF-8 JSON array of
[`GemReferenceContent`](../src/Workbench.Server/Gemology/GemReferenceContent.cs) records with
camel-case property names. Include all constructor fields, explicit `null` for absent optional
fields, and empty arrays for unselected aliases. Assign entry/source IDs once and retain them
across handoffs; do not regenerate IDs on import.

Each source record supports one populated field through its `field` value. One publication may
support several fields through separate assertions with distinct IDs. Use primary URLs, titles,
publishers, access dates, and actual review dates. Store concise original descriptions, not
copied prose or media. Do not invent aliases to fill an empty list. An absent group means the
sample does not assert that field, not that a broader mineralogical grouping is impossible.

The checklist maps every populated field to its source and records absent-field reasoning,
source locators, review date, scope limits, and review outcome. Scientific review and machine
validation are distinct: valid JSON and a citation do not establish support for a claim.

## Validation and GEM-04 handoff

Deserialize into `GemReferenceContent` with camel-case names and strict unknown-field and
missing-constructor-field rejection. Validate each record with
[`GemReferenceInput.Normalize` and `Validate`](../src/Workbench.Server/Gemology/GemReferenceInput.cs)
using the current date and the package's redirect map. Check entry/source IDs for package-wide
uniqueness and identities with `GemReferenceInput.IdentityKey`. Reject an empty package and
unknown properties, including premature numerical properties. Validation must not write SQL.
For private packages, report counts, entry positions, and field names rather than private
content or raw deserialization exceptions.

GEM-04 installs the committed sample and defines distribution and installation. If it supports
additional private content, it must define an explicit package input without embedding private
records in public source or artifacts. GEM-01 introduces no loader, database seed, automatic
download, or runtime change. Installation must preserve stable IDs and sources, seed once, and
retain later service-admin changes. CI can validate the committed sample's structure; source
accuracy still requires editorial review. Broader structural tests use synthetic fixtures.

M0 is complete for the revised four-mineral sample with content review, contract validation,
and merged GEM-02/GEM-03. This does not mark M1 or the entire library implemented.

The 2026-10-02 review checked four entries and nineteen field-level source assertions against
merged GEM-02 validation and an independent AI-assisted primary-source review. Publishing the
same package does not change its claims or constitute a new source review. No installation or
rendered-attribution verification is claimed. No new validation logic was introduced, so no new
behavior tests or mutation run were needed for this content-and-documentation change.
