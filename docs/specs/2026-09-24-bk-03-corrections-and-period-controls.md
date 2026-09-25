# BK-03: corrections and period controls

**Status:** Accepted — scope and written spec approved on 2026-09-24; implementation-plan review pending.

Parent: [PO-07 bookkeeping prerequisites](2026-09-20-po-07-deposits-and-payments.md).
Prerequisites: [BK-01 accounting foundation](2026-09-21-bk-01-accounting-foundation.md)
and [BK-02 atomic journal](2026-09-23-bk-02-atomic-journal.md).
Implementation baseline inspected: `bbb06da`.

## Outcome and boundaries

Prevent a posted event or a closed period from being silently rewritten. A correction appends an
exact reversal and, when requested, a replacement in one transaction, retaining the original source
revision and a traceable reason. Closed-period corrections use an explicitly selected open posting
date. Posting and period closure serialize across independent application replicas.

Deliver monthly period records, restricted SQL period/correction primitives, durable correction
receipts and links, additive report readback, and adversarial real-SQL tests. BK-02 already stores
document, effective and posting dates independently and records database-owned UTC time; preserve
that contract. No existing purchase order or attachment creates a financial event.

Keep `BookkeepingAvailable` false. Ship no public posting, correction or close write endpoint, manual
journal editor, reopening, fiscal-year earnings roll-forward, report UI or activation override.
Production close remains unavailable until BK-09 reconciliation acceptance, BK-10 cutover and BK-11
bank/cash/card reconciliation exist. Test-only typed adapters exercise the primitives in disposable
databases; they and their grants must be absent from shipped artifacts.

BK-06/BK-08 own supplier application dependency closure and BK-07 owns physical evidence retention.
This increment retains immutable source snapshots, digests and correction relationships. It does
not claim that an ordinary PO attachment reference constitutes a retained financial document.

## Existing architecture and chosen approach

`Accounting.PostJournal` is a restricted SQL kernel called inside an outer source transaction.
It shares the transaction-owned exclusive application lock `Accounting:<tenant UUID>` with setup
changes and validates live authority before receipt replay. Runtime principals cannot execute the
kernel directly or mutate journal tables. Source identity, snapshots and posting receipts are immutable.

Extend this boundary rather than introduce an application-only period check. Application-only
checks cannot prevent a close/post race or direct runtime SQL bypass. Retain the conservative tenant
lock rather than introduce per-period lock ordering before there is measured contention. Use typed
test sources rather than expose a generic correction API before source policies exist.

Keep period and correction SQL/model code in focused persistence units; alter the installed posting
procedure through the new migration. Do not rewrite the merged BK-02 migration or its historic SQL.

## Period representation and calendar

An accounting period is a calendar month identified by tenant and its first date, with inclusive
last date and fiscal-year start date derived from the configured start month. Store immutable period
identity and calendar values separately from an append-only closure record. A period is open when
it has no closure; a missing period must be materialized and validated under the tenant lock before
a new posting can proceed. Do not interpret a failed period lookup as permission to post.

Materialize months on demand for posting or internal close, avoiding an arbitrary pre-generated
calendar horizon. Closing an empty month is supported by the internal primitive. Reject months
entirely before the accounting start date; in the first partial month, retain BK-02's per-posting
start-date check. Use safe month-boundary arithmetic, including December of year 9999. Fiscal-year
start must be representable; reject an unsupported boundary rather than overflow or guess.

Period materialization also freezes the accounting calendar/start policies through the existing
policy protection mechanism or an equivalent period-presence guard in `Accounting.Save`. Thus an
empty-period closure cannot be reinterpreted by changing the fiscal calendar before a first journal.
The existing first-journal freeze remains valid and is never populated with a fabricated journal ID.

A closure stores tenant/period, stable ID, actor, database UTC time, nonblank reason, immutable
bounded evidence snapshot and SHA-256 digest, plus its command receipt. One closure per period;
no reopening or deletion. BK-03 permits independent month closure internally, without claiming the
completed financial-close checklist or fiscal-year close semantics.

All new records have tenant RLS, tenant-qualified foreign keys, restrictive deletion and runtime
DML denial. Constrain calendar boundaries and uniqueness in SQL. Current state is derived from
durable records rather than a freely writable `IsClosed` flag.

## Period command and posting order

The internal close primitive requires an outer transaction, the common tenant lock, authenticated
tenant/session and current `AccountingPeriodsClose` permission. It has no runtime EXECUTE grant.
A future trusted close adapter must additionally verify the release checklist; the kernel and an
accounting role alone never enable a production close action.

