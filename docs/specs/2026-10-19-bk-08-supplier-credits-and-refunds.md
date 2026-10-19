# BK-08: supplier credits, refunds and source corrections

**Status:** Accepted written specification, including its linked authority, recovery and verification
contract. The user approved the scope and then both written artifacts on October 19, 2026.
The implementation plan and execution method still require their handoff. No runtime changes or
bookkeeping activation are claimed.

Parent: [PO-07 bookkeeping prerequisites](2026-09-20-po-07-deposits-and-payments.md).
Dependencies: [BK-04 recognition](2026-09-25-bk-04-classified-purchase-recognition.md),
[BK-05 bills](2026-09-27-bk-05-structured-supplier-bills.md),
[BK-06 open items](2026-09-27-bk-06-supplier-open-items-and-allocations.md), and
[BK-07 retention](2026-09-28-bk-07-durable-financial-evidence.md).
Inspected baseline: main commit `20d3e28`; schema ends at
`20261008010000_AddFinancialEvidenceRetention`.

## Outcome and delivery scope

Give bookkeepers distinct evidence for a supplier reducing a purchase charge and for money actually
returning. An unused deposit returned by a supplier reduces an advance, not purchase cost. A correction
repairs a recorded fact; it must never masquerade as a refund or cause an actual payment to disappear.

Deliver internal, source-owned credit posting, explicit credit allocation, actual refunds, actual
repayment of refund-clearing liabilities, and preview/execution of complete bill, credit and refund
corrections. Extend authorized existing readback, evidence retention and recovery. The paid-bill and
refunded-credit correction examples must run against authentic production source commands, rather
than disposable adapters pretending those commands exist.

Keep `BookkeepingAvailable` false, runtime financial mutation grants absent, and financial-write
permissions unassigned in production. Do not deliver public financial-write routes or forms, money
transfer initiation, cross-PO allocation, FX, card funding/refunds, bank reconciliation, fees,
opening schedules, inventory valuation, tax filing or production close. Ordinary purchasing must
continue without accounting setup. BK-12 owns the worked example's bank fee; its absence must be
visible in the evidence, rather than simulated as a supplier payment.

## Existing implementation and selected approach

The existing journal is the only journal writer. BK-06 already models Payable, Advance,
CreditReceivable and RefundClearing, immutable movements, applications, historical control accounts,
dual cutoffs and reconciliation. `SupplierAllocationAvailability` checks capacity through later
event dates. `PlanSupplierCorrection` and payment corrections establish authenticated composition,
dependency previews and a common recorded instant. Bill-owned recognition and journal corrections
currently reject generic entrypoints; those safeguards remain.

BK-07's `FinancialEvidenceSourceAdapters` authenticates bills, payments and recognition. New sources
must participate there before they can acquire document holds. Current supplier reports recognize
only the existing authentic source shapes; extending storage without extending report validation
would leave unresolved attribution and is not sufficient delivery.

Extend these modules with focused source implementations. Credit validation owns cost/tax capacity;
cash-source validation owns real movement and its settlements; correction orchestration owns the
complete dependency closure. Their interfaces take business identities, versions and intent, not
caller-authored journals. The journal, allocation and retention implementations remain shared.
Do not add a general plugin framework, duplicate balance store, evidence archive or financial draft
subsystem merely to connect these implementations.

Alternatives considered:

- Journal-only credits/refunds cannot establish component capacity, settlement ownership or safe
  corrections. Reject this approach even when its current trial balance appears correct.
- Public PO-07 entry now adds UI, runtime grants and release dependencies beyond BK-08. Defer that
  workflow while making the internal commands authentic and independently verifiable.
- Reuse payment reversal for money returned: reject because it rewrites the meaning of actual cash
  evidence and cannot preserve the original payment in the required correction examples.

## Source identity and immutable records

One source belongs to one tenant, supplier, ordered PO and configured functional currency. Sources
remain addressable after settlement/correction. Use tenant-qualified keys, RLS, direct-DML denial,
append-only financial records and separate mutable concurrency versions, following BK-06.

