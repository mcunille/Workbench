# Small-business scenario: track purchases and their financial outcome

**Status:** Proposed — product scenario and user stories for discussion; not approved for implementation.

## Direction and problem

Prioritize the small-business owner's purchasing workflow as the next product scenario. The owner
needs to know what they intend to buy, what they committed to, what they owe, what they paid, and
what actually arrived. Supplier carts, invoices, payment receipts, and shipping messages currently
leave those answers scattered across different places.

This shifts the next increment from hobbyist-first collection features to professional purchasing.
It follows the [vision](../VISION.md) and [design principles](../DESIGN-PRINCIPLES.md): one product
with progressively deeper capabilities. Existing collection workflows remain useful and optional
accounting configuration must not become a prerequisite for cataloging a piece.

The [collection guide](../collection.md) describes today's individual holdings, shared acquisition
context, and private acquisition documents. Those capabilities provide useful connections, but do
not establish purchase orders, quantity tracking, payments, or bookkeeping. This spec describes
desired product behavior, not a database schema, API, accounting policy, or delivery commitment.

## Persona and scenario

The primary user owns a small gemstone or jewelry business and purchases gemstones, findings,
materials, and supplies from vendors. The owner also handles purchasing and basic financial record
keeping; a separate purchasing department is not assumed. A bookkeeper is a secondary user who needs
explainable records and supporting documents. This persona comes from the user's stated need,
not completed market research.

**Outcome:** The owner can take a supplier shopping list through commitment, invoicing, payment,
shipment, receipt, and any correction, then explain every quantity and amount without reconstructing
the purchase from email.

Example: The owner builds a cart with ten stones and twenty settings, records a supplier discount,
shipping, and sales tax, then commits to the purchase and attaches the invoice. They pay a deposit.
The supplier ships part of the order; the owner records the received quantities and a damaged piece.
Later they receive the remainder, settle the invoice, and record a credit or refund for the damaged
piece. At every step they can distinguish remaining delivery from remaining payment obligations.

## Terms and lifecycle

A purchase order (PO) is the owner's record of an intended or committed purchase from one supplier.
It need not be a formal document sent to that supplier: an online checkout or in-person purchase
also qualifies. A supplier order reference and an invoice number are distinct from Workbench's PO
identifier. An invoice states a supplier charge; a payment records money sent; a credit reduces a
charge; a refund records money returned. Recording any of these must not silently imply the others.

The familiar happy path is **draft → ordered → paid → shipped → received**, but these are not a
mandatory single sequence. A supplier may ship before payment, accept several payments, or deliver
without a tracking number. Show these related dimensions together:

| Dimension | Proposed meaning |
| --- | --- |
| Order | Draft while planning; ordered on explicit commitment; canceled in full or in part with a reason. Preserve the committed version and subsequent amendments. |
| Payment | No payment, partially paid, or paid relative to the current confirmed amount; show overpayment, credits, and refunds explicitly. Unknown final charges must not appear settled. |
| Fulfillment | Not shipped, partially shipped, shipped; independently show not received, partially received, or received against the uncanceled quantities. Direct receipt need not require a shipment record. |
| Exceptions and closure | Outstanding discrepancies, returns, or refunds remain visible even after receipt. Explicit closure means no unresolved delivery, financial, or discrepancy work remains. |

Dates and supporting events explain each state. Receipt does not mean paid; payment does not mean
received. Canceling a paid order does not itself return money. Returning goods does not erase the
original receipt or automatically create an expectation of replacement: record that resolution.

## User stories

All stories below use the small-business owner persona unless stated otherwise. Core stories form
the proposed coherent scenario, not an instruction to implement them all in one change. Follow-on
stories are useful extensions to prioritize separately.

### Core purchase tracking