Under the lock, validate current authority, check the durable request receipt, validate the frozen
calendar and expected configuration revision, materialize the month, then append closure, evidence,
receipt and audit atomically. Matching retries return the original result even after closure;
changed actor or canonical command conflicts. A distinct request for an already closed month
conflicts. Bounds and canonicalization follow BK-02: 2,000-character reason, 256 KiB JSON evidence
and canonical command, versioned command kind, SHA-256 over stored UTF-16LE text. Reject malformed,
duplicate or unknown fields; never truncate. Audit metadata contains IDs, not private evidence.

For a new post, `PostJournal` checks period openness after current-authority/receipt replay checks
and before any financial inserts, holding the same lock until outer commit. An original successful
posting retry remains a read of its receipt even if its period has since closed. A different request
for a new event in that closed period fails without side effects.

If posting wins the lock, close observes the committed posting. If close wins, posting rejects the
closed month. The future close adapter must validate its reconciliation evidence under this lock
or compare a recorded ledger version after acquiring it; a stale pre-lock checklist cannot close.
Timeout/deadlock remains a retryable conflict with full rollback, using the same request ID on retry.

## Correction records and authority

A correction group records tenant, stable ID, original source-event/journal IDs, reversal IDs,
optional replacement IDs, actor, reason, correction posting date, database UTC time and immutable
correction evidence/digest. It owns a durable command receipt returning the whole result. Link each
new event to its group and role; a unique original-journal constraint permits only one reversal.
Tenant-qualified references prevent linking another tenant's evidence or entries.

The restricted correction kernel accepts trusted source-adapter output inside an outer transaction.
It cannot be executed by runtime principals, and there is no caller-set authority flag or generic
public wrapper. The adapter requires its source-specific correction permission and rechecks it
under the accounting lock before any replay. Configuration/manage or report authority is not a
blanket permission to correct financial sources. The disposable synthetic adapter uses an explicit
test-only authority contract, without introducing a production correction permission prematurely.

The typed adapter locks and validates the latest source revision and dependency state after taking
the accounting lock. Only source types with an implemented correction policy may use this path.
The BK-03 synthetic source has no applications; future bill/payment/credit adapters must resolve
their entire dependency closure atomically under their approved policy before invoking this kernel.
Unknown or unsupported source/dependency cases fail closed. A caller-supplied empty dependency list
is never proof that a real financial source is independent.

## Atomic reverse and replace

1. Begin the source transaction, acquire the common lock and revalidate current source authority.
   Check the correction receipt before mutable source/configuration/account checks. Match actor,
   command kind/version and canonical input byte-for-byte; changed content conflicts.
2. Lock the original event and owning source, validate same-tenant identity, expected revision,
   correction eligibility and absence of an existing reversal. A reversal entry cannot itself be
   reversed. A replacement may later be corrected as a new original, retaining the entire chain.
3. Require a nonblank reason and immutable correction evidence referencing the original revision
   and snapshot digest. Validate the selected open posting date and expected configuration revision.
4. Derive the reversal from persisted original lines in SQL: same ordinals, accounts, account
   revisions and descriptive snapshots, exact swapped debits/credits, same currency and scale.
   Preserve original document and effective dates. Record a new actor, UTC time and open posting
   date. Do not rederive the inverse from current mappings or accept caller-authored inverse lines.
5. If replacement is requested, derive its lines through the typed source policy using current
   permitted mappings/account revisions and a new immutable source revision. Validate all BK-02
   journal rules. Use the same correction posting date; preserve the original effective date for
   this increment. A replacement document date may reflect corrected source evidence. Effective-date
   reclassification needs a later explicit source policy; it must not silently move closed balances.
6. Append source revisions/events, journal entries, correction links/evidence, audit and the single
   correction receipt, then commit together. Any failed validation, dependency resolution or insert
   rolls back the entire operation, including period materialization and source changes.

An inverse preserves historical account snapshots even if the account was renamed since posting.
The inverse path is narrowly restricted to the stored original; it does not create a general bypass
of active-account/version checks for new postings. BK-02 already blocks archiving used accounts;
retain that guard. Replacement entries always use current account checks and snapshots.

Do not change the meaning of existing single-post receipts. Correction receipts form a separate,
versioned command namespace and include all returned IDs. Internal journal append results must
not escape the outer transaction or create independently replayable half-corrections. Retain
per-entry posting receipts with server-generated keys stored in the correction group and
inaccessible as independent source commands; the group receipt alone controls correction replay.

Reversal-only is an explicit outcome, not an empty or zero replacement journal. A later reinstatement
requires a newly authorized source event; the old event is never made editable. Accounting reversal
does not assert that money was refunded. Future application corrections preserve actual funding
entries as required by the parent bill-300-to-280 and refunded-credit-18-to-10 examples.

## Readback and examples

Extend journal detail additively with correction relationships: group ID, role, original/reversal/
replacement IDs, reason and recorded time. Include immutable group evidence/digest for an authorized
detail reader. Existing header, source and line fields retain their meaning; regenerate OpenAPI
types. Reports continue to require `AccountingReportsRead`, tenant scoping and private/no-store.

