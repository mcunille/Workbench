# BK-04: classified purchase recognition

**Status:** Proposed for written-spec review. The owner approved the internal-foundation scope;
the detailed policies below and the subsequent implementation plan still require review.
No runtime implementation or production bookkeeping activation is claimed.

Parent: [PO-07 bookkeeping prerequisites](2026-09-20-po-07-deposits-and-payments.md).
Prerequisites: [BK-01](2026-09-21-bk-01-accounting-foundation.md),
[BK-02](2026-09-23-bk-02-atomic-journal.md), and
[BK-03](2026-09-24-bk-03-corrections-and-period-controls.md).
Implementation baseline inspected: `a2320a794ac6328be828b96e101aa2063d6383be`.

## Outcome and delivery boundary

Give purchasing a typed, database-enforced recognition boundary that records the right cost once,
regardless of whether recognition evidence or an eligible supplier invoice arrives first. Retain
the evidence, classification, mapping revisions, dates and matching relationships that explain it.
An invoice upload, PO commitment, shipment status or physical arrival alone never authorizes posting.

Deliver internal recognition records, restricted SQL commands, explicit component classification,
matching and correction rules, journal readback and real-SQL verification. Include the minimal
receipt-recognition slice needed for inventory; do not claim that operational PO-09 fulfillment,
item valuation, inventory movements or cost of sales have been implemented.

Keep `BookkeepingAvailable` false. No public financial write endpoint, bill-entry UI, payment,
allocation, supplier credit/refund, production close or activation override ships here. BK-05 owns
reviewed structured bills; BK-06 owns supplier open items and applications; BK-07 owns physical
evidence holds. Disposable test adapters exercise the internal commands and are absent from
production migrations, grants and published output. A future production adapter must validate its
own durable source and permission; accepting a caller's claim that an invoice was reviewed is not
that adapter.

## Approach and alternatives

Use explicit recognition units with two independently evidenced sides: cost recognition and
invoice liability. Match them by stable identity, never by equal totals or a PO's current estimate.
The journal kernel remains the only journal writer, inside the source transaction.

An invoice-only policy is smaller but cannot establish control transfer or prevent double-counting
uninvoiced receipts. Building the full bill/receiving UI now would mix BK-05 and operational PO-09
with this financial boundary. The selected internal foundation establishes the accounting contract
and tests it before exposing those workflows.

## Recognition policy version 1

These are proposed supported product rules, not a claim of jurisdictional or tax compliance.
The business records its framework/tax-policy rationale through BK-01 configuration. No country,
supplier, document label or account name automatically determines recoverability or recognition.

| Classification | Required recognition evidence | Treatment |
| --- | --- | --- |
| Expense | Identified goods/services consumed or services performed, effective date and reviewer rationale | Debit mapped Expense. |
| Inventory | Identified goods, quantity/unit, documented control-transfer basis and date, including whether goods are in transit | Debit mapped Inventory; physical possession alone is insufficient. |
| Prepayment | Eligible invoice establishes a present obligation and an identifiable enforceable right to future goods/services, with explicit rationale | Debit mapped Prepayment until the related recognition event. A demand or pro forma alone fails eligibility. |
| Recoverable tax | Explicit tax-policy reference, eligible invoice evidence, reviewed recoverable amount and confirmation that entitlement exists on the selected effective date | Debit mapped RecoverableTax at invoice liability recognition; never infer a rate or entitlement. |
| Nonrecoverable tax | Explicit amount assigned to an expense or inventory component | Include in that component's cost, including its prepayment balance before recognition. |

Service recognition needs a performed-service description and covered dates. Inventory recognition
needs a stable goods reference, positive quantity in a named unit and control-transfer evidence.
Store private bounded evidence text and optional document revision/digest references; those
references do not claim BK-07 retention. Document absence is explicit, not replaced by fabricated
attachment metadata. Unsupported eligibility, uncertain tax entitlement, fixed assets/depreciation,
consignment, FX and automatic time-based expense amortization reject rather than fall back to
Expense. A later supported rule requires its own version and acceptance cases.

Each unit's eventual cost classification is Expense or Inventory. Prepayment is its intermediate
state before that cost qualifies for recognition, not a cash deposit or a third final classification.
Supplier cash deposits continue to belong to SupplierAdvance under BK-06/PO-07.

## Recognition units and immutable evidence

A unit identifies tenant, supplier, ordered PO, PO revision, currency, stable source-component key,
classification, quantity/unit when applicable and a policy version. It has at most one active
recognition side and one active invoice side. Multiple units permit mixed classifications and
partial fulfillment/invoicing without matching an entire PO at once.

