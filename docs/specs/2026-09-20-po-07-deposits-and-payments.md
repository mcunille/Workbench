# PO-07: ledger-backed purchase payments and bookkeeping prerequisites

**Status:** Proposed — double-entry direction requested by the owner; detailed policies and
prerequisite implementation require approval. This replaces the unimplemented aggregate
"Confirm supplier total" proposal. No runtime or schema changes are delivered by this design.

The [BK-01 implementation specification](2026-09-21-bk-01-accounting-foundation.md) records the
subsequently agreed product policies and proposes the bounded configuration/accounts/authorization
delivery. Its role-based authorization replaces the original persona-based proposal below; the
remaining PO-07 stories are not implicitly approved by those policy decisions.

The implemented [BK-02 atomic journal design](2026-09-23-bk-02-atomic-journal.md) defines the
database posting boundary, first-posting protections and basic journal/trial-balance readback.
It does not enable production bookkeeping.

## Decision and evidence

Build one double-entry general ledger with a supplier subledger. Purchasing owns source documents
and events; accounting owns balanced, immutable postings. The subledger explains general-ledger
control balances by supplier, purchase, bill and allocation. It is not a second independently
editable ledger. A balanced journal is necessary but insufficient: recognition, classification,
completeness, authorization and reconciliation must also be correct.

