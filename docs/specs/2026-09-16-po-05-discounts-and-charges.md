# PO-05: draft discounts and additional charges

**Status:** Implemented on 2026-09-17. The owner approved the in-place V4 extension and broader
charge categories on 2026-09-16. Source, migration, browser and container delivery gates passed.

## Scope

Implement PO-05 from the [purchasing scenario](2026-09-11-purchase-orders-and-purchase-finances.md)
before PO-04. Extend the existing PO-03 draft editor and exact server calculations. Nothing
commits an order or creates invoices, balances due, payments, inventory or ledger postings.
All aggregate purchase amounts remain draft estimates. A charge may retain a supplier-stated
confirmed amount without confirming the order or creating a liability.

## Discounts and calculation policy

Support one optional discount per line and one optional order discount, each fixed amount or
percentage. This intentionally defers stacks of sequential discounts; the user can enter their
combined fixed reduction. Scope and base are determined by placement, not a configurable formula.

1. Calculate each line gross using the existing per-unit or total-line rules.
2. Apply its discount to that rounded gross; subtract the rounded discount to obtain line net.
3. Sum line nets. Apply the order discount to this merchandise amount only.
4. Add supplier charges to obtain the supplier draft estimate.
5. Add third-party charges to obtain the total purchase estimate.

Shipping, tax and other charges are never discounted. Do not calculate taxes from rates, infer
tax treatment, allocate costs to lines, or add an invoice to an order total.

Use decimal strings and exact arithmetic. Retain PO-03's four-place precision and midpoint-up
rounding. Round each percentage discount once to four places; sums/subtractions are exact.
Fixed discounts are nonnegative and allow 19 integer digits and four fractional digits; percentages allow 0–100
inclusive with four fractional digits. A supplied discount must have a value. Explicit zero is
valid; removing a discount means no discount. Reject a fixed discount exceeding a known eligible
base. Cap computed totals at 21 integer digits and four fractional digits with a field error
instead of overflow or clamping.

Incomplete drafts remain saveable. When a discount's base is unknown, retain the entered discount
as awaiting a base; show its result as Unknown and do not apply it to a partial subtotal. Revalidate
against the complete base when quantities/prices become known. A fixed discount on an unknown base
cannot produce a validated net or complete total. Known line subtotals remain visible separately.
An order discount requires every line net to be known. An empty order has no merchandise estimate;
known charges may be summarized, but do not turn an empty order into a complete purchase estimate.

## Charges

Use one ordered collection of charges, with stable UUIDs unique within the draft, maximum 50.
Shipping, taxes and duties are categories of these rows, never additional standalone amount fields.
Each charge has:

- Category from the table below.
- Required trimmed label, maximum 200 code units, defaulting to the category's display name
  except Other, which requires a descriptive label.
- Nullable nonnegative amount, up to 19 integer digits and four decimal places. Blank is Unknown.
- Required payee kind: `supplier` or `thirdParty`. Third-party rows require a payee name of up to
  200 code units; supplier rows use the order's supplier snapshot, which may still be incomplete.
- Required amount status: `estimated` or `confirmed`, default estimated. Confirmed requires an amount.
- Optional supporting reference (200 code units) and explanation/notes (2000 code units).

| Stored category | Display name |
| --- | --- |
| `shipping` | Shipping / freight |
| `handling` | Handling / packing |
| `insurance` | Shipping insurance |
| `salesTax` | Sales tax |
| `vatGst` | VAT / GST |
| `customsDuty` | Customs duty / tariff |
| `otherTax` | Other tax |
| `brokerage` | Brokerage / customs clearance |
| `paymentFee` | Payment / bank / currency-conversion fee |
| `inspection` | Testing / certification / inspection |
| `other` | Other |

Tax is not synonymous with sales tax. Customs duty/tariff is a tax on cross-border goods,
but remains a distinct category for recognition and reporting. VAT/GST collected on import
uses VAT / GST with a label such as Import VAT, not Customs duty. Other tax covers separately
stated taxes such as excise or use tax. Multiple rows may share a category: preserve separate
state/local taxes or separate tariffs when itemized on a source document. A combined source
amount is recorded once, with a descriptive label; never invent a split or duplicate its total.
Brokerage and clearance service fees remain separate from duties/taxes when separately stated.