Each side appends an immutable event revision with document/effective/posting dates, database UTC
recording time, actor, evidence snapshot/digest, exact classified components and journal links.
The invoice side additionally binds the durable upstream invoice identity/revision and component
key. Recognition binds its upstream recognition identity/revision and component key. Unique
tenant-qualified source keys prevent reposting either side under another request or unit ID.
An invoice and a recognition side may use different effective/document dates.

Units are the minimum independently matched pieces. A submitted side covers the whole unit;
amount-only partial matching against a posted side is unsupported in v1. Split known partial
deliveries or invoices into explicit units before posting. A later partial match that requires
splitting an already posted unit needs a supported correction, otherwise it rejects. Future BK-05
and PO-09 adapters must expose this limit, not silently allocate by amount or duplicate source keys.
For multiple units from one source component, retain an explicit subdivision identity and validate
aggregate quantity/amount against the trusted source revision under the same transaction. A new
subdivision ID is not permission to exceed the source.

The second side must explicitly name the unit and expected prior event revision. It verifies
supplier, PO, currency, classification and quantity compatibility; amount differences require the
variance approval described below. Matching and the second side's posting commit together. There
is no separately editable matched flag or writable recognized/remaining balance.

Use focused purchasing persistence/model units for unit identities, side events, component rows,
match links and command/correction receipts. Store both sides even when only one creates an AP
entry. Store zero-value side evidence where needed, but never create a zero-only journal. Unit
state is derived from active events, matches and correction links. Tenant-qualified foreign keys,
RLS and runtime DML denial apply throughout.

## Accounts and mappings

Reuse BK-01's Inventory, Expense, Prepayment, RecoverableTax and SupplierPayable slots. Add a
`GoodsReceivedNotInvoiced` slot requiring an active Liability/General account. This accrual is
separate from supplier AP and from all supplier controls. Require the accrual, inventory,
prepayment and recoverable-tax accounts used in one policy to have distinct identities; they must
remain separately reconcilable. Expense is already separated by its account type.

Extend the fixed mapping catalog, server validation, SQL save validation and generated client
contract together. Existing setup renders the additional slot with a readable label and eligible
account filtering. It is optional for general setup but mandatory for a receipt-first posting.
Do not automatically create an account or assign a mapping during upgrade.

Commands require an expected configuration version and capture the actual account/mapping
revisions used. Missing, archived, foreign or wrong-type/purpose mappings reject before any source
or journal write. New final classification uses current approved mappings. Clearing an earlier
receipt accrual or prepayment uses the account and amount captured by that event, even after a
mapping change; never clear a different account by reading the latest mapping. Preserve existing
used-account archival and supplier-control reassignment protections.

## Posting rules and matching

Let E be a receipt's approved estimated cost, C the invoice's final classified cost including
nonrecoverable tax, and T the explicitly recoverable tax. All are functional-currency amounts.
The invoice total is C + T, including any explicitly assigned invoice rounding component.

| Event/order | Journal |
| --- | --- |
| Recognition before invoice | Debit Expense/Inventory E; credit GoodsReceivedNotInvoiced E. |
| Invoice after recognition | Debit the original accrual E; debit/credit the recognized cost by C - E; debit RecoverableTax T; credit SupplierPayable C + T. |
| Eligible invoice before recognition | Debit Prepayment C; debit RecoverableTax T; credit SupplierPayable C + T. |
| Recognition after invoice | Debit Expense/Inventory C; credit the original Prepayment C. |

A negative variance credits the cost account; it is not a negative debit or a supplier credit note.
Omit zero lines. Reject arithmetic overflow, a negative final component cost, tax exceeding the
gross amount, or inconsistent totals. A completely zero-value side may append evidence and match
without a journal; its command receipt returns an empty journal-ID list. A nonzero side always
produces a balanced journal satisfying BK-02.

An invoice and recognition recorded together execute the same two-side rules atomically with a
single command receipt and recorded instant. Do not expose a temporary half-state at recorded-time
cutoffs. The implementation may retain both balanced journals; it must not recognize cost twice.

Invoice-first treatment requires the explicit eligibility above; otherwise reject financial
posting until the necessary evidence exists. A non-posting pro forma is left to BK-05 source
storage. Receipt-first estimates require a documented basis, never an automatic copy of a mutable
PO estimate. Recoverable tax is not accrued from an uninvoiced receipt in v1.

