# PO-07: ledger-backed purchase payments and bookkeeping prerequisites

**Status:** Public PO-07 workflow remains proposed; BK-01–06 prerequisites have separate approved
implementation boundaries described below. Remaining stories require their own approval and release
evidence. This replaces the unimplemented aggregate "Confirm supplier total" proposal and does not
activate production bookkeeping.

The [BK-01 implementation specification](2026-09-21-bk-01-accounting-foundation.md) records the
subsequently agreed product policies and proposes the bounded configuration/accounts/authorization
delivery. Its role-based authorization replaces the original persona-based proposal below; the
remaining PO-07 stories are not implicitly approved by those policy decisions.

The implemented [BK-02 atomic journal foundation](../ARCHITECTURE.md#atomic-journal-foundation) defines the
database posting boundary, first-posting protections and basic journal/trial-balance readback.
It does not enable production bookkeeping.

The implemented [BK-05 supplier bill foundation](../accounting.md#internal-supplier-bills)
adds internal immutable revisions, review, duplicate resolution and source-derived invoice posting
through BK-04. Public bill entry, runtime mutation grants and bill corrections remain unavailable;
this delivery does not enable production bookkeeping or payments. The later
[BK-07 boundary](../accounting.md#durable-financial-evidence) supplies posted evidence holds.

The implemented [BK-06 supplier subledger](2026-09-27-bk-06-supplier-open-items-and-allocations.md)
adds internal payment/allocation/reversal/correction commands and authorized open-item/history and
four-control reconciliation reads. Posting-date and recorded-time cutoffs and continuation ceilings
preserve historical readback. Targeted SQL/HTTP, supported-base upgrade and guarded SQL recovery
evidence exists; complete-branch review, full release gates and retained-preview inspection are still
pending. Runtime financial mutation grants, production write-permission assignments and public
financial-write UI/routes remain absent. `BookkeepingAvailable` stays false. Future source-owned
bill/credit correction participants have disposable tests only; BK-07 retention, BK-08 business
sources and the remaining PO-07 release stories are not delivered by this increment.

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
- [Purchase invoice files](../purchasing.md#keep-invoice-files-with-an-ordered-purchase) implement the PO-06 attachment scope only.
  Attaching files creates no financial effects. BK-04–06 now supply separate internal recognition, structured bill,
  payment and allocation foundations; they do not enable the public bookkeeping workflow below.
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

These are prerequisite delivery stories; the linked implementation updates above identify delivered
internal boundaries and remaining gates. The table itself is not evidence that a story has shipped.
Acceptance is observable and each story needs its own bounded implementation spec where indicated. **Core**
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
payment table and retrofit the journal later. BK-01–06 now have separate approved implementation
boundaries; unfinished stories still need their own design and release approvals. Separately
authorized follow-up design issues are linked from BK-01;
this specification does not itself authorize further collaboration writes or production changes.

## Journal and source contract

[Read this section](2026-09-20-po-07-journal-contract.md#journal-and-source-contract).

### Complete-statement coverage before cutover

[Read this section](2026-09-20-po-07-journal-contract.md#complete-statement-coverage-before-cutover).

### Posting and correction contract

[Read this section](2026-09-20-po-07-journal-contract.md#posting-and-correction-contract).

## Recognition and example postings

[Read this section](2026-09-20-po-07-journal-contract.md#recognition-and-example-postings).

## PO-07 after the prerequisites

[Read this section](2026-09-20-po-07-workflow-and-release.md#po-07-after-the-prerequisites).

### Allocation dates and historical availability

[Read this section](2026-09-20-po-07-workflow-and-release.md#allocation-dates-and-historical-availability).

## Integrity, security, API and evidence retention

[Read this section](2026-09-20-po-07-workflow-and-release.md#integrity-security-api-and-evidence-retention).

## Compatibility, migration and release

[Read this section](2026-09-20-po-07-workflow-and-release.md#compatibility-migration-and-release).

## Verification and completion criteria

[Read this section](2026-09-20-po-07-workflow-and-release.md#verification-and-completion-criteria).