Persist these responsibilities in Purchasing:

| Record | Required evidence |
| --- | --- |
| Credit source/revision | Stable credit ID, immutable revision, supplier/PO/currency snapshots, normalized supplier credit reference, document/effective/posting dates, original posted bill revision/component claims, positive total, reason, actor, evidence and financial-group IDs. |
| Credit component claim | Original bill and recognition component identity, approved treatment, exact cost/tax reduction and original account provenance; links to any successor correction claims. |
| Cash source/revision | Stable ID, typed Refund or RefundRepayment, actual/effective/posting dates, positive gross amount, Bank/Cash account identity/version snapshot, method, reference, notes, supplier/PO, evidence and financial-group IDs. |
| Cash settlement | Immutable cash-source-to-item consumption, positive amount, item/control identity, posting/recording dates, and exact journal attribution. One cash source may consume several eligible items on its PO. |
| Cash settlement release | Once-only inverse link to a settlement, restored asset capacity, new refund-clearing item, reason and correction group; no new cash movement. |
| Source correction | Original and optional fresh successor source/revision IDs, complete preview fingerprint, reason, dependency links, actor and common recorded instant. |

Reuse existing supplier financial receipts, groups, open items, movements, applications and control
attribution instead of maintaining new editable balances. No signed-amount shortcut changes an event
kind. A refund received and a refund repayment sent are distinct positive-amount commands.

The credit source is posted in one operation from a complete reviewed breakdown, without a separate
mutable draft/review lifecycle. That operation records the authorized review decision and exact
source revision atomically. Supplier credit reference duplicate checks span that supplier's POs and
currencies, independently of invoice references. Reuse bill normalization rules; require a reasoned
resolution of the exact current duplicate set under the financial lock. An existing immutable credit
identity/revision cannot post twice, even with a different request UUID. References alone cannot prove
that two real cash movements are the same movement; retain them as evidence, not a universal unique key.

## Credit classification and capacity

`PostSupplierCredit` requires explicit claims against authentic posted structured bill components on
the same PO. A claim identifies its bill revision and recognition provenance; a caller cannot choose
an arbitrary expense, inventory, prepayment or tax account. Original bills and recognition events stay
intact. A credit reduces recognized charges without reversing recognition quantities or marking goods
returned operationally. A physical return alone never posts a financial credit.

Derive the account treatment from the surviving BK-04 recognition state:

- Recognized expense reductions credit the original expense account.
- Unrecognized eligible invoice-first costs credit the original prepayment account. Matching later
  recognition must use the net surviving cost capacity and include the credit dependency; otherwise
  it must reject rather than release the original gross prepayment again.
- Inventory reductions require durable owner-provided evidence of the remaining eligible asset and
  amount at the posting date and affected later dates. Caller assertions such as `stillHeld=true`
  are insufficient. BK-08 supplies the typed participant contract and rejection path; until an
  authentic inventory owner supplies that evidence, inventory-credit posting is unavailable.
- Recoverable-tax reductions require an explicit approved policy/evidence revision and remaining
  reducible tax position. Preserve original tax attribution. Previously recovered, remitted or
  otherwise unsupported tax positions reject until an owning adapter supports their actual treatment.
  Nonrecoverable tax remains part of its original cost treatment.

Do not guess cost-of-sales, disposal, tax-payable or other replacement classifications for sold,
consumed, returned or impaired inventory. These unsupported positions remain a release limitation,
not a credit into an account whose balance happens to be sufficient. The expense-only worked example
is deliverable without claiming a production inventory or tax-position subsystem exists.

For every original component, the sum of surviving credit claims cannot exceed its supported cost
or tax capacity at any affected posting boundary. Follow the source across correction generations;
revising the same logical claim does not reset capacity. A reversal releases capacity only from its
posting date. Validate under the same source locks as the write, including competing credits, bill
corrections and recognition matching. Reject inconsistent, negative or excess claims atomically.