| ID | User story | Observable outcome |
| --- | --- | --- |
| PO-01 | As an owner, I want to build and resume a draft supplier order so that my cart or shopping list is not lost. | Save an incomplete draft, add notes or source links, and return later. Missing prices remain unknown rather than zero. Drafts do not create payment obligations or inventory. |
| PO-02 | As an owner, I want supplier identity and purchase references so that I can find an order and contact the right vendor. | Keep supplier name, optional contact details, supplier order reference, and a unique business-scoped PO reference. Later supplier edits do not rewrite historical purchase details. |
| PO-03 | As an owner, I want itemized quantities and prices so that I can explain exactly what I ordered. | Each line has a description, quantity, unit of measure, unit price and pricing basis, with optional supplier SKU, item type, notes, and link. Distinguish ten pieces from ten carats or one parcel; weight is not silently treated as piece count. |
| PO-04 | As an owner, I want to commit a draft so that planned purchases are distinguishable from purchases I placed. | Explicitly record the order date and agreed contents. Require a supplier and at least one valid line; unresolved costs stay visibly estimated or unknown. Capture later changes as amendments. |
| PO-05 | As an owner, I want discounts and additional charges so that the total matches the purchase. | Support line and order discounts, shipping, tax, and labeled additional charges; show their scopes, bases, and amounts without double counting. |
| PO-06 | As an owner, I want invoices and purchase documents together so that I can verify what the supplier charged. | Record invoice number, date, due date or terms, amounts, and private documents. Support more than one invoice per PO, highlight differences from the order, and flag potential duplicate supplier invoice references for review. |
| PO-07 | As an owner, I want deposits and subsequent payments so that I know what remains to pay. | Record each payment's date, amount, currency, method, reference, and allocation to this purchase. Show confirmed charges, net payments, credits, and outstanding balance separately; retain a receipt when available. |
| PO-08 | As an owner, I want shipment details so that I know what is on its way. | Record carrier, tracking reference or link, shipped date, expected arrival, and quantities per line for each shipment. Missing tracking is allowed; multiple shipments do not duplicate ordered quantities. |
| PO-09 | As an owner, I want to receive part or all of an order so that the record matches the delivery. | Record receipt date and quantities by line, distinguish accepted, damaged, wrong, and missing goods, and leave the remainder open. Flag overdelivery for explicit resolution. |
| PO-10 | As an owner, I want cancellations, returns, credits, and refunds so that exceptions do not corrupt the original purchase. | Cancel unfulfilled quantities, record returned quantities and reasons, and link supplier credits and cash refunds separately. Show replacements or money still expected and retain the original events. |
| PO-11 | As an owner, I want to connect purchased goods to my collection or inventory so that I can trace their origin. | Link received individually tracked pieces to the relevant PO line and acquisition context without creating duplicates. A line may supply several pieces; supplies need not create collection records. Unmapped quantities remain explicit. |
| PO-12 | As an owner, I want a purchasing overview so that I can act on outstanding work. | Search by PO, supplier, item, and invoice reference; filter by dates and the separate lifecycle dimensions. Show drafts, open commitments, due or overdue invoice balances, overdue expected deliveries, and unresolved exceptions. Keep estimates distinct from confirmed amounts. |
| PO-13 | As an owner or bookkeeper, I want an explainable history and export so that I can reconcile purchases. | Inspect who changed consequential quantities or amounts, when, and why; export linked PO, line, charge, invoice, payment, receipt, credit, and refund records with stable references and currencies. A summary can be traced back to its documents. |

### Follow-on stories

| ID | User story | Scope to evaluate separately |
| --- | --- | --- |
| PO-14 | As an owner, I want to duplicate a past order so that repeat purchasing is faster. | Create a new draft with prices to reconfirm; do not copy payments, receipts, or invoice identities. |
| PO-15 | As an owner, I want to buy and settle in different currencies so that international purchases reflect what I actually spent. | Retain original currency, settlement amount, exchange-rate source and date, and conversion fees; define business-currency reporting before combining totals. |
| PO-16 | As an owner, I want shared costs assigned to purchased items so that I understand their acquisition cost. | Propose and review allocation by value, quantity, weight, or manual assignment; define handling of mixed units, partial receipts, and later charges. |
| PO-17 | As a bookkeeper, I want invoice and payment matching across orders so that consolidated supplier billing can be reconciled. | Multiple POs on one invoice, one payment across invoices, and supplier credits carried into another purchase. |
| PO-18 | As an owner, I want purchasing roles and approval limits so that staff can prepare orders without committing unauthorized spending. | Separate preparation, commitment, financial correction, and approval authorities; basic tenant authorization is required from the first release. |
| PO-19 | As an owner, I want imports, reminders, and supplier communication so that routine data entry and follow-up take less effort. | Invoice extraction, bank feeds, supplier integrations, notifications, and sending formal POs; review extracted values before commitment. |

