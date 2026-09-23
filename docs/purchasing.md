# Purchase orders

The purchasing API is beta, as are all unreleased Workbench APIs. The current application uses
`/api/beta/purchase-order-drafts` and `/api/beta/suppliers`; older API paths cannot create or
change drafts or replay historical requests. Exact successful retries remain supported by the current beta API.
See [API lifecycle](api-lifecycle.md) for compatibility and rollout rules. If Workbench asks
you to reload after an update, copy unsaved changes first. For an unconfirmed save, use
**Select purchase draft text** (or **Select supplier text**) to focus and select the read-only
recovery text, then copy it with your usual keyboard shortcut. This does not confirm the save;
keep the page open and check the saved record before starting another save.

Purchase orders lets members of a business plan a supplier purchase and return to it later.
Use **New draft**, record what you know, and select **Save draft**. An empty draft is valid;
it appears as **Empty draft** with its permanent PO reference. Start with a supplier,
items, or both; no title is needed. Lists identify purchases by custom title when present,
otherwise supplier or the first described item, alongside the PO reference. Saved drafts remain available after
signing out and signing back in. All records belong to the signed-in business.

## Itemize a purchase

Start with supplier details and order lines. Under **Notes and custom label**, an optional
custom title can help you recognize a purchase; existing titles stay editable and searchable.
Keep notes and source links there too. Under **Order lines**, add a description and one quantity and unit matching how the supplier charges. Optional details include supplier SKU, item type, notes and a source link. Incomplete lines and empty drafts remain saveable; they do not commit a purchase.

Choose **Per unit** or **Total line** pricing. Per unit multiplies quantity by unit price: 12.5 carats at USD 20 produces USD 250.00. Total line records the supplier's amount directly and does not require quantity or unit to calculate. There is no separate ordered count, pricing unit, batch denominator or priced quantity. A quote of USD 8 per 100 pieces can be entered as USD 0.08 per piece, or as the total for the line. Workbench does not convert between units; ounce and troy ounce are distinct.

New quantities and prices start blank. Quantities must be positive, with up to nine integer digits and four fractional digits, including fractional parcels and packs. Prices are nonnegative; blank means **Unknown**, and explicit zero remains zero. Unit prices allow fifteen integer digits and total-line prices nineteen, each with up to four fractional digits. Choose one three-letter currency whenever entering a price. Switching pricing mode keeps the entered number and changes how it is applied; check the updated line estimate before saving.

The server calculates line amounts with exact arithmetic, rounding once to four decimal places with halfway values rounded up. **Merchandise gross** sums known line amounts before discounts, shipping and tax. If any line is incomplete, **Known line subtotal** identifies that limitation. No known amounts displays Unknown, not zero. This is a draft estimate, not an invoice amount or balance due. Pending calculations hide older figures; failed calculations keep input and offer retry. Saving is independent of the preview.

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

**Clear all amounts** asks for confirmation and clears current prices, retained legacy quotes,
line/order discounts and charge amounts. Charge status becomes estimated; charge labels, payees,
references and notes stay. It retains quantities, units and currency. Clearing saved confirmed
charges requires an explanation, appended to each affected charge's notes.
Cancel or Escape preserves the amounts. Save the cleared draft before changing its currency, then
save the changed currency before entering new amounts. Workbench never converts or relabels amounts.
Removal offers Undo until saving begins or the currency changes. Save failures preserve your input.

Drafts allow 100 lines and 20 order-level source links. Links must use HTTP or HTTPS and cannot
contain embedded credentials. Large drafts can reach the overall size limit before individual
field limits; shorten text or remove entries if a save reports that limit. Saving or removing a
line creates no collection item, acquisition, invoice, payment obligation or accounting entry.
Attachments, receiving and payments remain separate increments. There is no order export
workflow in this release.

## Discounts and additional charges

Use **Add discount** within a line or **Add order discount** under Discounts and charges.
Choose a fixed amount or percentage. A line discount applies to its gross amount; the order
discount applies to merchandise after line discounts, excluding all charges. One discount is
supported at each scope. Enter a combined fixed amount for multiple supplier reductions.
The editor shows the eligible base and reduction. Percentages allow up to four decimals and
cannot exceed 100%; fixed reductions cannot exceed a known base. Missing bases stay Unknown,
and an order discount is not applied to a partial known subtotal.

