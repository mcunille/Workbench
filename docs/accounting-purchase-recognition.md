# Accounting purchase recognition

[Return to Accounting](accounting.md).

## Internal purchase recognition

BK-04 records an explicitly classified whole purchase unit as Expense or Inventory. Recognition
before an invoice accrues cost against receipt accrual; an eligible invoice first records prepayment
and supplier payable. An explicit match clears the original accrual or prepayment account, even after
mapping changes, and posts only the approved cost difference. Inventory differences require durable
held-inventory evidence. Equal amounts alone never match sources.

Components distinguish cost, positive discount reductions, freight, charges, nonrecoverable tax,
recoverable tax and signed rounding. Amounts must exactly fit the configured currency scale; one
approved rounding component per invoice source may not exceed one minor unit in absolute value.
Source capacity, rounding and released-capacity date limits span surviving source revisions. A
second side cannot predate the first posting. Amount-only partial matches are unsupported.

Correction discovers and reverses the complete dependency group and optionally replaces both sides
with fresh revisions. Original evidence remains immutable, zero-value sides retain receipts without
inventing journals, and retries require current authority. Journal detail exposes recognition units,
components, matches and correction relationships to authorized accounting readers. Cutoff lists and
trial balances retain the original activity when later corrections fall outside their filters.

These kernels have no runtime execute grants or public financial write routes. BK-05 supplies the
internal bill-source adapter described below; synthetic receipt adapters exist only in disposable tests. `BookkeepingAvailable` remains false;
public payment and bill entry, operational fulfillment, item valuation, cost of sales and production closing
remain future work.

Recognition requires explicit evidence of consumed goods/performed services for Expense, or goods,
positive quantity/unit and documented control transfer for Inventory. Physical possession alone is
insufficient. Invoice-first Prepayment needs an eligible present obligation and enforceable future
right, with reviewer rationale; a demand or pro forma cannot establish that. Recoverable tax needs
an explicit policy reference and reviewed entitlement on its effective date. Nonrecoverable tax
belongs to the classified cost. Country, supplier, account name and document label infer none of these.
Unsupported eligibility, tax entitlement, fixed assets/depreciation, consignment, FX and automatic
amortization reject pending their own supported policies.

Let E be approved receipt-first estimated cost, C final invoice cost including nonrecoverable tax,
and T explicitly recoverable tax:

| Event | Entries |
| --- | --- |
| Recognition before invoice | Debit Expense/Inventory E; credit receipt accrual E. |
| Invoice after recognition | Debit original accrual E; debit/credit original cost by C − E; debit RecoverableTax T; credit SupplierPayable C + T. |
| Eligible invoice before recognition | Debit Prepayment C and RecoverableTax T; credit SupplierPayable C + T. |
| Recognition after invoice | Debit Expense/Inventory C; credit original Prepayment C. |

For inventory received at 100 then invoiced at cost 105 plus recoverable tax 5, matching leaves
Inventory 105, tax asset 5, accrual 0 and payable 110. A final cost of 98 instead credits cost by 2;
it is not a supplier credit note. Expense adjustments retain the original expense account. Inventory
adjustments need trusted evidence that the goods remain held; reviewer assertions cannot establish
that evidence. With no production inventory-state adapter, bill inventory variances reject, while
equal-value inventory matching remains supported. Cash deposits belong to SupplierAdvance, not
recognition Prepayment. Third-party charges need their own supplier/source. Every cost component,
discount, charge and rounding assignment is explicit; unexplained differences cannot plug tax or
an unrelated expense account.