## Financial behavior and additional fees

The first proposed scope uses one explicit currency per PO, with its invoices, charges, payments,
credits, and refunds in that currency. Show cross-PO summaries separately per currency. Do not
silently convert or combine currencies. Whether this is sufficient for the owner's real suppliers
is an open release-scope question, not an assumption that international purchasing is unimportant.

Capture these charges when applicable, with a label, amount, payee, and supporting reference:

- Shipping or freight, handling, packing, and shipping insurance.
- Supplier sales tax or VAT/GST amounts as stated on the source document.
- Customs duty, import taxes, brokerage, and clearance fees.
- Payment processing, bank transfer, and currency-conversion fees.
- Testing, certification, or inspection fees related to the purchase.
- Return shipping and restocking charges when resolving an exception.
- Other explicitly labeled charges instead of an unexplained adjustment to unit price.

A charge owed to a carrier, bank, or customs broker contributes to the purchase's recorded cost but
must not increase the supplier's invoice balance. Each charge is counted once, even if it appears
in both an order estimate and an invoice. Mark estimated versus confirmed amounts and retain the
source of a correction. Do not infer tax rates, recoverability, tax treatment, or capitalization
from a fee label; accounting and automated tax policies require a separate design.

Discounts may be a fixed amount or percentage at line or order level. Record the applicable base,
calculation order, and resulting amount; do not silently apply an order discount to shipping or tax.
Prevent discounts exceeding their eligible base; use explicit credits for subsequent reductions.
For invoice matching, preserve supplier-stated tax and rounding amounts with an explanation for
any discrepancy instead of forcing a guessed tax calculation to agree.

The product must make the following arithmetic inspectable:

- Line gross = quantity × price per stated unit, after any explicit pricing-unit conversion.
- Order estimate or confirmed total = line gross totals − discounts + applicable charges and tax.
- Supplier balance = confirmed supplier charges − supplier credits − payments applied + refunds
  reversing those payments. A credit or refund must identify its effect; neither is counted twice.
- Net cash paid = outgoing payments − cash refunds, distinct from the adjusted purchase total.

Display draft estimates, committed amounts, invoiced amounts, balances due, and cash paid as
different measures. Do not add an order total and its invoice as two purchases. A credit can leave
a supplier owing the business money: show that credit/refund position rather than hiding it at zero.
An uninvoiced commitment is not automatically an overdue invoice. Payment terms and recorded due
dates drive overdue views. Detailed rounding and quantity precision must be settled before implementation.

### Worked acceptance example

For an illustrative USD purchase (amounts taken as entered, not a tax-rate policy):

| Component | Amount |
| --- | ---: |
| 10 stones × $20 | $200.00 |
| 20 settings × $5 | $100.00 |
| 10% discount on stones | −$20.00 |
| Order discount on merchandise after the line discount | −$10.00 |
| Supplier shipping | $15.00 |
| Supplier-stated sales tax | $21.60 |
| Confirmed supplier total | $306.60 |

A $100 deposit leaves $206.60 due once that total is confirmed. A separately paid $3 bank fee
leaves the supplier balance unchanged and brings the recorded purchase outlay to $309.60 after
settlement. A later $18 supplier credit followed by an $18 cash refund reduces the adjusted
supplier charge to $288.60 and net cash paid to the supplier to $288.60: the final supplier balance
is zero, not an additional $18 reduction. Total net cash including the bank fee is then $291.60.

## Boundaries and tradeoffs