Category and payee are independent: a supplier may collect duty, and a carrier may collect
import VAT. Category does not select a tax rate, infer recoverability, or decide who is owed.
The charge basis is always an entered amount in the PO currency, taken from the source document
or the owner's explicit estimate, not a guessed percentage. Record a rounding discrepancy as a labeled nonnegative additional charge
when it increases cost, or as a fixed discount when it reduces eligible merchandise. Signed
rounding adjustments outside those semantics remain deferred and must not be disguised as tax.

A blank charge amount makes its applicable aggregate unknown. A third-party unknown amount does
not make an otherwise complete supplier estimate unknown. A charge-only subtotal is labeled as
such. Mixed estimated/confirmed charge rows never turn draft totals into confirmed balances.
Editing an estimate to a confirmed source amount updates that row, preserving its UUID; do not
create a second row for the same fee. Require an explanation when changing an existing confirmed
charge's amount, payee or status. Compare against the last persisted row: its replacement notes must
be nonblank and different from its saved notes, so an old explanation cannot silently justify a
new correction. Preserve earlier explanations by appending the new one in the UI; enforce the
notes length limit without truncation. This is draft correction context, not immutable financial history.
Invoice matching and permanent amendment history belong to later increments.

Currency is required for fixed discounts and charge amounts as for existing prices. Percentage-only
discounts can be entered without currency. Extend the current saved-currency lock to all monetary
fields. Replace Clear all prices with **Clear all amounts**, listing prices, discounts and charges
in its confirmation; clear line/legacy prices and discounts, retain charge identity/metadata but
clear amounts and reset their status to estimated. If saved confirmed charges are affected, the
confirmation requires one shared explanation and appends it to each affected row's notes; cancel
leaves everything unchanged. Save before changing currency, and save the
new currency before entering monetary amounts. Never relabel an existing amount.

## UI direction — impeccable / Operate

The audience is the owner reconciling a supplier cart or source document. Preserve Tanzanite,
the existing line disclosures, field components, explicit Save draft toolbar, light/dark appearance,
keyboard navigation and responsive layout. This is an extension of the established editor.

Add **Add discount** beneath each line's pricing fields. Once present, show Fixed amount / Percentage,
the value, eligible base, resulting reduction and line net. Collapsed lines show their net estimate
and a concise discount indicator; expanded arithmetic retains the gross so nothing disappears.

After Order lines, add **Discounts and charges**: an optional order discount followed by charge rows.
Provide one **Add charge** action; each new row has a category selector with readable groups
for delivery, taxes/duties, fees/services and Other. Do not add a button for every category.
Each row leads with its label and amount; payee and estimated/confirmed status remain visible.
Supporting reference and notes use progressive disclosure. Remove offers Undo until save or a
currency transition, consistent with line removal. Errors reveal and focus affected controls.

The owner's 2026-09-17 distillation request makes saved charges compact expandable rows, preserving
label, amount, payee and source status. New, restored and invalid charges open for editing.
Keep the existing monetary-entry semantics and precision controls; display detailed entry guidance
in a named help disclosure. Discount removal uses the same danger treatment as line and charge removal.
Provide a keyboard-accessible **View purchase estimate** jump near the order identity.

Place an inspectable summary directly below the charges: merchandise gross, line discounts,
merchandise net, order discount, supplier charges, supplier draft estimate, third-party charges,
and total purchase estimate. Omit absent adjustment rows. Show the currency on amounts and base
descriptions beside discounts. The final purchase estimate is the strongest amount; the supplier
estimate remains separately labeled. No balance-due or paid language. Do not show stale values
while calculations are pending/failed. Preserve editing and retry affordances.

Desktop uses aligned label/value rows; mobile stacks charge fields without horizontal scrolling.
Avoid a second sticky summary competing with Save draft. Screen-reader status announcements report
calculation state without moving focus. Conflict comparison includes every financial input and
its payee/status, not only the old line fields.

Critique the implemented editor against these tasks: explain the worked example without mental
arithmetic; distinguish supplier and bank costs; identify unknown amounts; recover from invalid
discounts, failed previews and concurrent saves. Inspect desktop/mobile and both appearances in
one batched round, fix material findings together, then confirm. Follow the impeccable craft floor
before UI edits and its independent finish review/documentation workflow before delivery.

## API, storage, security and compatibility

