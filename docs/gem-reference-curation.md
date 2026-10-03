# Shared gem reference curation

Service admins maintain the shared gem catalog at `/service-admin/sign-in`. The public tenant
sign-in page links to this dedicated entry. An operator must first provision the account using
the [service-admin identity procedure](operations/database-principals.md#service-admin-identity-maintenance).
There is no public registration or tenant-admin invitation path. Tenant and service-admin
accounts use separate sign-ins; this console contains no tenant-data browsing controls.

## Save a draft

Open **New draft**, or open a published entry and choose **Edit entry**. The library's
**Drafts** view lists saved work separately from the **Published catalog**. A published detail
also links to existing drafts for that entry; opening an existing draft avoids duplicating work.

Enter the common name, material kind, applicable taxonomy, description and aliases. Mineral
entries require a species. Add sources for each populated field or claim. Each source records
its supported field, title, publisher, URL or publication citation, review date and, when
applicable, accessed date. A notable locality needs its own supporting locality source and
scope; its review date follows the selected source. Citations remain content, with safe HTTP
or HTTPS links in published details; the console does not embed external media.

**Save draft** persists work without changing the published catalog. Incomplete drafts can be
saved. The server returns issues that must be corrected before publication; save corrections
again to recheck them. Unsaved edits trigger the discard confirmation when leaving the editor.

## Review and publish

Select up to 50 saved drafts, then choose **Review N drafts**. The combined review shows each
entry's current before and after fields and sources. One service admin can confirm and publish
their own selected batch. A second approver is not required.

Publishing applies the selected valid changes together. Validation or stale versions block
publication and preserve the drafts for correction. Editing a selected draft updates its
version and requires a fresh review and confirmation. Successful publication returns links
to the current shared entries and removes the published drafts.

To retire an entry, edit it, choose **Retire entry**, and provide a retirement explanation or a
replacement shared-entry ID. Save, review and publish the retirement. Retired entries leave
the active browse catalog but their stable detail URLs retain the explanation and replacement
link, when supplied.

## Recover changes and uncertain outcomes

If another admin changes a saved draft or publishes a newer entry, the editor retains your
local work and displays the current saved and published content beside it. Saving stays
blocked until you explicitly choose **Keep my edits with current versions** or **Use current
saved draft**. The latter restores the saved draft's original published baseline; a stale
baseline may still require an explicit rebase before saving.

If your admin session ends, sign in with the same account above the retained editor. Current
versions are refreshed before saving resumes. Signing in as a different account clears that
editor. Unsaved editorial content is held in memory and does not survive a browser reload.

If a publication response is lost, its original request identity and selected draft versions
remain in account-scoped session storage. **Check publication outcome** retrieves the durable
result; **Retry identical publication** checks the result first and resends that same request
only when no outcome exists. Reloading the review also checks the pending outcome. Resolve
it before selecting another batch. A rejected outcome requires repair, fresh review and new
confirmation; it is not an instruction to repeat the rejected request. Use the visible retry
action after a read or version-refresh failure.

## Delivered scope and evidence

GEM-07 adds the service-admin browser workflow over the existing GEM-03 identity and GEM-05
curation APIs. Its focused acceptance files are
`tests/Workbench.BrowserTests/gem-curation.spec.ts` and `gem-curation.ui.spec.ts`: live SQL-backed
create/save/reload/edit/batch publication/retirement, two-admin draft and published-base
reconciliation, retained-editor reauthentication after an ended session, and direct cross-role
denial. Intercepted keyboard, editor validation, dirty navigation and combined review run in
light and dark appearances at 390px; load-retry and lost-response recovery run in the default
appearance and viewport. These synthetic claims do not
change the distributed four-mineral sample.

Tenant additions and overrides, the tenant library screens, inventory linking, numerical
properties, bulk import and an audit-history UI remain outside GEM-07. The complete
[reference-library release](specs/2026-09-29-gemological-reference-library.md) remains incomplete.
