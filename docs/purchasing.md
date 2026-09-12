# Purchase order drafts

Purchase orders lets members of a business plan a supplier purchase and return to it later.
Use **New draft**, record what you know, and select **Save draft**. An empty draft is valid;
its display name is Untitled draft until you give it a title. Saved drafts remain available after
signing out and signing back in. All records belong to the signed-in business.

## Build a shopping list

Keep an optional title, supplier name, notes and source links together. Add shopping-list entries
with a description, notes, source link and reference price. Use notes for quantities, pricing bases,
or questions for the supplier. These are planning entries, not committed order lines.

A blank price means **Unknown**, while an explicitly entered zero remains zero. Enter one currency
for any prices on the draft. Prices accept up to four decimal places and remain reference amounts;
the application does not calculate a total. To change an existing currency, clear the prices and
save that change before entering amounts in the new currency. No conversion or relabeling is inferred.

Saved prices display at least two decimal places, retaining meaningful third and fourth digits
without rounding. **Clear all reference prices** asks for confirmation and shows the affected count;
Cancel or Escape keeps the prices. Confirming clears them locally, and **Save draft** persists the change.
The editor keeps a transparent Back/Save toolbar above the draft badge and saved time, with fields
grouped into order details, shopping list, and notes and sources.

Drafts allow 100 entries and 20 order-level source links. Links must use HTTP or HTTPS and cannot
contain embedded login credentials. Large drafts can reach the overall size limit before individual
field limits; shorten the text or remove entries if the save reports that limit.

Saving or removing a draft entry changes only this shopping list. It creates no collection item,
acquisition, invoice, payment obligation or accounting entry. Supplier directories, formal PO
references, quantities and unit pricing, commitments, attachments, receiving and payments are
separate increments. There is no order deletion or export workflow in this release.

## Resume and recover work

Purchase orders lists saved drafts with the most recently saved first. **Load more** retrieves the
next page; **Refresh** starts again. This is a live list: another user's edits can move a draft above
the current page, so refresh to see new or recently changed records. A failed page load keeps the
already loaded drafts available for retry.

Saves explicitly confirm whether the request succeeded, then load current details. If a save's
outcome is uncertain, **Check and retry** uses the original request so the action is not duplicated.
If the save succeeded but loading details failed, retry only the load. Do not start another draft to
recover a confirmed save. A newer saved version requires review before editing continues.

Competing edits show current saved content alongside your changes. **Use saved version** discards
your local edits. **Continue with my changes** returns them to the editor after reviewing the latest
version; reconcile them and explicitly save. No automatic merge or overwrite occurs.

Validation errors and failed saves retain your input in the open session. Leaving unsaved work
asks whether to keep editing or discard changes; discarding never deletes the saved draft.
Only saved content survives reload or browser closure. Authentication loss clears private in-memory
state, and leaving an uncertain save loses the browser's retry request even if the server saved it.

The [PO-01 design](specs/2026-09-11-po-01-draft-supplier-orders.md) specifies limits, retry receipts,
versions, and verification requirements. Existing collection and acquisition workflows remain
independent of purchasing.