Recommend manual entry and document attachment first, with explicit payment and fulfillment events.
A single status dropdown is simpler but cannot describe paid-but-unreceived or received-but-unpaid
orders. Building a complete procurement suite first would delay validating this owner's workflow.
The proposed scope keeps the financial distinctions while deferring automation and specialized controls.

This is purchase tracking, not yet a complete general ledger, tax return system, inventory valuation
engine, or accounts-payable suite. The [ledger principles](../DESIGN-PRINCIPLES.md) still govern future
posted financial effects: a PO is an operational record, not automatically a journal entry. Separate
design must settle posting events, account mapping, reconciliation, and period controls before claiming
accounting capability. Never rewrite closed-period postings through a PO edit.

Receiving quantities is part of this scenario; introducing general bulk stock, parcel valuation,
manufacturing, or automatic inventory cost posting is not. The collection currently represents
individual holdings, so quantity-to-piece mapping needs an approved design rather than treating an
entire multi-item line as one existing collection item. Supplier portal access, automatic ordering,
sales orders, and work orders are outside this scenario.

## Integrity, access, and compatibility

- Every record, attachment, search result, summary, and export belongs to the active business and
  requires server-enforced authority. A supplier reference or URL must not grant access.
- Preserve recoverable work on validation, upload, and save failures. Safe retries must not duplicate
  orders, invoices, payments, or receipts; concurrent changes require explicit reconciliation.
- Drafts may be incomplete, but confirmed events require valid quantities, dates, currency, and
  amounts. Corrections remain visible; do not silently delete committed purchases or financial events.
- Keep purchase documents private. Recording a payment requires a method/reference, not storage of
  card credentials or bank secrets. Reuse existing document safety and recovery rules where applicable.
- Existing collection records, acquisitions, and documents must remain accessible without creating
  synthetic POs or inferred financial history. Linking existing acquisitions must avoid duplicate records.
- No runtime, schema, API, migration, or export-format changes are authorized by this document.
  An implementation design must specify compatibility, migrations, and recovery for its actual changes.

## Scenario acceptance and validation

Validate the proposed scope with a representative real purchase before fixing the release boundary.
The integrated walkthrough must demonstrate these outcomes using persisted records:

1. Save an incomplete cart, reopen it, itemize quantities and pricing units, and commit the order.
2. Reconcile the worked example to its invoice, record the deposit, and explain the remaining balance.
3. Record partial shipment and receipt, then complete delivery without duplicating received quantities.
4. Also record a purchase received before payment and a direct receipt without tracking information.
5. Resolve a damaged item through a return and either replacement or credit/refund; preserve the history.
6. Cancel an unfulfilled remainder without implying that an existing payment was refunded.
7. Find unpaid, overdue, undelivered, and disputed purchases; trace their totals to underlying events.
8. Connect individual received pieces to their origin and retrieve the linked purchase documents.
9. Export records with enough detail to reproduce the financial example; verify business isolation,
   safe retries, concurrent corrections, and visible failure states during implementation testing.

The owner should be able to answer “what did I buy, what did it cost, what do I still owe, and what
am I still waiting for?” without developer assistance. Automated checks alone do not establish that
the workflow is usable. This documentation change exercises no new application behavior.

## Decisions to resolve before implementation

- Does the owner's first real purchase require foreign-currency settlement or consolidated billing?
  If so, promote PO-15 or PO-17 into the first scenario rather than inventing a workaround.
- Is operational purchase tracking enough for initial use, or are balanced ledger postings and
  reconciliation required from the first release? The latter needs a coordinated accounting spec.
- Which pricing units, fractional quantities, precision, and rounding rules occur in actual invoices?
- Is total purchase outlay sufficient initially, or must shared costs be allocated to individual items?
- How should receipts of parcels and bulk supplies connect to existing individual holdings?
- Which supplier documents and corrected-invoice cases must be represented structurally versus attached?

After those decisions, split the accepted scenario into verifiable implementation increments.
Story identifiers here support discussion; they are not filed issues or delivery-status claims.
