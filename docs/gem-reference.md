# Browse the gem reference

Signed-in tenant members can open **Gem reference** in the workspace navigation. Search by
common name, alias or classification, optionally select a material kind or enter an exact group,
then choose **Search**. **Clear filters** starts again. **Load more entries** continues a result
set without discarding earlier rows; a failed load offers **Retry**.

Open an entry to read its effective classification, names, description, reference locality and
sources. Returning to the list preserves the search, filters, loaded rows and selected result
within the current signed-in application. Searches remain in private memory rather than URLs;
reloading or signing out ends that traversal. Detail links include both origin and ID, so a tenant
entry and a Workbench entry with the same ID remain distinct.

## Layers and sources

- **Workbench reference** is the shared curated entry.
- **Workbench reference · customized** includes your tenant's field-level changes. Untouched
  fields inherit Workbench values and their sources; replaced fields show tenant attribution.
- **Tenant entry** belongs to your tenant. Other tenants do not see it.

Each displayed field identifies its attribution. **Tenant-authored · no sources supplied** means
the tenant claim has no supplied citation; it does not inherit a Workbench citation for a
different value. **Cleared by your tenant** is an explicit clear, distinct from an optional
Workbench field with no assertion recorded. Missing fields are unasserted, not a measured zero or
proof of absence. Non-mineral entries do not need an invented mineral species.

**Supporting sources** links to the field's citations, publisher and review/access dates. Safe
HTTP(S) source links open separately; a citation with an unsupported URL remains readable text.
The source review date records review of that assertion, not a guarantee that a source has not
changed since then.

## Scientific and workflow limits

**Needs review** means a retained effective value or identity needs reconciliation. The entry
remains findable by name with its reasons, while classification values are withheld rather than
presented as validated. This browsing screen has no editing or reconciliation controls; tenant
editing is a separate release story. Retired entries retain their identity and show an explanation
or a link to a replacement when supplied.

A **Reference locality** describes the material type and its cited scope, including exceptional
quality where asserted. It never establishes the origin of an individual specimen. This release
does not provide numerical identification ranges, specimen measurements, inventory classification
or exhaustive gem coverage. The reviewed starter sample contains Diamond, Sapphire, Emerald and
Ruby; broader examples in tests are synthetic and are not distributed reference content.

Shared publications and tenant changes become available through subsequent API reads. The current
in-memory list preserves its loaded traversal until another search or reload; opening a detail
requests its current effective values. An expired session returns to the normal tenant sign-in
flow. A missing or inaccessible detail reports not found without disclosing another tenant's data.
