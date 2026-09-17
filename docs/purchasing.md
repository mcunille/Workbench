# Purchase order drafts

The purchasing API is beta, as are all unreleased Workbench APIs. The current application uses
`/api/beta/purchase-order-drafts` and `/api/beta/suppliers`; older API paths cannot create or
change drafts. Exact successful retries remain recoverable through receipt-only adapters.
See [API lifecycle](api-lifecycle.md) for compatibility and rollout rules. If Workbench asks
you to reload after an update, copy unsaved changes first and preserve any uncertain save's
request identity rather than starting a duplicate save.

Purchase orders lets members of a business plan a supplier purchase and return to it later.
Use **New draft**, record what you know, and select **Save draft**. An empty draft is valid;
its display name is Untitled draft until you give it a title. Saved drafts remain available after
signing out and signing back in. All records belong to the signed-in business.

## Itemize a purchase

Keep an optional title, supplier details, notes and source links together. Under **Order lines**, add a description and one quantity and unit matching how the supplier charges. Optional details include supplier SKU, item type, notes and a source link. Incomplete lines and empty drafts remain saveable; they do not commit a purchase.

Choose **Per unit** or **Total line** pricing. Per unit multiplies quantity by unit price: 12.5 carats at USD 20 produces USD 250.00. Total line records the supplier's amount directly and does not require quantity or unit to calculate. There is no separate ordered count, pricing unit, batch denominator or priced quantity. A quote of USD 8 per 100 pieces can be entered as USD 0.08 per piece, or as the total for the line. Workbench does not convert between units; ounce and troy ounce are distinct.

New quantities and prices start blank. Quantities must be positive, with up to nine integer digits and four fractional digits, including fractional parcels and packs. Prices are nonnegative; blank means **Unknown**, and explicit zero remains zero. Unit prices allow fifteen integer digits and total-line prices nineteen, each with up to four fractional digits. Choose one three-letter currency whenever entering a price. Switching pricing mode keeps the entered number and changes how it is applied; check the updated line estimate before saving.

The server calculates line amounts with exact arithmetic, rounding once to four decimal places with halfway values rounded up. **Merchandise estimate** sums known line amounts before discounts, shipping and tax. If any line is incomplete, **Known line subtotal** identifies that limitation. No known amounts displays Unknown, not zero. This is a draft estimate, not an invoice amount or balance due. Pending calculations hide older figures; failed calculations keep input and offer retry. Saving is independent of the preview.

Price entry starts with a `0.00` placeholder: digits fill from the right (`1` → `0.01`, `12` → `0.12`,
`123` → `1.23`). An untouched or cleared field remains Unknown. Choose **Use extra precision** to
enter a decimal directly. Saved amounts retain meaningful third and fourth digits without rounding;
pasted amounts retain their value, including whole amounts (`20` becomes `20.00`). Prices display
two decimal places unless meaningful third or fourth digits require full precision. Quantities use
ordinary decimal entry and omit insignificant trailing zeroes when reopened or summarized.

Saved lines reopen as compact descriptions, quantities and estimates. Expand a line to edit it;
new and restored lines open automatically. Optional metadata stays collapsed with a short summary,
and validation reveals fields that need attention. **Add line** is available above and below the
list. Saved, unsaved, saving and uncertain-save feedback stays beside **Save draft** while scrolling;
the header retains the last-saved timestamp. Collapsing a line does not save or discard its input.

Older complete quotes are presented on the supplier's pricing basis without changing their totals. Exact batch prices become per-unit prices; a rate that cannot be represented exactly becomes a total-line price. Reading a draft does not save these changes.

Older reference prices remain labelled **Reference price — basis not recorded** and do not contribute to estimates until you choose **Use as unit price** or **Use as total line price**. Incomplete older quotes retain a read-only summary under **Previous pricing needs review**; choose **Replace previous pricing** to enter a new price. Saving unrelated edits retains unresolved quotes.

**Clear all prices** asks for confirmation and clears current prices and retained legacy quotes. It retains the current quantities, units, metadata and currency.
Cancel or Escape preserves the amounts. Save the cleared draft before changing its currency, then
save the changed currency before entering new prices. Workbench never converts or relabels amounts.
Removal offers Undo until saving begins or the currency changes. Save failures preserve your input.

Drafts allow 100 lines and 20 order-level source links. Links must use HTTP or HTTPS and cannot
contain embedded credentials. Large drafts can reach the overall size limit before individual
field limits; shorten text or remove entries if a save reports that limit. Saving or removing a
line creates no collection item, acquisition, invoice, payment obligation or accounting entry.
Commitment, attachments, receiving and payments remain separate increments. There is no order export
workflow in this release.

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
with supplier snapshots, per-order platforms and permanent references. The [PO-03 design](specs/2026-09-16-po-03-itemized-quantities-and-prices.md) defines structured quantities and draft estimates with one supplier quantity/unit and per-unit or total-line pricing. After an upgrade, an older
client must reload before sending a new save; already successful old requests can still be resolved.

## Delete an unwanted draft

Open a saved draft and choose **Delete draft** beneath the form. The confirmation names the saved
draft and warns that local unsaved edits will be discarded. Cancel or Escape leaves everything
unchanged. Confirming removes the draft from purchase orders and returns to the list; it cannot
be restored through the application. If another member changed it, review the current version
before requesting deletion again. An uncertain deletion offers **Check and retry deletion** with
the original request; do not create a new request to resolve a lost response.
