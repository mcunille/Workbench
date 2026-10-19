# BK-08: authority, recovery and verification contract

**Status:** Accepted with the written [BK-08 specification](2026-10-19-bk-08-supplier-credits-and-refunds.md) on October 19, 2026. These requirements are normative for that design. The implementation-plan handoff remains pending; no runtime verification is claimed.

## Authority, transactions and failure behavior

Add fixed permissions `SupplierCreditsPost`, `SupplierCreditsCorrect`, `SupplierRefundsRecord`,
`SupplierRefundsCorrect`, `SupplierRefundRepaymentsRecord`, `SupplierRefundRepaymentsCorrect`, and
`SupplierBillsCorrect`. Keep existing `SupplierBillsManage`/`SupplierBillsPost` for successor bill
review/posting. Correction authority does not implicitly grant authority to replace another source
family. Require each affected family's correction permission and successor posting permission;
require `SupplierAllocationsManage` for allocations, settlement releases and reapplications.
Standalone refund/repayment recording requires its own recording permission, not allocation authority
merely because it consumes items. Preview requires the same authority as execution. All new write
permissions remain unassigned and their SQL commands ungranted to web/worker runtime principals.

Use the existing tenant Accounting transaction-owned exclusive lock. Acquire affected PO/source rows
in stable identity order, then item/application/settlement coordination, then evidence rows. Mapping
changes, account archival, recognition matching, bill/credit/cash commands, PO amendments, period
controls and evidence removal share compatible coordination. Financial history continues to block
PO supplier identity changes even after full correction. Independent SQL connections prove races;
in-process serialization is insufficient.

Reject malformed/duplicate/unknown JSON properties, invalid UUIDs, stale versions, unsupported source
states, excessive precision, overflow, duplicate targets/claims, unbalanced derived journals, foreign
evidence and unavailable required dependencies. Match existing strict financial canonicalization.
Sources, journals, settlements, applications, claims, evidence holds, audit and success receipt commit
together or roll back together. Missing and foreign source identities remain indistinguishable.

Authenticate actor/session/tenant and recheck all required current permissions before replay. Exact
operation/request UUID plus canonical complete payload returns the original authorized result even
when versions have since advanced; changed content conflicts. Preserve actor binding. Unique source
events prevent reposting under another UUID. Deadlock retries retain request identity; an uncertain
outcome checks/retries that command. A failed read after a successful write retries only the read.
Trusted composition requires an authentic pending owner receipt and exact authorized child payload;
supplying an owner UUID or operation string cannot impersonate a correction transaction.

## Evidence, readback and compatibility

Extend `FinancialEvidenceSourceAdapters` for each authentic credit/cash source and successor. Acquire
BK-07 document holds in the source transaction, with private revision/digest/length, source snapshots
and frozen retention policy. Accept explicit missing-evidence reasons. Original evidence sets and
holds survive every reversal; successor and supplemental evidence append relationships. Preserve
supplier, account and mapping snapshots. Removal/cleanup races either establish the hold first or
reject unavailable evidence. Recovery reports missing/corrupt bytes honestly without dropping sources.

Extend the existing journal detail and supplier-open-item detail/history contracts additively with
typed credit claims, refund/repayment source details, cash settlements/releases and correction links.
An item history identifies every cash source consuming it, including multi-item receipts, and retains
zero-balance/corrected items. Journal detail exposes the immutable source identity and correction
chain. Internal source read commands use the same stored records. No new public write contract or
standalone reporting subsystem is required. Existing authorized GET routes retain
`AccountingReportsRead`, private non-cacheable responses, bounded histories, decimal strings, dual
posting/recorded cutoffs and continuation ceilings. Regenerate OpenAPI/client declarations for changed
response contracts. Report access does not grant private-file access; use BK-07's document checks.

Reconciliation must independently aggregate all new control journals and their attributed item
effects, retaining historical account identities. Unknown/duplicate/invalid source evidence remains
an incomplete result even when net differences are zero. Net supplier position remains
AP + RefundClearing - Advance - CreditReceivable. Cash events distinguish original movements,
recording-error corrections and actual opposite-direction movements. Refund repayments remain
separate from supplier purchase payments. Do not infer a completed purchase from zero AP.

