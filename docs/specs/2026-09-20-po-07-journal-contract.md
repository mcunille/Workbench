# PO-07 journal and recognition contract

[Return to the main entry](2026-09-20-po-07-deposits-and-payments.md).

## Journal and source contract

### Complete-statement coverage before cutover

BK-01 inventories every intended bank/cash/card account using complete representative statements,
including receipts, sales deposits, interest, loan principal/interest, payroll, taxes, transfers,
fees and card settlements where present. Map each transaction class to an implemented typed source,
its posting policy and reconciliation behavior. Sampling only supplier-payment rows is insufficient.
Record unsupported classes as explicit prerequisite stories for that business's activation; their
implementation needs separately approved scope. Never disguise an unsupported receipt as owner
funding or omit statement rows to achieve a zero difference.

BK-10 repeats this coverage check against the actual cutover statements and opening schedules,
including outstanding checks/transfers and expected recurring activity. Production activation
requires complete coverage for each included account and no unexplained opening difference.
If coverage is missing, remain in synthetic development until the prerequisite is delivered or
the owner/bookkeeper approves a coherent accounting perimeter and its limits. Excluding an account
must not omit activity needed by the included books. New unsupported activity after activation
remains visible and blocks affected reconciliation/close until its treatment is approved.

### Posting and correction contract

Accounting owns Accounts, Periods, JournalEntries, JournalLines, immutable source-event links,
correction links and durable command receipts. Purchasing owns versioned bills, payment/credit/
refund source records and allocation events. Reads may use rebuildable projections, but posted
journal lines and source events are the financial authority; no writable balance columns are truth.

Each posted journal records tenant, stable ID/sequence, typed source ID and revision, event kind,
posting-rule version, posting date, effective/document dates, actor, UTC recorded timestamp,
currency, reason/reference and reversal/replacement links. Lines contain account, exact debit or
credit, supplier and source dimensions where required. One side per line is positive; totals match
exactly. Require at least two lines and reject zero-only or overflow entries. Zero-value source
records can be retained without fabricating zero journals. Do not reuse or promise gap-free IDs.

Preserve four-place PO pricing calculations. Journal amounts use the BK-01 currency scale
(initial supported scales 0–4) and bounded exact decimal storage, proposed SQL decimal(28,4),
with explicitly bounded accumulation to prevent overflow. Preserve source precision separately;
preview deterministic rounding and post an explicit, approved rounding line when necessary.
Do not silently round a payment or force an unexplained invoice difference into an account.
API amounts are decimal strings; no binary floating-point money. Currency scale and posting-rule
versions are retained with each journal, so a later mapping change cannot recalculate history.

Draft source records may be edited with optimistic concurrency. Posting freezes the source revision
and appends its journal in the same SQL transaction. Even in open periods, posted corrections use
an exact inverse and optional replacement, not UPDATE/DELETE. Reversal and replacement are atomic
when requested together. Only one effective reversal of an event is allowed. A reversal of an
allocation/payment first unwinds dependent applications through linked events in that transaction;
reject ambiguous dependencies rather than silently reallocating. Accounting reversals do not
claim money was returned. Closed-period corrections retain the original effective date and use
an authorized open posting date. No reopening or year-end earnings roll-forward in this increment;
BK-01's calendar must support later stories without erasing prior periods.

Corrections of bills and credits must also resolve their dependent applications, not just payment
corrections. BK-03/BK-06/BK-08 require an atomic preview and execution of the dependency closure:
reverse affected applications, reverse/replace the source, and explicitly reapply eligible amounts.
Preserve actual payment/refund records and their funding-account postings. Reclassification entries
contain no new Bank/Cash/card movement; do not void real money movement merely to free an open item.
Reject a correction if its dependencies cannot be resolved under the approved mappings.

- A fully paid bill of 300 corrected to 280 keeps the actual payment of 300. Unapply it to a
  supplier advance (debit Advances, credit AP, 300), reverse the original bill, post the replacement,
  and apply 280 (debit AP, credit Advances). AP ends at zero and Advances at 20; Bank remains -300.
  A larger replacement instead leaves additional AP. Any later return of the 20 is a real refund.