For a receipt-first amount difference, the invoice reviewer records the difference, reason and
approved original cost classification. Inventory adjustment requires trusted source evidence that
the relevant inventory remains held and can receive that adjustment. Sold, consumed, returned,
impaired or otherwise unsupported inventory state rejects pending BK-08/valuation policy; a user
supplied boolean is not proof. With no production inventory adapter in BK-04, this authority is
demonstrated only by the disposable source fixture. Expense adjustments retain the original
expense account. Changing classification requires an explicit correction.

Posting the second side cannot predate the first side's posting date; both satisfy BK-03 open-period
and accounting-start checks. Effective dates may precede posting but never make an unposted side
available. Backdated independent units remain permitted within the existing journal rules.

## Components, discounts, tax and rounding

Require an explicit complete breakdown: base cost, assigned discounts, supplier freight/charges,
nonrecoverable tax, recoverable tax and any invoice rounding adjustment. Every component has a
stable identity, typed treatment and exact decimal amount. Discounts reduce identified costs;
freight and other charges need an explicit Expense/Inventory assignment. Third-party charges are
separate supplier/source events, never additions to this supplier's AP by relabeling the payee.
Mixed recoverability requires separately stated recoverable and nonrecoverable amounts.

Posted amounts must be exactly representable at the configured currency scale. Do not silently
round excess input precision. Upstream four-place estimates may be retained as evidence but the
reviewed posting breakdown must state currency-scale amounts. Multi-unit allocations are explicit
amounts whose sum equals the source; this increment does not choose allocation weights.

Permit one explicitly approved invoice rounding component per invoice source, signed and bounded
to one minor currency unit in absolute value. Assign it to a named non-tax cost component, retaining
the reason and its own identity. Larger discrepancies or unassigned differences reject. It adjusts
Expense/Inventory or Prepayment with the same recognition timing as that component; it never plugs
RecoverableTax or an unrelated expense account. Across invoice subdivisions, enforce the bound
once for the whole source, not once per unit. The final cost must remain nonnegative.

## Corrections and dependency closure

Use BK-03's append-only correction kernel through a typed recognition correction command. A
single-side independent unit can reverse or reverse/replace with a reason and expected revisions.
A matched unit requires the command to discover and lock both sides and their match, reverse in
dependency order, and optionally rebuild the two sides under the approved replacement breakdown.
Reject attempts to correct only one matched journal or to reuse the original source identity as
an unrelated new event. Preserve the original effective dates as required by BK-03.

All corrections, replacement events, match reversals/replacements, journals, receipts and audit
commit as one group with one database-owned recorded instant. Exact inverses use original journal
lines; replacement and matching lines are derived from the typed rules. If multiple BK-03 kernel
calls are needed, the outer group normalizes only this transaction's newly inserted timestamps.
Zero-journal side corrections still append immutable event and match history.

The correction date is open and no earlier than any event it reverses. A correction after closure
does not change prior closed balances. Corrections preserve actual cash movement; none exists in
this source family. Once later bill/open-item/application or inventory-disposal dependencies exist,
reject until the owning adapter implements their complete dependency closure. The internal API
does not accept an arbitrary caller-authored list as proof that there are no dependencies.

## Transaction, authority and compatibility boundaries

Each command requires an outer SQL transaction. Take BK-03's transaction-owned exclusive
`Accounting:<tenant UUID>` lock, then affected PO/source/unit rows in stable ID order, then match
and evidence rows. Recheck live actor/session authority before any replay, and expected source/PO
and configuration versions before new writes. Preserve the tenant lock rather than invent a
second concurrency scheme. Close, setup/mapping writes and every correction share this lock.

Internal commands receive trusted typed source input, never caller-authored account/line lists.
They have no runtime EXECUTE grants; only a future specifically reviewed source adapter may call
them. Disposable adapters use test-only permissions and durable fixture records for upstream
authority, capacity and dependency checks. Runtime configuration/report permission must not grant
financial write access, and no blanket posting permission is added.

The source transaction also protects the PO supplier/currency identity. On the first unit event,
retain the identity snapshot and block supplier changes through every existing amendment path.
Those paths participate in compatible locking so a financial record cannot race a supplier swap.
Other permitted operational amendments never rewrite recognition evidence or classified costs.

Use a versioned typed command and canonical bounded JSON, with a durable group receipt including
all event, match, correction and journal IDs. Follow BK-02/03 size, exact decimal, duplicate/unknown
field, UTF-16LE hash and actor-binding conventions. Check receipts before mutable account/source
validation but after current authorization. Same input replays; changed input conflicts; a different
request ID cannot bypass source uniqueness. Deadlock/timeout retries retain the request identity.
Missing/foreign source IDs are indistinguishable. Rollback removes every new side effect, including
materialized periods, after any failed component or second-side posting.