Extend the existing V4 draft/calculate contract in place, as directed by the owner; do not add
V5 routes, DTO families or SQL command families. Increment the persisted content schema to 4
so old JSON remains distinguishable; this is a storage marker, not another API version. Keep decimal strings,
closed input validation, required nullable fields and canonical normalization. Include discount
and charge inputs in request fingerprints. Calculated bases, reductions, line nets, supplier
and purchase estimates, and incompleteness reasons are read-only response data.

Persist adjustments in the existing draft content JSON; do not introduce a financial subsystem.
Upgrade earlier content in memory with no discounts and an empty charge collection, preserving
legacy quotes exactly. Merely reading must not rewrite a record. Preserve existing references,
supplier snapshots, tenant ownership, row versions, request receipts and deletion behavior.

Update the existing V4 restricted SQL commands and authoritative validation for the new JSON shape, monetary bounds,
known-base discount limits, currency transitions and correction requirements. SQL direct-command
tests must establish parity with API validation; the web principal retains no general write access.
Validate collection uniqueness and reject malformed/unknown fields at both trust boundaries.

Require the new adjustment properties on new V4 writes, including explicit null discounts and
empty charge arrays. Reject stale V4 payloads missing those properties rather than defaulting
them and erasing saved adjustments. Preserve resolution of already successful pre-upgrade requests
against their original receipt/fingerprint; this does not authorize accepting new old-shape writes.
Existing V1–V3 mutation/read guards must also protect new content. Older reads must never silently
omit financial adjustments: return reload-required for content they cannot represent. Retain the
V4 body-size and middleware protection without increasing current global limits. Removing earlier
versions is a separate future iteration, not part of PO-05.

Ship one forward migration for this coherent change, updating the content constraint, commands,
permissions and schema-readiness markers. Do not rewrite base migrations. Verify fresh creation
and upgrade from the PR base, including representative old content and request receipts. Drain
incompatible writers for deployment. Rollback that could discard new financial inputs must fail
explicitly; use a forward correction or the documented backup/restore procedure. No destructive
cleanup of retained previews or shared databases.

## Acceptance and delivery

- Persist/reopen 10 stones × USD 20 and 20 settings × USD 5; apply a 10% stone discount and USD 10
  order discount to USD 280 merchandise net. Add supplier shipping USD 15 and tax USD 21.60:
  supplier draft estimate USD 306.60. Add bank fee USD 3 as third-party: total purchase estimate
  USD 309.60; supplier estimate unchanged. No payment/balance or commitment is inferred.
- Cover fixed/percentage discounts on both scopes, percentage rounding, exact zero, 100%, unknown
  bases/charges, empty orders, over-discounts, overflow, editing/removing/undoing adjustments,
  confirmed correction explanations and independent supplier/third-party incompleteness.
- Persist each charge category, multiple tax/duty rows and an import-VAT label. Verify category
  changes do not change arithmetic or payee and each row contributes exactly once.
- Verify tenant isolation, direct SQL rejection, stale versions, idempotent retries, older-client
  write/read protection, old-draft upgrade and currency clearing without data loss.
- Use focused failing tests before each behavior change, with GIVEN/WHEN/THEN comments. Test exact
  arithmetic and state transitions with affected mutation tooling; document tooling limits.
- Regenerate OpenAPI/client contracts. Run repository verify and container smoke gates. Refresh this
  checkout using dev-up.ps1 and exercise persisted browser flows on current source. Record preview
  URL and permitted external screenshot evidence without credentials or media in Git.
- Update purchasing/architecture/migration guidance, internally review the complete change, then
  commit, push and open a ready-for-review PR. Do not merge.

## Alternatives and approval boundary

The owner chose to extend V4 in place rather than introduce another API version. Required new
properties protect adjustments against stale replacement writes; coordinated deployment and reload
are accepted instead of maintaining another full contract version. Dedicated charge tables and
posting events add premature accounting semantics, so retain draft JSON. Multiple
discount stacks and configurable tax bases increase ambiguity; explicit single discounts and entered
charges satisfy the story with inspectable arithmetic. Two-decimal-only calculation would conflict
with the current PO-03 precision, so retain four places until invoice settlement is designed.

The owner approved this implementation design and its public draft contract, database write contract
and financial calculation policy. PO-04 commitment remains a separate increment.