All input amounts are exact decimal strings representable in `decimal(28,4)` and the configured
currency scale. Components must sum exactly to a strictly positive credit total. Reference original
approved rounding attribution when reducing it; do not introduce a balancing plug or another
one-minor-unit allowance. Claims and allocations are explicit, with no automatic proportional split.

## Credit posting and allocation

Post the complete credit as a CreditReceivable asset first: debit configured SupplierCreditReceivable,
credit the validated original cost/prepayment/tax classifications. Optionally apply explicit amounts
to posted structured bills in the same transaction: debit the target's historical SupplierPayable,
credit the credit item's historical SupplierCreditReceivable. Separate balanced journals are allowed;
all effects share one group and recorded instant. Readers cannot see an intermediate receivable.

This gross-capacity approach reuses BK-06 applications and preserves credit capacity even when fully
allocated immediately. Every control line is attributed exactly once; omit zero lines. No cash line
appears. An unallocated remainder is a supplier receivable. Never silently apply it based on the
original bill's current unpaid balance. Explicit allocations may target another bill on the same PO,
but cannot exceed its debt or the credit capacity at any affected date.

`ApplySupplierFunds` continues to serve Advance/CreditReceivable-to-bill applications. Add a narrowly
typed internal settlement participant for CreditReceivable/Advance-to-RefundClearing reapplication
within source corrections. Do not weaken the ordinary command's current structured-bill validation
to accept an arbitrary caller-selected debt kind. Both paths share availability and attribution rules.

## Actual refunds and repayments

`RecordSupplierRefund` records money already received, never initiates it. It consumes one or more
explicit Advance or CreditReceivable items on the same PO/currency. The settlement amounts equal
the cash amount exactly; reject an unidentified excess instead of inventing an asset or liability.
Partial and repeated refunds are allowed only within remaining capacity across all relevant dates.

Debit the selected active Bank/Cash account once for the gross receipt; credit each consumed item's
historical Advance/CreditReceivable control. A return of a deposit does not credit expense and does
not create a credit note. A refund of a credit does not reduce cost for a second time. Record exact
source/settlement/movement/control links; method labels never select accounts. Reject card accounts,
unsupported currencies and arbitrary general accounts.

`RecordSupplierRefundRepayment` records actual money sent back against explicit RefundClearing items:
debit their historical liability controls and credit the selected active Bank/Cash account once.
The positive settlement total must equal the positive cash amount and fit historical liability
capacity. This is not a new supplier purchase payment, cost, credit or automatic refund correction.

Actual and effective cash dates cannot follow posting date. Settlement cannot predate the source
item, and all posting dates must be open and within accounting-start policy. A Bank/Cash selection
is captured with its current validated version; a refund need not use the original outgoing account.
Historical settlement controls come from authentic item evidence, not today's mappings.

## Complete source corrections

Provide source-owned preview and execution for `CorrectSupplierBill`, `CorrectSupplierCredit`,
`CorrectSupplierRefund` and `CorrectSupplierRefundRepayment`. Preview takes correction intent,
expected source/PO/configuration versions, an eligible posting date, reason and explicit replacement
and reapplication decisions. It discovers the complete dependency graph; a caller's list is not
proof that no other dependency exists. Return deterministic fingerprint, all affected versions,
source/item/application/settlement identities, proposed journals and remaining open positions.

Execution repeats discovery and validation under locks. A stale fingerprint or changed dependency
conflicts without partial effects. A preview reserves nothing. Bound the entire closure and combined
input to 1,000 events and 262,144 bytes, including nested replacements; reject oversized closures
without truncating or splitting an atomic correction.

### Bills and credits

Unwind affected applications, then reverse/replace the owned source and reapply explicit eligible
amounts. Discover recognition matches, component claims, credits and cash settlements as well as
ordinary allocations. Any dependent credit that must change requires its explicit successor/reversal
decision in the same closure. Reject unsupported inventory, tax, reconciliation or other owner
dependencies instead of bypassing them through generic recognition/journal correction.

