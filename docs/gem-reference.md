# Maintain the gem reference

Signed-in tenant members can open **Gem reference** in the workspace navigation. Search by
common name, alias or classification, optionally select a material kind or enter an exact group,
then choose **Search**. **Clear filters** starts again. **Load more entries** continues a result
set without discarding earlier rows; a failed load offers **Retry**.

Open an entry to read its effective classification, names, description, reference locality and
sources. Returning to the list preserves the search, filters, loaded rows and selected result
within the current signed-in application. Searches remain in private memory rather than URLs;
reloading or signing out ends that traversal. Detail links include both origin and ID, so a tenant
entry and a Workbench entry with the same ID remain distinct.

## Tenant entries

Choose **Add entry** to create a reference visible only to your tenant. Enter its common name,
material kind and applicable classification, add optional aliases and notes, then choose
**Save changes**. Mineral entries require a species. Sources are optional for personal reference
claims except notable locality, which requires a citation supporting its exact scope. A source
can use a safe HTTP(S) URL or an identifiable publication citation, with publisher and review date.
Descriptions and publication citations use the API's single-line text format; the editor converts
pasted or typed line breaks to spaces and explains this beside those controls.

Open a tenant entry and choose **Edit entry** to update it. **Archive tenant entry** hides it from
ordinary searches after confirmation. To find it again, select **Include archived tenant entries**
in the library and choose **Search**. This inclusive view contains active and archived tenant entries
alongside shared references. Open the archived entry and choose **Restore tenant entry**; restore
must satisfy current validation and duplicate-identity rules.

## Customize and reset shared fields

Open a Workbench reference and choose **Customize for this tenant**. Every field shows its current
Workbench value and supporting source information beside your tenant's choice:

- **Use Workbench value** inherits the current value, citations and review dates.
- **Use tenant value** replaces that field with your tenant's value and optional tenant sources.
- **Clear for this tenant**, offered for clearable fields, explicitly removes the assertion.
  Clearing a species can still be invalid when the material kind requires one.

An empty text box never silently selects inheritance. Aliases replace as a complete list, entered
one per line; an empty replacement list means no aliases. Replaced fields do not keep citations for
displaced Workbench claims. Add sources beside the field they support. A notable-locality assertion
selects its own supporting tenant source and uses that source's review date.

**Reset Species to Workbench**, and the equivalent action on another changed field, stages that
field's reset in the form. Other unsaved choices remain intact. Choose **Save changes** to apply
the complete draft; the reset field then receives current Workbench values and sources together.
**Reset all fields to Workbench** is a separate confirmed action that immediately removes every
tenant customization and discards unsaved form changes when successful. A rejected reset preserves
your draft so you can repair any remaining conflict.

## Failed saves and reconciliation

Validation failures preserve your values and citations and show field-specific reasons. If another
member or shared publication changed the entry, compare **Your draft** with the **Current saved
value** and sources. **Keep my draft with current versions** explicitly adopts those versions;
review the choices and save again. **Use current entry** discards the draft in favor of saved data.
Neither comparison choice writes automatically, and a further competing save requires another review.

If a request's response is lost, the request may already have committed. **Check current entry**
reads the original entry ID before any retry. For creation, the ID stays the same. If the entry exists,
review its saved values before adopting it or keeping the draft for an explicit update. If a new
entry is confirmed absent, **Save changes** deliberately retries with that same ID.

Saved values survive refresh. Unsaved drafts live only in the current page; navigation prompts
before discarding them, and reload or session loss can discard them. They are not durable saved
drafts. Signing out or changing accounts clears private editor state.

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
presented as validated. Open its tenant editor to compare current Workbench values with retained
tenant choices. Explicitly repair or reset conflicting fields before saving; unrelated changes
cannot be saved while the complete entry remains invalid. Retired entries retain their identity and show an explanation
or a link to a replacement when supplied.

A **Reference locality** describes the material type and its cited scope, including exceptional
quality where asserted. It never establishes the origin of an individual specimen. This release
does not provide numerical identification ranges, specimen measurements, inventory classification
or exhaustive gem coverage. The reviewed starter sample contains Diamond, Sapphire, Emerald and
Ruby; broader examples in tests are synthetic and are not distributed reference content.

Shared publications and tenant changes become available through subsequent API reads. The current
in-memory list preserves its loaded traversal until another search, reload or successful tenant
mutation; successful mutations refresh results while preserving search filters. Opening a detail
requests its current effective values. An expired session returns to the normal tenant sign-in
flow. A missing or inaccessible detail reports not found without disclosing another tenant's data.