## Readback and examples

Extend authorized journal detail additively with recognition unit/event IDs, side, match and
correction-group relationships, classified components and retained evidence snapshots/digests.
Keep financial decimal serialization, tenant checks, no-store and AccountingReportsRead authority.
No public recognition write or new operational receiving screen is added. Existing journal source
snapshots remain readable after mappings and supplier contact data change.

| Scenario | Expected cumulative balances |
| --- | --- |
| Inventory receipt 100, no invoice | Inventory 100; accrual liability 100; AP 0. |
| Later invoice cost 105 plus recoverable tax 5 | Inventory 105; tax asset 5; accrual 0; AP 110. |
| Eligible invoice cost 105 plus tax 5 before recognition | Prepayment 105; tax asset 5; AP 110; Inventory 0. |
| Later recognition of that invoiced unit | Inventory 105; tax asset 5; Prepayment 0; AP 110. |
| Receipt 100, later final cost 98 | Inventory/Expense 98; accrual 0; AP 98. |
| Pro forma or unsupported tax/recognition eligibility | No financial events, journals or successful posting receipts. |

Tax-free expense examples use the same matching rules with Expense. Nonrecoverable tax is included
in C, not reported as a tax asset. A bank fee is outside these source events. Reports sum immutable
journals at both posting-date and recorded-time cutoffs; later matches/corrections must not leak
into earlier as-recorded views. Unmatched accruals and prepayments remain explicit evidence that
the purchase is incomplete. BK-06, not this increment, establishes supplier control reconciliation.

## Migration and delivery verification

Ship one additive migration from merged `20260925044758_AddAccountingPeriodControls`. Preserve
all earlier migrations, journals and receipts. Update models, RLS, principal provisioning probes,
schema/readiness markers and the migration compatibility matrix together. No automatic posting,
source backfill, invented recognition evidence, new seeded account or data deletion. Destructive
Down is blocked. Use matching binaries and the existing guarded recovery contract.

Implementation follows TDD with GIVEN/WHEN/THEN comments and the repository test-ownership rules.
Required acceptance cases include:

- Both ordering paths for Expense and Inventory, combined commands, pro forma rejection,
  uninvoiced estimates, explicit eligible prepayments and immutable source evidence.
- Recoverable/nonrecoverable/mixed tax, discounts, charges, positive/negative approved variance,
  unsupported inventory disposition, rounding bounds, overflow, scale and source-total checks.
- Missing/wrong/archived mappings; clearing historical mappings after reassignment; foreign
  source/account/match IDs; supplier changes through all amendment paths.
- Multiple units, partial-source capacity, incompatible quantities, duplicate source subdivisions,
  duplicate side and replays under another request ID; zero-journal evidence and receipts.
- Reversal-only and replacement for single and matched units; dependent-source rejection,
  closed-period corrections and posting/recorded cutoff views.
- Real independent SQL connections with controlled overlap for duplicate posting, competing
  second sides, source-capacity use, supplier amendment, correction, mapping change and close.
- Failure injection between side records, journals, matches and receipts proves full rollback;
  lost-response retries return the complete original group after current-authority validation.
- Restricted-principal direct-DML/EXECUTE denial, revoked-authority replay denial and absence of
  test adapters/permissions in shipped and re-provisioned databases.
- Fresh creation and upgrade from the PR base with retained journal/correction history and exact
  replay, destructive-Down refusal, and normal non-accounting PO workflows without setup.

Target .NET and isolated SQL mutations at classification, mapping validation, matching uniqueness,
source capacity, variance signs, rounding, replay ordering, correction closure and period guards.
Document unavailable tooling and equivalent/surviving cases; do not claim mutation coverage from
ordinary test success.

Run the full verification and container gates for implementation. Refresh this checkout's isolated
preview and inspect existing accounting setup (including the new mapping), report readback and
unaffected PO behavior. Report its URL and distinguish that inspection from disposable SQL
recognition tests; no public recognition workflow exists to exercise yet. Internal review covers
the complete change. Deliver verified implementation in a ready-for-review PR; merge and production
operations remain separately authorized.

For this specification-only stage, check links, arithmetic, scope and consistency with BK-01–03.
Runtime, mutation, migration and browser verification have not run. Written-spec approval precedes
the implementation-plan review and execution-method selection.