Add a bounded `GET /api/beta/accounting/periods` read for an explicit inclusive month range of at
most 120 months. Return calendar/open-or-closed state and closure ID/time for those months; an
unmaterialized eligible month is reported as open without writing during a GET. Missing calendar
configuration returns a configuration-incomplete response, not invented periods. Exclude months
entirely before the start boundary. Invalid/non-month-start ranges return validation errors.
This is readback of current state, not a historical close certificate or an available close action.

Journal and trial-balance reports sum original and correction entries at the existing independent
posting-date and recorded-time cutoffs; never hide reversed entries or replace the original amount.
Relationship readback on detail describes current history; cutoff reports must not leak later
correction metadata into an earlier as-recorded view. Keep list/header contracts unchanged in BK-03.

For example, an original September expense of 300 posts debit Expense/credit Payable 300. After
September closes, an October correction reverses 300 and replaces with 280, retaining the original
effective date. September remains 300; through October the balance is 280. A recorded-time cutoff
before the correction sees only 300. Failure to post the replacement leaves the original 300 with
no reversal or successful correction receipt. This synthetic journal example does not implement
supplier allocations or approve real purchase recognition before BK-04.

## Migration and compatibility

Ship one additive migration from merged `20260923010000_AddAtomicJournal`, with models/snapshot,
restricted procedures, RLS/permissions, readiness marker and provisioning probes updated together.
Existing journals, source events, snapshots, amounts and receipts remain unchanged. Materialize
distinct months containing existing postings using the frozen policies, all initially open; never
infer historical closure from dates. No calendar needs to be generated for tenants without journals.

Verify fresh creation and upgrade with retained BK-01/BK-02 history, including receipt replay after
upgrade. Stop incompatible writers for schema deployment and require the matching application
readiness version. Down refuses destruction of accounting history; recovery uses a forward fix or
separately authorized guarded restore. Local preview data is not permission for history resets.

## Acceptance and verification

Use test-first GIVEN/WHEN/THEN coverage, proving meaningful missing-behavior failures before code.
Tests run against current source and actual restricted SQL principals, including:

- Calendar boundaries: fiscal start month, leap February, year transition, maximum date, partial
  first month, empty-month close, and policy changes after period materialization.
- Closed-period rejection for ordinary posting, reversal-only and replacement; later open-period
  correction preserves original dates and closed-period balances. Receipt replay remains valid
  after closure but requires current authority; distinct requests cannot bypass closure.
- Exact inverse across multiple lines and scales 0–4, maximum legal values, renamed accounts,
  stale replacement mappings, invalid reason/evidence, and malformed or cross-tenant links.
- Original snapshots/revisions unchanged; reversal-only, atomic replacement, correction of a
  replacement, duplicate reversal rejection and unsupported dependency/source rejection.
- Fault injection after reversal, replacement, evidence and receipt writes; connection interruption
  and rollback leave neither half-corrections nor successful receipts. Lost-response retry returns
  the same complete result; changed payload/actor conflicts.
- Independent overlapping SQL connections with controlled barriers for post-versus-close in both
  orders, correction-versus-close, duplicate correction and two different requests correcting the
  same original. Assert complete results and absence of partial state; no sleep-based race claims.
- Permission revocation before replay, tenant isolation, direct DML/kernel execution denial and
  absence of synthetic adapters/grants in production migration and re-provisioned principals.
- Readback links/digests, unchanged report pagination/totals, September/October and recorded-time
  cutoffs, bounded period queries, unauthorized reads and decimal-string serialization.
- Fresh/upgrade migration, preserved historical snapshots and receipts, refusal of destructive
  Down, and unchanged PO/setup workflows with no public financial-write or close route.

Target mutation work at period guards, inverse derivation, uniqueness, authority/replay order and
rollback boundaries. Use available .NET mutation tooling plus isolated SQL mutation experiments;
report surviving/equivalent cases and unavailable tooling honestly.

Implementation delivery requires `scripts/verify.ps1`, `scripts/smoke-container.ps1`, OpenAPI drift
checks and the isolated preview started/refreshed through `scripts/dev-up.ps1`. Exercise authenticated
period/report reads and existing setup in the browser; financial seeds and correction/close commands
remain confined to disposable test databases. Give the preview URL and explicit coverage limits.
Update living accounting/architecture and migration documentation only when implementation makes
these contracts current. Obtain internal review and deliver a ready-for-review PR; merge, production
operations, retained-file holds and activation remain outside this implementation authorization.

## Review handoff

Scope approval permits this written design. Written-spec approval precedes an implementation plan;
the plan then needs review and execution-method selection before runtime changes. This document
does not select an execution method or inherit BK-02's agent/model choices.
