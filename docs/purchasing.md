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
without rounding. Reference-price entry starts with a `0.00` placeholder: digits fill from the right
(`1` → `0.01`, `12` → `0.12`, `123` → `1.23`). An untouched or cleared field remains Unknown.
Choose **Use extra precision** to type a decimal amount directly; saved prices with meaningful
third or fourth decimal digits automatically use this mode. Remove those digits before returning
to two-decimal entry; switching modes never rounds. Pasted decimal amounts retain their value.
**Clear all reference prices** asks for confirmation and shows the affected count;
Cancel or Escape keeps the prices. Confirming clears them locally, and **Save draft** persists the change.
The Back/Save toolbar is transparent at the top and gains a background when pinned while scrolling.
The draft badge sits in Order details, with the saved time above the form and fields
grouped into order details, shopping list, and notes and sources.

Drafts allow 100 entries and 20 order-level source links. Links must use HTTP or HTTPS and cannot
contain embedded login credentials. Large drafts can reach the overall size limit before individual
field limits; shorten the text or remove entries if the save reports that limit.

Saving or removing a draft entry changes only this shopping list. It creates no collection item,
acquisition, invoice, payment obligation or accounting entry. Quantities and unit pricing,
commitments, attachments, receiving and payments are
separate increments. There is no order export workflow in this release.

## Suppliers, platforms and purchase references

The first save assigns a permanent business reference such as **PO-000001**. A reference identifies
the draft; it does not mean the purchase was placed. Numbers increase within each business, do not
reset each year, and are never reused after deletion. Supplier order reference is a separate optional
field for the supplier's own identifier; it is not an invoice number.

Use **Manage suppliers** from Purchase orders to create and maintain reusable suppliers, or enter
one-off supplier details directly on a draft. Contact name, email, phone, website and postal address
are optional. Duplicate supplier names are allowed. Archiving a supplier removes it from default
selection while keeping its saved orders and details; it can be reactivated later.

Selecting a supplier copies their details onto this draft. Editing the directory never rewrites
saved purchases. **Use current supplier details** previews a refresh for this draft; confirm the
replacement and then save. **Keep details as one-off** removes the directory link while retaining
the copied details. Changing supplier asks what to do with an existing supplier order reference.
An inline supplier creation saves the directory record separately: discarding or failing to save
the draft does not remove that supplier.

**Platform** records where this purchase happens, for example Retail, Instagram or Gem Rock Auctions.
It accepts custom text and may be blank on an incomplete draft. Orders for the same supplier can use
different platforms. Selecting or refreshing a supplier never overwrites the platform, and changing
platform leaves the supplier details and references intact. Keep storefront, listing or conversation
URLs in the order's source links. Recording a platform does not send messages or connect an account.

**Search purchase orders** finds saved drafts by Workbench reference, supplier order reference,
supplier snapshot name or title across the business. Platform is displayed but is not searched in
this release. Clear the search to return to normal browsing. Supplier search matches names and can
include archived records. Both lists retain loaded results when a page fails.

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
The [PO-02 design](specs/2026-09-11-po-02-supplier-identity-and-references.md) extends those contracts
with supplier snapshots, per-order platforms and permanent references. After an upgrade, an older
client must reload before sending a new save; already successful old requests can still be resolved.

## Delete an unwanted draft

Open a saved draft and choose **Delete draft** beneath the form. The confirmation names the saved
draft and warns that local unsaved edits will be discarded. Cancel or Escape leaves everything
unchanged. Confirming removes the draft from purchase orders and returns to the list; it cannot
be restored through the application. If another member changed it, review the current version
before requesting deletion again. An uncertain deletion offers **Check and retry deletion** with
the original request; do not create a new request to resolve a lost response.