Use **Add charge** for shipping/freight, handling/packing, shipping insurance, sales tax, VAT/GST,
customs duty/tariff, other tax, brokerage/clearance, payment/bank/conversion fees,
testing/certification/inspection, or another labeled charge. Categories describe what the amount
is for; they do not calculate tax rates or infer accounting treatment. Import VAT belongs under
VAT/GST, separately from customs duty. Multiple rows can share a category when the source itemizes
them. Record a combined amount once rather than entering it again under each component.

Each charge keeps its own label, amount, supplier or named third-party payee, estimated/confirmed
status, supporting reference and notes. Blank amounts stay Unknown. Confirmed means the amount
is confirmed from a source; the order remains a draft. Changing a saved confirmed amount, payee
or status requires an updated explanation in notes. Update the same charge when an estimate is
confirmed rather than adding it twice. Charge removal offers Undo until save or currency change.

Saved charges open as compact rows showing their label, payee, amount and source status. Expand
a row to edit; new, restored and invalid charges open for entry or correction. Monetary-entry
guidance is available from **Cents entry help** or **Decimal entry help** beside the precision control. **View purchase estimate**
near the order identity jumps directly to the full breakdown without changing the draft.

The summary separates supplier charges and third-party costs. Ten stones at USD 20 with a 10%
line discount, twenty settings at USD 5, a USD 10 order discount, USD 15 supplier shipping and
USD 21.60 sales tax produce a **Supplier draft estimate** of USD 306.60. A USD 3 third-party bank
fee raises **Total purchase estimate** to USD 309.60 without changing the supplier estimate.
No payment or balance due is inferred. Unknown third-party amounts leave the supplier estimate
available when otherwise complete; the purchase total remains Unknown. Charge-only subtotals do
not constitute a complete purchase estimate until merchandise is entered.

The [PO-05 design](specs/2026-09-16-po-05-discounts-and-charges.md) defines these rules. This
increment supplies the financial inputs preserved by PO-04 commitments and amendments. Structured invoices
and payments remain separate work. The beta contract has no revision negotiation. Stale open clients may need a manual reload
after preserving edits; ordinary endpoint validation and conflicts govern their requests. Current beta retries retain their original request identity and result.
Retired API requests are unsupported; inspect the saved record before replacing an uncertain old save.

## Suppliers, platforms and purchase references

The first save assigns a permanent business reference such as **PO-000001**. A reference identifies
the draft; it does not mean the purchase was placed. Numbers increase within each business, do not
reset each year, and are never reused after deletion. Supplier order reference is a separate optional
field for the supplier's own identifier; it is not an invoice number.

Use **Manage suppliers** from Purchase orders to create and maintain reusable suppliers, or enter
one-off supplier details directly on a draft. Contact name, email, phone, website and postal address
are optional. Duplicate supplier names are allowed. Archiving a supplier removes it from default
selection while keeping its saved orders and details; it can be reactivated later.

Supplier directory websites accept domains such as `example.com` or `www.example.com/shop`;
saving trims surrounding whitespace and adds `https://` when the scheme is omitted. Explicit
HTTP/HTTPS schemes, paths, queries and fragments are preserved. Websites remain optional;
malformed addresses, other schemes, spaces and embedded credentials are rejected by the server.

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

**Search purchase orders** finds drafts and ordered purchases by Workbench reference, supplier order reference,
supplier snapshot name or title across the business. Platform is displayed but is not searched in
this release. Clear the search to return to normal browsing. Supplier search matches names and can
include archived records. Both lists retain loaded results when a page fails.

## Resume and recover work

Purchase orders lists drafts and ordered purchases with the most recently saved first. **Load more** retrieves the
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

## Record a placed purchase

Save and review the draft, then choose **Record as ordered**. Enter the calendar date you placed
the order and select **Confirm order**. Workbench requires a supplier name (a directory link is
optional), currency, and at least one line. Every line needs a description, positive quantity and
unit, including lines priced by total. Resolve or clear old reference quotes before commitment.
Unknown prices and charge amounts are allowed; ordering does not turn estimates into confirmed
charges. It records no payment, invoice balance, receipt, inventory item or accounting entry.