- A credit of 18 already refunded in cash, corrected to 10, keeps the actual receipt of 18.
  Unapply the refund through an explicitly mapped supplier refund-clearing liability (debit supplier
  credit receivable, credit refund-clearing liability, 18), reverse the original credit, post the
  replacement, then apply 10 (debit refund-clearing liability, credit receivable). The remaining
  liability of 8 is money owed back to the supplier; cash remains +18. Display and reconcile that
  liability as a separate supplier control/open item, including it in the net supplier position.
  BK-01 defines its mapping, BK-06/BK-09 reconcile it, and BK-08 owns its actual repayment
  (debit refund-clearing liability, credit the funding account). No automatic repayment occurs.

These are proposed correction contracts for the prerequisite specs. If the source was posted in
a closed period, all correction/reapplication entries use an eligible open date; prior closed
balances remain unchanged. Retain original classifications unless the correction explicitly changes
them under BK-04. An already reconciled cash entry stays matched when its amount/account/date did
not change; a correction to that cash entry itself requires explicit reconciliation resolution.

Allow narrowly typed source adapters, not caller-authored arbitrary journals from a PO endpoint.
Runtime users cannot directly write ledger tables or post manual entries to supplier control
accounts. Opening entries use a dedicated reviewed adapter with matching open-item evidence.
General manual journals, if later introduced, need separate authority and control-account rules.

## Recognition and example postings

An invoice is evidence, not a universal recognition trigger. A pro forma request, goods in transit,
services received, prepayment and accepted inventory can require different treatment. BK-04 must
settle the applicable policy before enabling those transaction types. A PO confirmation or shipment
status alone never recognizes an expense, inventory or payable.

| Event | Debit | Credit | Effect / condition |
| --- | --- | --- | --- |
| Supplier deposit of 100 | Supplier advances 100 | Bank 100 | Asset retained until applied; no expense and no AP reduction yet. |
| Recognized supplier bill of 306.60 | Approved expense/asset/tax accounts totaling 306.60 | Accounts payable 306.60 | Illustrative bill for recognized goods/services; split debits by BK-04 rules. |
| Apply deposit of 100 to bill | Accounts payable 100 | Supplier advances 100 | Explicit allocation; no new cash movement. |
| Subsequent payment of 206.60 allocated immediately | Accounts payable 206.60 | Bank 206.60 | Clear the remaining bill; recording and allocation are atomic. |
| Paid bank fee of 3 | Bank fee expense 3 | Bank 3 | Separate payee/source; never increases supplier AP. |
| Supplier credit of 18 after bill is fully paid | Supplier credit receivable 18 | Original applicable cost/asset/tax accounts 18 | Preserve classification; no cash has returned yet. |
| Cash refund of that credit, 18 | Bank 18 | Supplier credit receivable 18 | Clears the receivable; never reduces purchase cost again. |
| Refund of an unused deposit | Bank | Supplier advances | Does not create a supplier credit note or reverse an expense. |

For an unpaid bill, a credit debits AP up to the open amount and credits the applicable original
classification. Any excess becomes a supplier credit receivable. Applying an existing receivable
to an open bill debits AP and credits that receivable, with an allocation event. Returned/sold
inventory and previously recovered tax may require a different credit classification; BK-04/BK-08
must validate the actual remaining asset/tax position rather than blindly reversing an old account.

BK-04 must also specify matched receipt accounting: an uninvoiced recognized receipt may debit
inventory/expense and credit a goods-received-not-invoiced accrual; the later invoice clears that
accrual and records only approved differences against AP. An invoice preceding recognition may
use an approved prepayment/clearing treatment. These are policy examples, not an instruction to
post on every physical receipt. Quantity fulfillment (PO-09), financial recognition and later
item-cost allocation (PO-16) have separate evidence and must not recognize the same cost twice.
If BK-04 needs structured receipts for the selected policy, the minimal receipt-recognition slice
of PO-09 becomes an explicit dependency before that policy is enabled; it cannot be deferred by
assuming all supplier invoices imply delivery.