A bill replacement is a fresh successor bill identity/revision, with reviewed components and fresh
recognition successor units linked from the original bill correction. Preserve supplier/PO/currency,
original effective dates and immutable prior evidence. Discover and correct the full matched
recognition group under BK-04 rules. Apply replacement funds to the successor bill/item explicitly;
the original item and bill remain historically addressable. This adds a source-owned successor path;
it does not reopen the original posted bill for editing.

A credit replacement similarly gets a fresh successor credit identity/revision and validated
component claims. Capacity validation considers the complete proposed group, including released
original claims and dependent source replacements. Generic correction routes continue to reject
all owned bill/credit/cash journals, including aliases or caller-invented source-kind strings.

Preserve actual payment and refund sources and their cash lines during bill/credit corrections:

- For a payment embedded in the original payment journal, unapply its bill settlement through
  debit Advances / credit AP; do not invert the payment journal or also reverse the same application
  as a standalone journal. Later standalone applications use their own exact inverse.
- For an actual refund already consuming the corrected credit, release its settlement through debit
  CreditReceivable / credit RefundClearing. The release restores credit capacity for source reversal
  and opens an explicit liability for money already received. Keep the original Bank debit intact.
- Reapply the successor credit to the clearing liability through debit RefundClearing / credit
  CreditReceivable. Any excess liability remains owed to the supplier until an actual repayment.

Apply the same preservation rule when a corrected payment's advance has already been refunded: the
payment correction must discover the refund dependency and either resolve it through this typed
participant with explicit intent or reject. Existing payment correction cannot ignore BK-08 effects.

### Cash recording errors

Refund/repayment corrections repair an incorrectly recorded cash fact. Reverse its exact historical
cash and control effects and optionally post a fresh successor source with explicit settlements.
This is distinct from recording another real receipt or repayment. Discover settlement releases,
clearing applications, later repayments and further corrections; require complete explicit decisions
for each affected source. Unsupported closure rejects. A matched bank entry will require a future
reconciliation-resolution adapter before correction; there is no implicit unmatching.

Every inverse uses original journal account identities and exact amounts. New source classification
uses the validated successor state; allocation uses authentic historical control identities. All
inverse, replacement and reapplication effects use one eligible open posting date no earlier than
any reversed event or participating source. Preserve original effective dates and leave closed-period
balances unchanged. One database-owned recorded instant covers the entire committed group. No new
cash movement is present in bill/credit correction groups.

## Authority, recovery and verification contract

The linked [authority, recovery and verification contract](2026-10-19-bk-08-verification-contract.md)
is a required part of this specification. It defines permissions, transaction/retry behavior,
evidence retention, readback, migration/recovery and the acceptance matrix for this design.

## Risks and completion boundaries

Dependency closure crosses bill recognition, credit component capacity, allocations and cash
settlements. Keep ownership checks explicit and reject unsupported closures. Existing supplier
report capture scales with tenant history; this change makes no unmeasured throughput claim and
does not add unrelated projection or caching work. Database administrators remain able to bypass
application controls; preserve existing exceptional-repair and reconciliation procedures.

No production inventory/tax-position owner is established by this story. Consequently unsupported
inventory credits, recovered-tax adjustments and complete-statement coverage remain blocked for
activation. The internal expense/prepayment and supported tax-policy paths must identify exactly
which evidence they validate. Public PO-07 entry, BK-09 release reconciliation/close, BK-10 cutover,
BK-11 bank/card matching and BK-12 fees/cards retain their independent completion gates.

Before implementation, the user reviews this specification, then the implementation plan and
execution method under the [development workflow](../development-workflow.md#design-and-planning).
During implementation update Accounting, Architecture, PO-07 status, migration/principal/recovery
guidance and dated verification evidence to describe what actually shipped. Design-only validation
checks links, source consistency and arithmetic; it does not establish runtime correctness.