The owner requires dependable bookkeeping from the first financial feature. Existing Workbench
[principles](../DESIGN-PRINCIPLES.md#4-use-ledger-semantics-where-financial-truth-requires-them)
already require explainable corrections and closed-period protection. Current
[architecture](../ARCHITECTURE.md) and source establish:

- PO-04 commits operational contents and preserves amendments; it creates no journal.
- PO-05 uses four-place exact estimates and separates supplier and third-party charges.
- [PO-06](2026-09-18-po-06-invoices-and-purchase-documents.md) implements private files only.
  Structured bills, liability recognition and payment allocation do not exist.
- `PurchaseOrderEndpoints.cs` and `PurchaseOrderContracts.cs` own commitment/revision APIs;
  `PurchaseOrderDocumentService.cs` owns purchase document storage. Extend their source boundaries,
  not their estimates into an accounting balance.

Commitment, an uploaded invoice, goods receipt, liability recognition, payment and allocation are
separate events. A PDF or PO amendment must never implicitly post or rewrite accounting.

## Scope and policy decisions

Recommend an accrual bookkeeping foundation, initially one functional currency per business and
only same-currency posting. Operational POs in other currencies remain possible; accounting
posting rejects them until PO-15 supplies transaction/functional amounts, FX rates, remeasurement,
settlement gains/losses and foreign-currency reconciliation. Do not aggregate foreign amounts or
silently declare the first PO currency to be the business currency.

Double-entry does not alone decide reporting views or the correct recognition date. The BK-01
discussion established the following product boundaries; business-specific values and the bounded
implementation choices remain explicit in its linked specification:

| Decision | Recommended boundary / remaining decision |
| --- | --- |
| Reporting framework and jurisdiction | Country and state/region are business configuration; authorized users record applicable framework and tax-policy decisions. Jurisdiction selection alone does not enable tax calculations or assert IFRS, GAAP or tax compliance. |
| Accounting basis | Accrual general ledger; eventual accrual and cash-basis reporting views are a separate design and never rewrite posted transactions. |
| Functional currency and precision | Explicit business choice and supported currency scale before first posting; no change after postings in this release. |
| Recognition and classification | Explicit expense, inventory/asset, prepayment, recoverable tax and nonrecoverable tax mappings. Define ownership/control transfer and uninvoiced receipt treatment before inventory purchases can post. |
| Cutover | Enter complete history from a genuine zero-balance beginning, or use reconciled opening balances and outstanding supplier items at a dated cutover. An empty database is not evidence of no prior activity. Never infer history from existing POs. |
| Authority | Owner and Bookkeeper are personas, not authorization rules. Assignable roles supply explicit permissions for business actions, accounting configuration, reconciliation, period controls, reporting, and role administration. Authorized business actions create their required journal effects automatically; there is no extra blanket posting permission. |
| Record retention | Preserve posted accounting history and archive accounts instead of deleting history. Configure supporting-document retention separately and enforce it with holds under BK-07 before real bookkeeping use; existing seven-day removal is insufficient as the accounting policy. |

Reference grounding: the [IFRS Conceptual Framework, paragraph 1.17](https://www.ifrs.org/content/dam/ifrs/publications/pdf-standards/english/2022/issued/part-a/conceptual-framework-for-financial-reporting.pdf?bypass=on)
distinguishes accrual effects from the timing of cash flows. [IAS 21, paragraph 21](https://www.ifrs.org/content/dam/ifrs/publications/pdf-standards/english/2024/issued/part-a/ias-21-the-effects-of-changes-in-foreign-exchange-rates.pdf?bypass=on)
requires functional-currency translation for initial foreign-currency recognition. These support
the proposed separation and explain why FX cannot be implemented by relabeling amounts; the
applicable reporting framework remains an owner/bookkeeper decision.

## Prerequisite user stories

These are proposed delivery stories, not filed issues or claims of implementation. Acceptance is
observable and each story needs its own bounded implementation spec where indicated. **Core**
blocks completion of PO-07; **Release** can follow its development but blocks real bookkeeping use.
The inventory recognition portion of BK-04 is Core for the intended gemstone purchasing scenario.

| ID / gate | User story | Observable acceptance | Depends on |
| --- | --- | --- | --- |
| BK-01 / Core | As an owner, I want explicit accounting policies, accounts and permissions so that postings have a stable meaning. | Approve the bounded [BK-01 design](2026-09-21-bk-01-accounting-foundation.md); configure jurisdiction, functional currency, scale, fiscal calendar and start/cutover plan; create general typed asset/liability/equity/income/expense accounts and controlled mappings; configure bank/cash and supplier control accounts; inventory complete-statement transaction coverage; archive accounts without deleting history; enforce role-derived permissions without persona checks or a blanket posting permission. | Design approval |
| BK-02 / Core | As a bookkeeper, I want an atomic double-entry journal so that every recorded event is balanced and traceable. | Every posted journal balances exactly, has at least two nonzero lines, approved accounts and a unique source-event identity; journal/source/receipt commit together; direct runtime mutation and unbalanced posting fail at the database boundary; retries and replica races create one result. | BK-01 |
| BK-03 / Core | As a bookkeeper, I want corrections and period controls so that historical balances cannot be silently changed. | Distinguish document/effective/posting dates and UTC recording time; reject closed-period postings; atomically reverse/repost with a reason and linked evidence; preserve source revisions; serialize close-versus-post races; corrections after close use an open period. | BK-02 |
| BK-04 / Core | As a bookkeeper, I want classified purchase recognition so that purchases affect the right accounts at the right time. | Define and test expense/prepayment/inventory/tax rules, invoice-before-receipt and receipt-before-invoice cases; independent recognition events prevent invoice and receipt double-counting; every component and rounding difference has an approved treatment; missing mappings block posting. | BK-01–03 |
| BK-05 / Core (structured PO-06) | As an owner, I want structured supplier bills linked to private evidence so that liabilities come from reviewed source records. | Draft/post bills with supplier, reference, document and posting dates, due date/terms, currency, classified components and exact total; support multiple bills per PO; flag normalized duplicate supplier references and require recorded resolution; posted bills are immutable; pro forma requests create no automatic payable. | BK-02–04 |
| BK-06 / Core | As a bookkeeper, I want supplier open items and allocations so that deposits and liabilities remain distinguishable. | Supplier/PO/bill subledger reconciles exactly to AP, advances, supplier-credit and refund-clearing controls; partial allocations cannot exceed available advance or open debt; reversal releases allocations through new events; concurrent allocations cannot overspend the same amount. | BK-02–05 |
| BK-07 / Core | As an owner, I want durable financial evidence so that corrections and recovery retain an audit trail. | Posted sources preserve supplier/account/mapping snapshots and private document revision/digest links; retained evidence cannot be removed through ordinary PO-file deletion; holds cover physical cleanup and recovery; permission loss revokes access without erasing evidence; configured retention is enforced. | BK-01–03; existing PO-06 storage |
| BK-08 / Core (financial PO-10) | As a bookkeeper, I want supplier credits and refunds so that overpayments and exceptions reconcile without false payments. | Post credit notes and refunds separately, allocate credits to bills or retain an explicit supplier receivable, return unused deposits, preserve the original entries, and distinguish corrections from actual money returned; the worked example below reconciles. | BK-04–07; payment source contract |
| BK-09 / Core readback, Release close | As a bookkeeper, I want a trial balance, account journal and supplier reconciliation so that I can prove balances and close a period. | Readback/export trace every amount to immutable source/journal IDs; debits equal credits; subledger totals equal control accounts at the same cutoff; close requires resolved discrepancies and retained reconciliation evidence; chronological and as-recorded views include later corrections. | Readback: BK-02–08; close: BK-10–11 |
| BK-10 / Release | As an owner, I want a reconciled cutover so that existing cash, liabilities and advances are not invented or omitted. | Approve a balanced opening journal with source schedule; opening supplier items equal control balances and opening bank balances match evidence; revalidate complete-statement transaction coverage before activation; prevent duplicate imports and pre-cutover reposting; a clean start explicitly confirms no opening items. | BK-01–03, BK-06, BK-09 readback |
| BK-11 / Release | As a bookkeeper, I want bank/cash/card reconciliation so that recorded payments match actual money movement and card debt. | Manual statement opening/closing balances and dated matching show outstanding items and zero unexplained difference; matching does not repost a payment; duplicates and reversal of matched payments require explicit resolution; retain statement evidence; reconcile card liabilities separately from bank balances. | BK-06–08, BK-09 readback, BK-10, BK-12, PO-07 payment recording |
| BK-12 / Core for fees/cards, Release otherwise | As a bookkeeper, I want controlled non-supplier cash entries so that bank fees, transfers and card settlement do not become fictitious supplier payments. | Typed sources record bank fees, bank-to-bank transfers, card-liability settlements and approved owner funding/withdrawals with configured account mappings and evidence; both sides of a transfer post atomically; prevent duplicate settlement; never write AP/advance/supplier-credit controls through these commands; unsupported statement transactions remain unresolved and block reconciliation. | BK-01–03, BK-07 |

BK-08 consumes the shared payment source contract designed with BK-06; it need not wait for the
finished PO-07 UI. BK-09 is split: journal/trial-balance readback ships with BK-02, supplier controls
with BK-06, and financial close acceptance after BK-08. BK-03 supplies the locking/closed-period
foundation; the close action remains unavailable until BK-09's checklist, BK-10 cutover and BK-11
bank/cash/card reconciliation are implemented. BK-10 and BK-11 depend on BK-09 readback, not its
completed close action. These
splits avoid a dependency cycle and make the foundation verifiable before payments ship.

### Recommended delivery order

1. Approve BK-01 policies and the journal/source contract; implement BK-01 and BK-02 with basic
   trial-balance readback. Use synthetic data until all release gates pass.
2. Deliver BK-03 and BK-07; settle BK-04 recognition in its own design before purchase posting.
3. Deliver structured bills (BK-05), subledger/allocation/payment source primitives (BK-06),
   financial credits/refunds (BK-08), and bank/card source entries (BK-12), with BK-09 reconciliation
   expanded alongside them.
4. Deliver PO-07's user workflow below, including persisted end-to-end evidence.
5. Finish BK-10 cutover and BK-11 reconciliation, then BK-09 close before production bookkeeping use.

Implement these as focused PRs with additive migrations. Do not implement PO-07 as a standalone
payment table and retrofit the journal later. The next implementation story is BK-01, after its bounded
design is approved, not PO-07. Separately authorized follow-up design issues are linked from BK-01;
this specification does not itself authorize further collaboration writes or production changes.

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

## PO-07 after the prerequisites

**User story:** As an owner, I want to record a deposit and later supplier payments with their
receipts and allocations, so that I can distinguish what I have paid from what remains owed.

Record actual payment date, posting date, amount, currency, method, funding account, supplier,
PO, optional reference/notes and private receipt. Bank/card transfer references are evidence,
not secrets. The funding account determines the credit: Bank/Cash for cash payments or a configured
credit-card liability account for card-funded payments. Card funding stays disabled until BK-12
settlement and BK-11 card reconciliation are delivered. Method labels cannot choose a guessed
account. Later settlement of a credit-card liability is a separate bank transaction, not another
supplier payment. BK-12 also owns the worked example's bank fee. No bank integration or money
movement is initiated by recording.

Allocate all or part to posted bills on this PO. An unapplied remainder is a supplier advance;
a payment split between bills and deposit debits AP and Advances respectively and credits the
funding account once. Initial scope is one supplier/PO/currency per payment. PO-17 owns cross-PO
allocation and consolidated billing. A deposit is allowed before any bill exists. Do not label
unapplied money as an invoice settlement or allow an allocation above the outstanding bill amount.

### Allocation dates and historical availability

BK-06 requires an allocation posting date in an open period, no earlier than the posting date of
either the bill or the payment/advance/credit it applies. Both sources must already be posted and
authorized when recording the allocation. A document/effective date cannot make an unposted or
future-posted source available. Allocation reversals cannot predate the original application and
closed-period applications are corrected in an open period. Reapplication also respects the
replacement source's posting date.

For a backdated allocation, validate available credit and open debt at that date and at every later
affected event boundary, including existing applications/reversals. Reject any result that would
overdraw a source or over-settle a bill at any cutoff; today's remaining balance alone is not proof.
Perform this validation under the same source/open-item locks as allocation writes. No implicit
reordering or relocation of existing applications is allowed.

Example: a 100 deposit posted September 10 and a 150 bill posted September 15 can be allocated
on September 15 or later, never September 12. An allocation of 100 dated September 16 but recorded
September 20 is allowed only while that period is open and all intervening availability checks pass.
By posting-date cutoff, September 14 shows Advances 100/AP 0; September 15 shows Advances 100/AP 150;
September 16 shows Advances 0/AP 50 once the allocation is recorded. An as-recorded report through
September 19 still shows Advances 100/AP 150 at the September 16 posting cutoff. Reports accept
both posting-date and recorded-time cutoffs and include only events satisfying both. If a September
18 allocation already consumed the same 100, the backdated request conflicts rather than rewriting
that history. BK-09 exports both dates and reproduces these views from immutable events.

The ordered purchase screen retains its existing estimate and adds a compact **Bookkeeping**
section with posted bills, credits, payments, unapplied advances and open liabilities. Entries
link to journal evidence, source documents and correction history. **Record payment** previews
the allocation and posting before explicit save. **Apply deposit**, **Record supplier credit**
and **Record refund** are distinct actions. No generic paid checkbox or confirmed-total override.

Report independently:

- Net supplier payments = actual outgoing supplier payments minus actual incoming supplier refunds,
  excluding correction pairs. Card-funded supplier payments are included; label actual bank/cash
  movement separately from payments funded by card liabilities.
- AP outstanding = recognized bills minus credit allocations minus payment/advance allocations,
  including explicit reversals. Tie exactly to the PO's AP subledger.
- Unapplied advances and supplier credit receivables are positive assets displayed separately.
- Net supplier position = AP outstanding + refund-clearing liabilities - advances - supplier credit receivables; a negative
  result is money held by/owed from the supplier, not a negative bill balance or an automatic refund.
- Remaining purchase cost stays unknown if further bills/recognition are expected. Zero AP means
  **Recorded bills settled**, never proof the PO is complete. Uninvoiced accruals remain separately
  visible; overdue status uses bill due dates, not commitment date or deposit date.

Support overpayments as advances, partial invoices and amended POs without rewriting postings.
Once accounting evidence exists, changing the PO's supplier identity is rejected through every
amendment path; currency is already fixed for ordered purchases. Supplier contact edits do not
change posted snapshots. Misallocated posted purchases need an explicit reversal/replacement
workflow and cannot be repaired by editing a PO name. Show unlinked operational/financial changes
for review rather than silently forcing one to match the other.

Keep unsaved forms through validation and conflict failures. Retain the exact request identity
while outcome is uncertain; check/retry it before replacing a command. Successful writes followed
by failed reads retry only the read. Guard navigation and clear private state on authentication
loss. Reuse the established Tanzanite components, keyboard/focus behavior, responsive layouts,
light/dark themes and accessible status feedback; this design changes no visual system.

## Integrity, security, API and evidence retention

Use tenant-qualified keys and RLS for every account, source, journal, allocation and evidence link.
Validate authority, tenant, source state, mapping version, period and expected source/PO versions
inside restricted SQL transactions. Tenant/operation/request UUID plus normalized payload defines
a command; a unique source-event key also prevents reposting the same event under another UUID.
Authenticate before replay; exact retries return the original authorized result, changed payloads
conflict. Missing and foreign IDs have indistinguishable 404s. Never accept an arbitrary ledger
account or tenant supplied by an untrusted client without policy validation.

Establish one lock order for ALL financial commands: tenant accounting policy/period coordination,
then affected PO/source rows in stable ID order, then open items/allocations, then evidence records.
Use compatible period posting locks and an exclusive close lock, so close checks and postings cannot
race. Configuration/mapping changes, account archival, retries, corrections, document removal and
legacy PO amendment paths must participate in the relevant ordering. Independent-connection tests
must prove no lost posting, overspent allocation or journal after a completed close. Do not use
in-process locks as cross-replica protection. Deadlock retries reuse the same command identity.

SQL procedures construct/validate complete balanced journals before commit. Deny runtime direct
DML, constrain line shapes and source uniqueness, and protect posted data from update/delete.
A privileged migration/operator can bypass application controls; log exceptional repair actions,
reconcile afterward, and do not describe the ledger as tamper-proof against database administrators.

Proposed additive `/api/beta/accounting` resources cover configuration, accounts, periods, journal
readback, reconciliation and command status. Purchasing source endpoints own bills, payments,
applications, credits, refunds and correction commands. PO finance GET returns a consistent
ledger-derived summary plus versions and completeness indicators, with bounded paged histories.
Every write carries requestId and expected versions, uses antiforgery and existing Problem Details
semantics, and returns immutable source/journal IDs. Final schemas belong to each prerequisite spec;
no `/confirmations` endpoint or mutable payment-balance contract is retained from the old proposal.

BK-07 adds accounting holds to existing immutable document revisions. Linking a posted source pins
its bytes/digest and metadata against ordinary removal/garbage collection; unposting/reversal does
not lift the hold. Replacing a document appends linked evidence, retaining the original. Removal
races must either acquire the hold before removal or reject posting with an unavailable document.
Uploads finish through the existing private pipeline before financial posting; a failed posting
leaves an unposted file, not a partial journal. Metadata-only financial records remain permitted
when no receipt exists, with explicit missing-evidence status. Account for backup retention and
missing-file recovery without dropping the journal or claiming missing bytes are available.

## Compatibility, migration and release

Existing collection, PO estimates, commitments and invoice files remain accessible without accounting
setup. No automatic journal backfill, synthetic opening balances or bulk expense classification.
BK-10 explicitly decides which existing sources become opening items and prevents reposting them.

Use one forward migration per coherent prerequisite release; consolidate only unapplied development
migrations. Preserve base/retained histories, source receipts and private storage. Update readiness,
provisioning, direct-DML denial, schema markers and supported backup restore paths with each change.
Test clean creation, upgrade from each PR base and paired recovery with journal/source/attachment
integrity. Prevent old binaries or procedures from mutating sources in ways that bypass new locks
or posted-state invariants. Destructive ledger down-migrations are blocked; rollback is a compatible
binary, forward correction, or verified coordinated recovery, never deletion of financial history.

No production bookkeeping activation until the release stories pass, the opening schedule is
approved, and a representative purchase is reconciled with source and bank evidence. This is a
purchase bookkeeping foundation, not complete sales accounting, tax filing, inventory valuation,
statutory financial statements or year-end closing. Surface those coverage limits in reports.

## Verification and completion criteria

Each implementation story uses test-first behavior changes, Gherkin comments and targeted mutation
checks. Validate database authority and concurrency on real SQL connections; component tests alone
cannot establish journal integrity. Required scenarios include:

1. Journal imbalance, excess scale, overflow, disabled account, unauthorized role, cross-tenant
   evidence, duplicate source, closed period and changed request payload all fail without partial
   source/journal/receipt writes. Race posting with close, corrections, allocations and removal.
2. Before billing, the USD 100 deposit yields Advances 100, Bank -100, AP 0; recorded-bill balance
   is zero but final obligation is unknown. The recognized USD 306.60 bill and deposit application
   yield AP 206.60 and Advances 0. Paying 206.60 yields AP 0 and supplier payments 306.60.
3. The separately paid USD 3 bank fee does not change supplier AP/payments. An USD 18 supplier credit
   followed by refund yields supplier payments 288.60, supplier receivable 0 and net bank outflow
   291.60 including the fee. Purchase debits net to 288.60 before the bank fee. All journals balance.
4. Partial applications, excess deposits, returned advances, unpaid/part-paid bill credits, card
   funding and repeated refunds respect available balances. No positive/negative amount shortcut
   bypasses typed event rules. Reversal and replacement retain exact history and release allocations.
5. Invoice-before-receipt and receipt-before-invoice follow BK-04 without duplicate recognition.
   Credits after consumption/sale use the approved classification. Unrecognized/unsupported cases
   cannot produce a superficially settled purchase.
6. Trial balance and supplier control reconciliation match at defined posting-date and recorded-time
   cutoffs, including closed-period corrections and opening items. A deliberately mismatched control
   projection blocks close and rebuilding it from the journal restores agreement.
7. Recovery preserves journals, source snapshots, permissions, request receipts and held evidence;
   a missing receipt is visible. Existing non-accounting workflows still work without configuration.
8. Exercise the persisted payment/bill/credit/refund workflow in the preview and reconcile it to
   source documents; test keyboard, narrow viewports and themes. Run repository verification and
   container gates for each application change, and independently review accounting invariants.

9. Exercise both correction examples above: paid bill 300 to 280 leaves Advances 20 and no new cash;
   refunded credit 18 to 10 leaves a supplier liability of 8 and preserves the cash receipt. Repeat
   across a closed-period boundary and verify dependent application history and reconciliation.
10. Reproduce the September allocation cutoffs above, including a competing later allocation and
    closed-period rejection. BK-01/BK-10 coverage checks reject a complete statement containing an
    unsupported transaction even when its supplier-payment subset reconciles.

For this design-only change, check document links, dependencies, arithmetic and consistency with
current contracts. No application, mutation, migration or browser tests are claimed. Design approval
precedes runtime implementation; implemented stories then ship through verified ready-for-review PRs.
