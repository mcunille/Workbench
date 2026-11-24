# PO-07 workflow and release

[Return to the main entry](2026-09-20-po-07-deposits-and-payments.md).

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