The PO reference stays the same. The list distinguishes **Draft** and **Ordered** and can filter
by order state. The ordered view shows the entered order date separately from history timestamps.
If commitment is unconfirmed, **Check and retry commitment** retains the original request and date.
Do not begin another commitment to recover a lost response.

## Amend an ordered purchase

Ordered purchases cannot be deleted or saved through draft editing. Choose **Create amendment**,
edit the latest contents, and enter an **Amendment reason**. **Review amendment** shows current and
proposed contents and the order date before **Record amendment** appends the next revision.
Currency is fixed after commitment; quantities and costs still follow the existing validation rules.
Changing a confirmed charge also requires the explanatory charge notes used by draft corrections.
Removing a line or reducing quantity records the amendment without claiming a cancellation or refund.

**View history** lists the original commitment and subsequent amendments with their actor IDs,
recording timestamps and reasons. Select a revision to inspect its complete contents, order date
and saved calculation; amendments also show the preceding revision. Earlier supplier/contact
snapshots and quantities remain available even after directory changes or line removal.

Amendments are local unsaved work until recorded. Validation and network failures keep input;
uncertain writes retain their request for retry. A confirmed write followed by a failed read retries
only the read. Competing changes require explicit comparison before resubmission. Earlier revisions
are never overwritten. The [PO-04 design](specs/2026-09-17-po-04-commitment-and-amendments.md)
defines the commitment, history and concurrency contracts.

## Keep invoice files with an ordered purchase

In **Invoice files**, choose **Add invoice files**, select one or more supplier PDFs, and review
their labels before **Upload files**. Labels start with the filename and can be changed. JPEG,
PNG and WebP are also supported. Each file is limited to 10 MiB; a purchase can hold 20 current
or pending files. Files remain private to members of the business and may contain embedded
metadata. The existing document validator accepts a restricted PDF subset; a rejected file may
need re-exporting or a supported image copy.

Uploads run one file at a time and show which files were uploaded. If a response is lost, use
**Check and retry file** to resolve that exact upload, then **Upload remaining files** if needed.
If the write succeeded but refreshing failed, **Retry loading saved files** repeats only the read.
Validation errors retain your selection; remove a rejected file from the selection or correct its
label. A conflicting change requires **Review current files** before explicitly resubmitting.
Unsaved selections exist only in the open page. Files already uploaded stay attached if you cancel
the remaining selection. Finish or cancel file editing before opening an amendment or history.

Each saved file offers **Download**, **Rename** and **Remove**. Rename affects only its label;
file bytes cannot be edited. Removal asks for confirmation and immediately revokes download
access; retained copies follow the seven-day retention policy and any holds. Recovery-unavailable
files keep their metadata and explain that another copy or administrator help is needed.

Files do not create structured invoice amounts, payments, credits or inventory and do not advance
the agreed order revision. One file may contain several invoices, or one invoice may span files.
Invoice numbers, due dates, amount comparisons and duplicate supplier-reference warnings remain
future work. See the [attachment scope](specs/2026-09-18-po-06-invoices-and-purchase-documents.md).

## Delete an unwanted draft

Open a saved draft and choose **Delete draft** beneath the form. The confirmation names the saved
draft and warns that local unsaved edits will be discarded. Cancel or Escape leaves everything
unchanged. Confirming removes the draft from purchase orders and returns to the list; it cannot
be restored through the application. If another member changed it, review the current version
before requesting deletion again. An uncertain deletion offers **Check and retry deletion** with
the original request; do not create a new request to resolve a lost response.

## Supplier social handles

Use **Add social** in a supplier's **Social handles (optional)** section to record a **Platform** and
**URL / Handle** of your choosing. For example, enter **Discord** and **gemdealer**, or a marketplace name
and seller identifier. Edit either field, or use **Remove** to remove a pair, then save the supplier.
Both fields are required for each added row; labels must be unique ignoring case. Up to 20 pairs
are supported. Handles are plain text kept for reference; they need not be web addresses and
have no opening action. The website is separate, and purchase-order snapshots stay unchanged.
See the [supplier handles design](specs/2026-09-20-supplier-profiles.md).