Ship one coherent additive migration after the actual PR base. Preserve merged migrations and
retained preview data; consolidate only this release's unapplied development migrations. Update EF
models, RLS, immutable-row guards, denied direct writes, protected procedure integration, provisioning,
readiness/schema markers and supported restore compatibility. Existing receipts replay unchanged;
there is no automatic financial backfill or synthetic credit/refund source for old documents.
Old generic and source commands must reject newly unsupported dependencies after upgrade, rather
than detach BK-08 evidence. Guard Down against destroying financial history. Rollback is a verified
compatible binary, forward correction, or coordinated SQL/blob recovery; no production operation
is authorized by this design.

## Acceptance and verification

Use the repository [test method](../../tests/README.md#test-method), test-audit authoring gate,
Gherkin comments and targeted behavioral mutations. Exercise authentic source commands through
real SQL under intended principals. Disposable setup may establish authorities and BK-04 evidence;
it must not substitute a synthetic credit/refund implementation for the shipped command.

| Scenario | Required observable result |
| --- | --- |
| Deposit 100, expense bill 306.60, apply 100, pay/apply 206.60 | AP 0, Advance 0, Bank -306.60; supplier payments 306.60. |
| Credit 18 on that settled bill | CreditReceivable 18; purchase debits net 288.60; cash unchanged. |
| Actual refund 18 | Bank -288.60; CreditReceivable 0; net supplier payments 288.60. The separate BK-12 fee 3 would yield outflow 291.60; BK-08 does not claim fee posting. |
| Return unused deposit 40 from Advance 100 | Advance 60, Bank net -60; expense and AP unchanged; no credit note. |
| Credit 30, explicitly apply 20 to an open bill | AP reduced 20; CreditReceivable 10; original classifications reduced 30; no cash. |
| Paid bill 300 corrected to 280 | Original payment remains 300; AP 0, Advance 20, Bank -300; complete bill/recognition successor history. |
| Credit 18, refunded 18, corrected to 10 | Original receipt remains 18; CreditReceivable 0, RefundClearing 8, Bank +18 for this sequence. |
| Actual repayment 8 after that correction | RefundClearing 0, Bank +10 for that sequence; no second purchase payment or cost change. |
| Refund recording error corrected | Exact original cash/control inverse plus explicit successor; distinct from an actual repayment; prior history retained. |

Also prove:

1. Unpaid/part-paid bills, partial/multiple refunds, mixed advance/credit refund, repeated attempts,
   multiple original components and duplicate credit references preserve exact capacities and totals.
2. Source revisions, original rounding attribution, invoice-first prepayment credits and later
   recognition cannot double-release capacity. Unsupported inventory/tax state rejects. Concurrent
   credit claims, matching and corrections cannot consume the same supported amount twice.
3. Backdated refund/application/credit/correction rejects a negative balance at any later affected
   event date, even when today's balance permits it. Repeat September allocation examples from BK-06
   with credits/refunds. Exercise posting-date and recorded-time cutoffs before/after complete groups.
4. Preview staleness, oversized closure, omitted dependent credit/refund, unknown owner, generic
   correction bypass, wrong permission, current permission loss before replay, cross-tenant IDs,
   changed request content and different-UUID source reposting fail without partial history.
5. Independent-connection races cover refunds versus allocations, competing refunds/repayments,
   source correction versus posting/matching, period controls, account archival and document removal.
   One transaction wins or the conflicting command fails; no excess consumption or post-close journal.
6. Every control family reconciles independently at both cutoffs, including historical mappings,
   closed-period corrections, net-zero control groups, settlement releases and changed pagination
   ceilings. Deliberately corrupt attribution in a disposable database and require incomplete reports.
7. Clean creation, upgrade from every supported PR base and guarded rollback preserve existing source,
   receipt, journal, evidence, permission and document bytes. Paired SQL/blob recovery includes credit,
   refund, repayment and correction chains with held and deliberately missing receipt content.
8. Evidence hold acquisition, inheritance, supplements, permission loss, expiry/disposal and physical
   cleanup cover new source kinds. Metadata-only sources remain honest; failed posting acquires no
   partial hold. Ordinary unposted documents and non-accounting purchasing continue to work.

For implementation delivery, run current-source `verify.ps1` and separate hardened container smoke,
independent complete-branch internal review, and retained-preview inspection. In the isolated preview,
use controlled internal source setup and authorized report GETs to inspect persisted source/journal/
item/correction evidence and retained documents; do not temporarily grant runtime financial commands.
Exercise affected existing report/document UI at keyboard, narrow viewport and both themes where
rendering changes. Record exact evidence and limits; no public credit/refund entry UI is claimed.
