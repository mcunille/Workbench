# BK-06: supplier open items and allocations

**Status:** Approved specification and implementation plan; internal commands, immutable supplier
evidence and authorized read APIs implemented. Targeted SQL/HTTP and mutation evidence is recorded
below. Complete-branch review, current-source full verification, separate hardened container smoke
and retained-preview/browser inspection remain pending. Production bookkeeping remains unavailable.

Parent: [PO-07 bookkeeping prerequisites](2026-09-20-po-07-deposits-and-payments.md).
Dependencies: [BK-02 journal](2026-09-23-bk-02-atomic-journal.md),
[BK-03 periods and corrections](2026-09-24-bk-03-corrections-and-period-controls.md),
[BK-04 recognition](2026-09-25-bk-04-classified-purchase-recognition.md), and
[BK-05 structured bills](2026-09-27-bk-05-structured-supplier-bills.md).

Baseline inspected: main `4140bc233b7502b84b29d08bfd165e4e31a870ff` and
[PR #183](https://github.com/mcunille/Workbench/pull/183) head
`1332972372b08c84a7ba7d8618d2bf12776a2d5c`; BK-05 was open at design time. It has since merged.
Implementation includes main `1be8631956c5025de793b3f2ea7c9e6d7bbb95db`, whose schema ends at
`20260928034802_AddSupplierBills`. BK-06 adds only `20260928071548_AddSupplierOpenItems` after that
durable base; development preview revisions do not create additional supported baselines.

## Outcome and delivery boundary

Give a bookkeeper a supplier subledger that distinguishes posted debt, money held by a supplier,
credit receivables, and refund-clearing liabilities. Every amount must trace to immutable source
and journal evidence. Partial applications and reversals must conserve value across concurrent
requests and historical cutoffs, rather than merely producing a plausible current balance.

Deliver internal payment recording, allocation, allocation reversal and payment correction;
immutable open-item evidence; integration with BK-05 bill posting; supplier control reconciliation;
and bounded authorized readback. Preserve ordinary purchasing without accounting configuration.
Keep `BookkeepingAvailable` false, runtime financial mutation grants absent, and new business-write
permissions unassigned in production. Disposable adapters exercise commands in SQL integration tests.

Do not deliver public payment/bill entry UI, public financial-write routes, cross-PO allocation,
foreign-currency accounting, bank integrations, money transfer initiation, physical evidence holds,
opening balances, bank/card reconciliation, production close, or bookkeeping activation.
BK-08 owns credit-note and actual refund/repayment sources. BK-07 owns physical evidence holds.
Card funding remains rejected until BK-11/BK-12 supply reconciliation and settlement.

The four control families exist in the model and reconciliation now. Credit and refund-clearing
source adapters are exercised synthetically; this is not delivery of BK-08 business commands.
Complete bill corrections remain a later source-owner workflow. BK-06 defines and verifies the
allocation participant contract for that workflow without opening a generic bypass around BK-05.

## Approach and alternatives

Extend Purchasing with immutable item, movement and application evidence; reuse Accounting's
existing journal kernel in one SQL transaction. Link each control effect to the actual journal
line and historical account identity. Read balances from this evidence, never from editable
paid/outstanding columns. Keep source validation, allocation planning, posting and query units
separate so each boundary can be tested without duplicating journal construction.

A journal-only balance query cannot establish which bill an advance settled or enforce remaining
source capacity. Application-maintained balances cannot protect concurrent replicas or historical
availability. Neither alternative supplies the required allocation evidence and database authority.

## Identity, evidence and amounts

Use tenant-qualified keys, foreign keys, RLS, immutable financial rows and denied runtime direct
DML. Supplier, ordered PO and currency are fixed identities for an item and its source; an
application requires all three to agree. One payment may fund multiple bills on that same PO.
Cross-PO matching belongs to PO-17. Accounting supports only the configured functional currency.

Persist these conceptual records in Purchasing:

- **Open item:** stable identity, kind, supplier/PO, source/revision identity, optional bill identity,
  source dates, due-date evidence where applicable, currency and source snapshots. Kinds are
  Payable, Advance, CreditReceivable and RefundClearing. Zero-value bills retain bill evidence but
  create no monetary item. An item remains addressable after full settlement or reversal.
- **Item movement:** signed change to one item's normal positive balance, immutable event identity,
  posting date, recorded instant, source/correction identity and journal evidence. Positive balances
  mean debt for Payable/RefundClearing and assets for Advance/CreditReceivable.
- **Application:** positive amount, funding item and debt item, date, source versions, actor and
  immutable identity. It contributes reductions to both items and corresponding control effects.
  Its full reversal is a new linked event restoring both capacities; it never deletes the original.
- **Payment source:** immutable actual payment date, effective/posting dates, amount, currency,
  method, funding-account snapshot, supplier/PO snapshot, reference, notes and optional evidence
  revisions. Preserve corrections and replacement links separately from real cash refunds.
- **Control evidence:** tenant-qualified item-event-to-journal-line attribution, including signed
  amount and historical account/mapping identity. Each control line is covered exactly once in
  aggregate, even when a combined journal represents several movements or applications.
- **Command receipt:** canonical complete request, hash, actor, operation, result identities and
  common recorded instant. Mutable concurrency versions are coordination metadata, not balances.

Amounts use exact `decimal(28,4)` and the configured currency scale. Command/read boundaries use
decimal strings. Reject excess scale before conversion, nonpositive application/payment amounts,
overflow, unknown properties, duplicate properties and malformed identities. Bound input to
262,144 bytes and 1,000 application targets; reject duplicate targets rather than relying on order.
Accumulate with checked exact arithmetic and reject totals outside the storage bounds.

Internal capacity events and their net control effects must not be confused: a payment can establish
gross advance capacity and immediately consume it while its combined journal has no advance line.
The evidence group proves the net effect by historical account and retains both capacity events.
It must not fabricate journal lines for net-zero effects or attribute one journal line twice.

## Bill integration and existing recognition

BK-05 posting creates payable item evidence in the same transaction as the complete reviewed bill,
recognition journals and receipt. One bill has one payable item, supported by its invoice-side
control lines; each recognition unit retains its existing detailed links. Derive capacity from
posted journal/source evidence, not the PO estimate, declared draft total or a caller's amount.
Invoice-first Prepayment is distinct from SupplierAdvance. GRNI is distinct from supplier AP and
is reported separately; neither is available for payment allocation.

Existing BK-04 invoice-side postings without a structured bill remain attributable recognition
payables. Reconcile them by supplier/PO with bill identity explicitly absent; do not invent BK-05
bills or allow bill allocation until a source-owned supported linkage exists. Recognition-only
receipt accruals do not create supplier AP items.

Extend trusted source integration so later BK-04 invoice effects and permitted corrections keep
item evidence and journal effects atomic. Preserve BK-05's rejection of generic corrections to
bill-owned postings. Generic journal/recognition entrypoints must also reject corrections that
would detach item/application evidence. A non-bill recognition correction can proceed only through
an integrated adapter that records the corresponding item effects and proves no unresolved
allocation dependency. No user-selected source-kind string grants this authority.

## Payment source and posting contract

`RecordSupplierPayment` accepts request identity, expected PO/configuration versions, immutable
payment details, optional private evidence and an explicit list of bill allocations with expected
item versions. No separate mutable payment-draft subsystem is introduced. It validates an active
Bank or Cash funding account, allowed configuration, current authority, dates and source identity.
A method label never chooses an account. Reject CardLiability and arbitrary general accounts.

The command records an actual payment already made; it never initiates money movement. The
payment's actual/effective date cannot follow its posting date. Receipt metadata follows existing
PO-file authorization and availability rules; the missing-document state is explicit. Metadata
snapshots do not promise BK-07 physical retention.

Post one atomic source result, all item/application effects and the complete journal:

| Payment disposition | Debit | Credit |
| --- | --- | --- |
| Entirely unapplied | SupplierAdvance for the full amount | Funding account once |
| Entirely allocated | Historical payable account(s) for allocated amounts | Funding account once |
| Mixed | Historical payable accounts plus SupplierAdvance for the remainder | Funding account once |

For each payment retain gross funding capacity and its immediate applications. A fully allocated
payment has zero remaining advance capacity, but unapplying it can restore an advance without
inventing another payment. Initial allocations use the payment posting date, which cannot precede
any target bill posting date. All source, journal, item, application, evidence and receipt writes
commit or roll back together, with one recorded instant.

Bank fees, account transfers and card settlements are separate BK-12 sources and cannot be bundled
into supplier payments. Actual supplier refunds and repayment of refund-clearing debt belong to
BK-08. A negative payment is not an alternate route to those event types.

## Allocation and historical availability

`ApplySupplierFunds` applies Advance to Payable. The shared restricted participant also supports
CreditReceivable to Payable and Advance/CreditReceivable to RefundClearing for the appropriate
future source-owned BK-08 flows. Production credit/refund commands are absent. Each pair clears
the debt's historical liability account and the funding item's historical asset account; current
mapping changes cannot strand a balance in an old control account.

The application date must be in an open period and no earlier than either participating source's
posting date. Both sources must already exist and be posted when the command is recorded. Check
current authority and expected versions under the same accounting/source locks as writing.

For each affected item, calculate the proposed final movement stream at the requested date and
every later event date. Opening capacity plus signed movements must remain nonnegative at every
boundary, including already recorded applications, reversals and source corrections. Consider
future-dated recorded activity as well as today's balance. Validate both debt and funding sides.
Use final atomic event groups on a date so a valid correction group is not rejected for an
intermediate uncommitted state; no report may expose that intermediate state.

The union of posting-date boundaries suffices for availability because normal business operations
use all events currently recorded. Historical as-recorded queries filter the immutable committed
groups by recorded time; a later command cannot rewrite the earlier view. Reject a conflict rather
than moving another application's date or silently redistributing funds. Same-date applications
have a stable evidence order but acquire no right to exceed the date's available capacity.

Example: Advance 100 on September 10 and Payable 150 on September 15 permit application on or after
September 15. An application dated September 16 and recorded September 20 gives these results:

| Posting cutoff | Recorded cutoff | Advance | Payable |
| --- | --- | --- | --- |
| September 14 | After September 20 commit | 100 | 0 |
| September 15 | After September 20 commit | 100 | 150 |
| September 16 | September 19 | 100 | 150 |
| September 16 | After September 20 commit | 0 | 50 |

A recorded September 18 application consuming that same 100 makes the backdated request fail,
even if a later reversal restores today's available balance. Allocation before September 15 or
into a closed month fails. No document/effective date substitutes for posting availability.

## Reversals and source corrections

`ReverseSupplierApplication` reverses one complete application once, with a reason and linked
evidence, at an open date no earlier than the application. Partial changes reverse the application
and explicitly reapply the retained amount atomically; they do not edit it. Use exact inverse
historical control accounts. Recheck the entire affected availability stream, including later
source reversals; releasing one allocation must not create capacity unsupported by its source.

`CorrectSupplierPayment` is an internal correction of an incorrectly recorded payment. It previews
and then atomically reverses dependent applications, reverses the original payment, optionally
records its explicit replacement, and applies the requested replacement allocations. A correction
reason and expected dependency versions are mandatory. The original remains immutable. This
corrects recorded cash evidence; it is never labeled an actual refund. Unsupported dependencies,
including future reconciliation matches without a resolution adapter, reject the entire command.

Account for immediate allocations already embedded in the original payment journal. Their inverse
item movements participate in the correction group, whose combined control/funding journal effect
is the exact inverse of the original payment result. Do not independently post their unapplication
journals and then invert the entire combined payment journal: that would restore AP twice. Later
standalone allocations have their own inverse journal effects. Prove attribution for the complete
group before commit, then post the replacement as a distinct linked source result if requested.

Preview returns the affected source/item/application versions, proposed effects and a deterministic
plan fingerprint. Execution recomputes under locks and rejects changed dependencies. A preview is
not a reservation or posting receipt. Neither preview nor execution accepts caller-authored journal
lines. Bound dependency closure to 1,000 events and the command envelope; reject oversized closures
without partial execution or truncation and report that limitation.

For future bill/credit corrections, the same allocation participant operates inside the source
owner's transaction. It must preserve actual payment/refund records and cash postings, unwind
applications, reverse/replace the source, and explicitly reapply eligible amounts. Until the
complete owner workflow exists, reject the correction rather than offer a partial workaround.
BK-06 tests the following participant contracts using disposable source adapters:

- Paid bill 300 replaced by 280: unapply 300 to Advances, correct the bill, reapply 280. AP is zero,
  Advances is 20, and Bank stays -300. Correcting a bill does not correct the real payment.
- Refunded credit 18 replaced by 10: unapply through RefundClearing, correct the credit, reapply 10.
  CreditReceivable is zero, RefundClearing is 8, and Bank stays +18. No automatic repayment occurs.

All source reversals, replacement postings and reapplications respect source-date and open-period
rules. A correction after close uses an open posting date and leaves closed-period balances intact.
The complete immutable group shares one recorded instant across its journal and subledger effects.

## Reconciliation and readback

Implemented read-only accounting resources expose supplier open items, item/application history
and supplier control reconciliation under `/api/beta/accounting`. Require AccountingReportsRead. Supplier/PO/bill
filters are tenant-qualified and validated; foreign and missing identities are indistinguishable.
Private document access continues to require its existing permission independently of report access.

Accept `postingThrough` and `recordedThrough`; include only committed event groups satisfying both
inclusive cutoffs. Use one consistent database snapshot for journal, source and subledger reads.
Return the resolved cutoffs in responses and bind filters/cutoffs to stable pagination cursors.
Bound pages to 200 rows and source-detail envelopes to the command limits. Return whole-filter
totals separately from page totals. Monetary values remain decimal strings.

The implemented database capture runs under the shared tenant Accounting lock, then commits and
disposes its transaction before indexing evidence and computing responses. Cursors also bind route,
page size and captured financial-group/journal sequence ceilings, so later commits cannot enter a
continued report even with backdated postings or a future recorded cutoff. Duplicate-preserving keyed
lookups replace repeated whole-history scans; indexing, projection and reconciliation observe request
cancellation outside the lock. Database capture still holds the shared lock and loads metadata
proportional to tenant history. Page bounds do not bound capture or total processing; capture memory
and latency remain unmeasured, with no throughput or maximum-history claim.

Reconcile each historical account, currency and control family, then roll up by supplier/PO/bill.
Liability controls use credit minus debit; asset controls use debit minus credit. Aggregate item
movements independently from journal lines and compare exact totals. Include archived or formerly
mapped control accounts using posted account-purpose snapshots. Current mappings are not a filter
for historical activity. Attribute journals with immutable links, never current PO supplier names.

Return journal amount, attributed subledger amount, difference, missing/duplicate attribution and
invalid-source evidence counts. A control journal without item evidence remains in journal totals
and appears as an unresolved discrepancy; a missing item cannot disappear from an inner join.
Offsetting discrepancies across accounts/items cannot produce a successful reconciliation verdict.
Zero-net missing evidence still prevents a complete verdict. Report unsupported legacy evidence
explicitly and never manufacture a balancing item merely to make the report agree.

Supplier/PO filters cannot hide unattributed tenant control activity whose owner is unknown. Return
that unresolved coverage separately and mark scoped reconciliation incomplete until it is resolved.
There is no authority to assign unknown financial history to the currently selected supplier.

Report AP, Advances, CreditReceivable and RefundClearing separately. Net supplier position is
AP + RefundClearing - Advances - CreditReceivable. Expose source/journal/correction IDs, due-date
evidence and unassigned recognition payables. Zero AP means recorded bills settled, not a completed
purchase or proof there are no uninvoiced accruals. GRNI remains separate and unavailable to allocate.

This delivers BK-09's supplier-control readback prerequisite. It does not add production close or
claim statement reconciliation, tax filing, complete inventory valuation or complete books.
Any rebuildable query projection is disposable; rebuild from preserved events and journal evidence,
then rerun reconciliation. Rebuilding cannot erase an unknown journal or amend posted history.

The implemented repair is narrower than a general projection rebuild: protected recognition
derivation reconstructs only missing deterministic control-attribution rows whose full stored
source/group/item/movement/journal ownership is still valid. Conflicting, detached and unknown
evidence stays unresolved. Future bill/credit fixture adapters have no shipped repair workflow.

## Authority, retries and concurrency

Use fixed SupplierPaymentsRecord, SupplierPaymentsCorrect and SupplierAllocationsManage permission
identifiers. Recording requires Record and also AllocationsManage when allocations are supplied;
payment correction requires Correct and AllocationsManage for its dependent applications. Allocation
and reversal require AllocationsManage. Preview requires the same authority as execution. These
permissions receive no production role assignments or runtime mutation grants in this release.

Trusted participant procedures are callable only within authorized source-owner transactions;
there is no general runtime item-creation or arbitrary control-posting API. Validate actor/session,
tenant, business authority and request actor binding before replay. Exact operation/request UUID
and canonical payload return the original authorized receipt, without rechecking changed source
versions. A changed payload conflicts. A different UUID cannot repost an already owned source event.
Deadlock retries retain the same request identity. Never retry a successful write because readback
failed; retry the read or consult the original command receipt.

Use the existing tenant Accounting transaction-owned exclusive application lock for all affected
financial commands. Within it, acquire PO/source rows in stable ID order, then item/application
coordination and finally document evidence. Period close, mapping/account changes, PO amendment,
recognition, bill posting and corrections must share this ordering. Source and item versions change
atomically. Independent SQL connections, not an in-process semaphore, must establish race safety.

Retain PO supplier-change guards after payment or other financial evidence, even when reversed.
Funding account archival and permission revocation must not race successful posting. Failures leave
no new source, journal, movement, application or success receipt. Existing SQL administrator powers
remain outside application tamper protections.

## Migration and compatibility

Add one coherent forward migration after BK-05; consolidate only BK-06 development migrations.
Preserve dependency ordering and never rewrite a migration merged into main. Update readiness,
provisioning, principal probes, schema markers, restore compatibility and migration expectations.
Block destructive down-migration. Require matching binaries/procedures and stop incompatible writers.

The migration deterministically creates attribution/item evidence for supported already posted
BK-04/BK-05 invoice journals and their corrections, preserving original posting and recorded times.
This is a derived index of existing accounting evidence, not a new opening journal or automatic
backfill from estimates/files. Preserve original source, journal, receipt and correction bytes.
Do not remint successful receipts to include new IDs; readback resolves newly derived item links.
Unknown or inconsistent control evidence is retained and reported as a discrepancy without guessing
bill identity or available capacity. Historical reports must agree before and after derivation.

Verify clean creation and upgrade from the actual merged PR base, including bills already posted,
recognition corrections, remapped/archived control accounts and exact request replay. Keep a stacked
BK-05 upgrade test while developing, then refresh against its final merged schema. Preview data
preservation never authorizes migration-history resets, deletion or a new supported upgrade baseline.

Recovery must retain item/source/journal links, original receipt bytes, allocations, inverse events,
permissions and document availability state. BK-07 retention gaps remain explicit. Rollback uses a
compatible binary, forward repair or guarded paired recovery; it never deletes financial history.

## Verification and implementation handoff

Use TDD with Gherkin comments and distinct regression claims. Pure tests own canonical input and
exact arithmetic where sufficient; independent real SQL connections own authority, atomicity,
history and concurrency. Required acceptance includes:

1. Deposit 100, bill 306.60, apply 100, pay/allocate 206.60: Advances and AP end at zero; supplier
   payments and funding-account outflow equal 306.60, with balanced journals and exact attribution.
2. Multiple bills, partial allocations and overpayments; zero-value bill evidence; rejection of
   excess scale, overflow, unsupported funding, currency/supplier/PO mismatch and pro forma targets.
3. The September cutoff example, a later competing allocation followed by release, same-date groups,
   closed periods, future sources and reverse/reapply dates. Check both source and target capacity.
4. Concurrent allocations competing for one advance and for one bill; record/allocate versus close,
   mapping/archive, correction, PO amendment and permission changes. Prove both serial orders.
5. Exact replay after later source changes, revoked authority before replay, changed payload, duplicate
   source under a new UUID, stale versions, foreign/missing IDs, RLS and direct-DML denial.
6. Faults after source/journal/application writes roll back the entire command. Preview drift rejects
   execution. Concurrent reversal creates at most one inverse and preserves capacity history.
7. Payment correction and the two synthetic source-correction participant examples, including closed
   periods, preserve actual money movement where the money source itself is not being corrected.
8. Reconcile all four controls, historical mappings and archived accounts at both cutoffs. Inject
   missing/duplicate attribution, offsetting discrepancies and unknown controls; none reports complete.
   Rebuild supported derived evidence and verify exact agreement without changing financial history.
9. Existing BK-04 activity, BK-05 posting, supported recognition corrections and generic bypass
   rejection preserve source ownership. Runtime grants cannot mint synthetic credits/refunds/items.
10. Clean migration, supported-base upgrade and guarded recovery preserve all financial/replay bytes
    and report missing document bytes honestly. Ordinary accounting setup and PO workflows still work.

Run targeted meaningful SQL/.NET mutation checks and document surviving equivalent defenses and
tooling limits; do not claim an automated score without a run. Review accounting invariants before
delivery. Run CONTRIBUTING.md's current-source full verification and separate container smoke gate.
Refresh this checkout's isolated preview with `scripts/dev-up.ps1`, exercise authorized readback and
ordinary PO/accounting flows, and report its URL. Internal posting acceptance remains real-SQL
evidence; there is no invented browser payment-entry workflow in this increment.

Update living accounting, purchasing, architecture, principal and migration guidance as implementation
makes these contracts true. Deliver a ready-for-review PR after verification. Merging and production
operations remain separately authorized. Written-spec approval precedes the ignored implementation
plan; plan review and execution-method selection precede product implementation.

## Implementation evidence and remaining gates

The owner approved this specification and the subsequent plan before implementation. The branch
implements the internal payment/allocation/correction and four-control readback boundary described
above; living contracts are in [accounting](../accounting.md), [purchasing](../purchasing.md),
[architecture](../ARCHITECTURE.md), [principals](../operations/database-principals.md) and
[migrations](../operations/database-migrations.md). `BookkeepingAvailable` is false, runtime financial
write grants are absent and new financial write permissions have no production assignments.

Recorded targeted evidence at the corresponding implementation revisions:

- Correction ownership, historical availability, replay and atomicity have real disposable SQL
  regression coverage. The Task 6 lock-order review fix at `55bf934` passed its seven affected owners
  after an observed behavioral failure; future bill/credit corrections remain disposable participant
  adapters, not shipped business workflows.
- Task 7 report revision `d75d476` passed 16 SQL/HTTP cases after the source-ownership review fix.
  Selected mutations detected cursor binding, frozen continuation, missing coverage, exact historical
  line ownership, decimal serialization and embedded-application compensation regressions. The initial
  HTTP pre-implementation run failed on a Cache-Control assertion setup issue, so transport behavioral
  RED was not established then. Later GREEN and mutations do not erase that process deviation.
- Task 8 at `be18892` passed 81 migration/recovery/principal/schema/manifest cases and detected six
  selected timeline, source-ownership, replay-authority, inverse-uniqueness, attribution and cursor
  mutations. The subsequent original-correction-source proof fix at `0bda235` has its own observed
  behavioral RED and four affected GREEN cases; the earlier 81-case result is not evidence for that
  later amendment. All targeted runs rebuilt the source at their respective revisions.
- Actual merged-BK-05 upgrade and guarded SQL recovery retained original financial/replay bytes,
  restored supported attribution and kept missing-file disposition explicit. These are disposable SQL
  drills, not a live Azure or paired blob-copy disaster drill. BK-07 holds remain unimplemented.

The selected mutation probes are not an exhaustive mutation campaign or an aggregate score, and
overlapping cohorts must not be added into a unique-test count. Ignored execution reports retain
commands, timings, behavioral/setup distinctions, source restoration and scoped review evidence.

Pending release gates: complete-branch internal review and any scoped fixes; the current-source
`./scripts/verify.ps1` aggregate with exact counts/timings; independent `./scripts/smoke-container.ps1`;
preserved isolated-preview refresh and browser inspection of accounting setup, ordinary PO behavior
and authorized report responses; then ready-for-review PR delivery. Nonzero internal posting
acceptance remains disposable SQL evidence. No public payment-entry browser workflow, production
activation, merge or production operation is claimed or authorized by these results.
