# BK-02: atomic double-entry journal

**Status:** Implemented — owner approved on 2026-09-23.

Parent: [PO-07 bookkeeping prerequisites](2026-09-20-po-07-deposits-and-payments.md).
Prerequisite: [implemented BK-01](2026-09-21-bk-01-accounting-foundation.md).

## Outcome and scope

Give future financial source commands one exact, immutable journal boundary. A successful command
must leave its frozen source event, balanced journal and durable receipt together; a failure leaves
none. Independent replicas and retries must identify the same event without duplicate postings.
Deliver the parent specification's BK-09 basic journal and trial-balance readback alongside BK-02.

This increment includes schema, an internal SQL posting kernel, first-posting policy protection,
account archive protection, restricted read APIs and adversarial integration tests. It introduces
no financial write endpoint, manual journal editor, bill/payment adapter, opening-balance import,
report UI or export format. Existing POs and documents never generate accounting entries.
BookkeepingAvailable remains false. BK-03 owns periods/close and correction commands; BK-04 owns
recognition; BK-06 owns supplier dimensions/open items; BK-07 owns retained private evidence;
BK-10 owns acceptance of a real cutover. No production posting adapter is enabled by this delivery.

The journal is usable infrastructure, but real bookkeeping remains blocked by the parent gates.
Tests install a narrowly typed synthetic source adapter only in disposable test databases. Its
procedure, execute grant and routes must not occur in production migrations, binaries or startup.

## Existing boundaries

- `Persistence/AccountingSchema.cs` installs restricted `Accounting.Save`, tenant RLS and direct
  runtime DML denials. It serializes accounting changes with a transaction-owned tenant application
  lock named `Accounting:<tenant UUID>` and validates current authority before receipt replay.
- `Persistence/AccountingModel.cs` contains configuration, account, revision and receipt mappings.
  Configuration is versioned JSON; account type/purpose are immutable, descriptions can change.
- `Accounting/AccountingEndpoints.cs` exposes setup commands and reads. Its route group requires
  configuration-read permission, which must not be inherited by the new reporting routes.
- `Authorization/WorkbenchPermissions.cs` already defines AccountingReportsRead. Both accounting
  roles receive it; an accounting reader does not require configuration authority to read reports.
- The current merged schema is `20260921051843_AddAccountingFoundation`. No journal exists to
  migrate or infer from estimates. The design was checked against checkout commit `53dc9e9`.

## Durable records

All tables use tenant-qualified foreign keys, tenant RLS filter/block predicates and restrictive
deletion. No cascades delete financial history. Stable UUIDs identify records; a database-generated
bigint journal sequence provides deterministic order but is neither gap-free nor a commit clock.

| Record | Required contents and invariants |
| --- | --- |
| Ledger policy freeze | One per tenant; configuration revision, functional currency/scale, fiscal start month, start approach/date, first journal ID and UTC time. Inserted atomically with the first journal. This freezes a planned boundary, not BK-10's acceptance of opening balances. |
| Source event | ID, tenant, typed source kind/ID/revision, event kind, rule version, actor, document/effective/posting dates, reference/reason, immutable bounded canonical source snapshot and digest. Unique tenant/source-kind/source-ID/revision/event-kind identity. Rule version is evidence, not a way to bypass uniqueness. |
| Journal entry | ID, tenant, sequence, unique source-event ID, configuration revision, currency/scale, three dates, database UTC recorded time, actor, reference/reason and exact debit/credit totals. One journal per nonzero source event in BK-02. |
| Journal line | Tenant, journal ID, ordinal, account ID, account version and code/name/type/purpose snapshots, debit and credit. Unique ordinal per entry. Exactly one positive side; neither negative nor both positive nor both zero. |
| Posting receipt | Tenant/request UUID, original actor, source-command kind/version, canonical input and fingerprint, source-event/journal IDs and original result. Insert-only; independent from BK-01 configuration receipts. |

Store canonical source evidence with its owning event, not solely a pointer into a mutable draft.
Snapshot and canonical-input digests use SHA-256 over the stored SQL Unicode text bytes
(UTF-16LE without a byte-order mark); readback exposes the snapshot digest as hexadecimal.
Future source adapters must freeze their domain revision in the same transaction and retain a
typed reference to it. A source event is not permission to put arbitrary JSON or a claimed rule
name through a runtime posting endpoint. BK-02 has no caller-controlled adapter registry.
Supplier dimensions and correction relationships require their later typed adapters and additive
schema changes; do not add unenforced arbitrary relationship fields now.

## Exact amounts and bounded inputs

Use SQL decimal(28,4) for persisted amounts and totals, with supported posting scales 0–4.
An entry has 2–1,000 lines; each side's total must fit decimal(28,4). Accumulate with decimal(38,4)
in SQL, check the total bounds before conversion, then require debit total = credit total > 0.
This bound accommodates 1,000 maximum-size inputs without overflowing the accumulator.

Validate canonical unsigned decimal strings before numeric conversion: no exponent, separator,
sign, whitespace, more than 24 integer digits or more than four fractional digits. At the selected
scale reject any nonzero excess precision; SQL conversion must never silently round it. Canonicalize
semantically equal inputs deterministically and retain a version for this canonicalization contract.
Report amounts as fixed-scale decimal strings; never use JavaScript numbers for money. C# uses
decimal only where its range suffices; wide SQL report totals are serialized directly as strings.

Reject unknown/duplicate input properties, malformed UUIDs/dates, oversized strings and arrays before
conversion or persistence. Bound source snapshots/canonical commands to 256 KiB, type/event identifiers
to 64 characters, references to 200 and reasons to 2,000; reject rather than truncate. Adapters own
source precision and approved rounding rules. The kernel never invents a rounding account or line.
Zero-value source records belong to their source workflow and do not call the journal kernel.

## Atomic SQL boundary

Use a stored procedure kernel with no EXECUTE grant to web, worker, public or ordinary runtime roles.
Runtime principals have SELECT as needed under RLS, and explicit INSERT/UPDATE/DELETE denials on all
new accounting records. Extend provisioning and permission probes so re-provisioning cannot reopen
the boundary. Trusted same-owner source procedures will call the kernel through ownership chaining;
do not use caller-set SESSION_CONTEXT flags, dynamic SQL or a publicly executable generic wrapper
as authority. The first real adapter must add its specific permission and recognition policy.

The typed source procedure owns the outer transaction using XACT_ABORT and TRY/CATCH rollback.
The kernel requires an active transaction and never commits its caller's transaction. It accepts
trusted typed-source results, revalidates all journal invariants in SQL, and writes the source event,
journal, lines, freeze and receipt. A runtime caller cannot insert an incomplete journal and then
commit it: no runtime table writes or standalone kernel execution are permitted. SQL CHECK/FK/unique
constraints enforce row identities and amounts; cross-row balance is enforced by the restricted
SQL command. This is a runtime-principal boundary, not protection against a database administrator
who can alter permissions, procedures or constraints.

Command ordering:

1. Begin the transaction, validate tenant/session/current source-action permission, then acquire
   the exact existing exclusive tenant accounting application lock. After acquiring it, revalidate
   current authority before any replay or mutation. Use a bounded lock timeout.
2. Check request receipt before mutable source-version/account-state checks. A matching command from
   its original actor returns its original result after current authority validation. Changed actor,
   command or normalized input with that request ID conflicts. An old receipt never reapplies a write.
3. Validate source identity/revision under source locks. A different request ID for an already posted
   event returns a conflict identifying the existing event to an authorized caller; it never appends
   another journal. The original request ID is the retry mechanism after a lost response.
4. Validate configuration, expected configuration revision, source date boundary, active same-tenant
   accounts and exact amounts. Account revisions used to derive lines must match the locked state;
   adapters revalidate mappings and derive their approved account selections inside this transaction.
5. Freeze the source revision and append all records plus the existing transactional audit event.
   Audit records contain IDs and action metadata, not private source payloads. Capture database UTC recording time once for
   the event/journal/receipt. Insert the policy freeze if absent. Return only after the outer commit.

The global tenant accounting lock is intentionally conservative: it reuses BK-01's synchronization
domain, avoids configuration/post/archive races, and makes correctness auditable. No in-process mutex
can replace it. Future narrowing needs measured contention and an explicit lock-order design. All
source commands acquire this lock before source/account locks; BK-03 close must share this boundary.
Deadlock/timeout yields a retryable conflict and no partial effects. Retrying uses the same request ID.

The test adapter must exercise the production kernel through a restricted principal and ownership
chain. It derives two lines from a synthetic amount and two General accounts, validates a test source
revision and action permission, and freezes a test source row. It does not accept arbitrary lines.
Privileged adversarial tests can call the internal kernel with malformed lines to prove SQL rejection.
Fault injection lives exclusively in the test adapter, including failure after journal creation but
before outer commit. Tests must prove a kernel call outside a transaction fails.

## Configuration and account lifecycle

First posting requires configured currency/scale, fiscal start month, start approach and start date;
reject posting dates earlier than that boundary. Remaining setup/release requirements are mandatory
for a future production adapter. Synthetic tests do not imply production readiness. Document and
effective dates are retained independently; recorded time is server-owned UTC, not a browser date.
Before any production adapter receives an execute grant, its design must require BK-10's recorded
acceptance of the starting books and the remaining applicable release gates. Neither the policy
freeze nor a complete BK-01 setup constitutes that acceptance; BK-02 adds no activation override.

Extend Accounting.Save under its existing lock to reject changes to currency, scale, calendar,
start approach or start date once a policy freeze exists. Other permitted configuration changes
append revisions as today. An intervening configuration change makes a proposed posting's expected
version stale; it must fail rather than reinterpret amounts. Descriptive account edits remain
allowed; journal snapshots preserve historical meaning.

Until BK-06 can prove balances and open items are resolved, reject archive of any account referenced
by journal lines, even if its current net balance is zero. This is deliberately stricter than a
zero-current-balance check, which cannot establish historical or supplier-item safety. Existing
mapping/coverage archive restrictions remain. Unused accounts can still be archived/restored.

## Read contracts

Add a separate authenticated `/api/beta/accounting` reporting route group requiring
AccountingReportsRead and private/no-store responses. Tenant comes from the authenticated context.
There is no new write permission or posting HTTP route.

| GET route | Result |
| --- | --- |
| `/journals` | Bounded journal headers with source identity, dates, actor, currency/scale and exact totals; default page 50, maximum 100. |
| `/journals/{id}` | Immutable header, ordered lines, account snapshots and source-event evidence; unavailable tenant-scoped IDs return 404. |
| `/accounts/{id}/journal` | Account lines with journal/source IDs and debit/credit strings, ordered by posting date, sequence and ordinal. No mutable running balance. |
| `/trial-balance` | Debit/credit activity and signed debit-minus-credit balance per referenced account; grand debit and credit totals. Includes archived historical accounts. |

All collection/report queries accept inclusive postingThrough (date) and recordedThrough (UTC instant).
Default postingThrough to 9999-12-31 and capture a database UTC recordedThrough for the response;
echo both and bind every cursor to tenant, route, filters and cutoffs. Use keyset pagination with
deterministic sequence/ID ties and reject invalid or mismatched cursors with 400. Trial balance is
paged by stable account ID, default 50/max 100, with whole-filter totals distinguished from page totals.
Each response computes rows and totals in one SERIALIZABLE database read transaction, consistent
with existing repository read patterns; ordinary READ COMMITTED across multiple statements is
insufficient. Test a report overlapping a posting and handle deadlock/timeout as a retryable conflict
without returning partial results. Empty books return
empty rows and explicit zero totals; label data as recorded journal activity, not reconciled books.

Use SQL decimal(38,4) accumulation for reports and return exact strings; an aggregate beyond that
range fails explicitly without returning partial totals. Individual entries remain decimal(28,4).
Rows preserve posted account snapshots; trial-balance identity is the stable account UUID and may
show the current code/name, explicitly distinguished from historical labels in journal detail.

Recorded time is a recording timestamp, not a SQL commit watermark. An in-flight transaction with
an earlier timestamp may become visible between pages or later repeats of the same cutoffs. BK-02
does not promise a frozen multi-request export. Immutable export snapshots/commit-watermark semantics
need the separately scoped export design. Each response is internally consistent and every included
event satisfies both cutoffs; sequences must not be described as commit order.

## Migration, compatibility and recovery

Create one new migration after the merged BK-01 baseline. Add the schema/model and SQL kernel, update
Accounting.Save with freeze/archive guards without editing its historical migration, and advance
readiness/backup schema markers and migration assertions together. Preserve BK-01 receipts and replay
bytes. Install no test adapter. Existing databases receive no postings, freezes or invented opening
balances. Production setup remains unavailable for bookkeeping even after migration.

Verify both fresh creation and upgrade from the merged base, including pre-existing account/config
revisions, role grants and receipt replay. Stop incompatible older writers for schema deployment;
deploy the matching application. Down migration must refuse destruction of accounting evidence;
recovery is a forward fix or separately authorized guarded restore. No production operation is part
of implementation approval.

## Approved implementation decisions

1. Prefer a restricted SQL kernel over application-only validation: the latter permits bypass by
   direct runtime SQL and cannot establish the required database boundary.
2. Prefer test-only typed sources over an early manual journal or payment endpoint: the latter
   invents authorization/recognition behavior reserved for later stories.
3. Prefer one tenant accounting lock initially over per-account locks: lower concurrency is accepted
   in exchange for sharing BK-01's established configuration/archive synchronization.
4. Prefer blocking archive of every used account until BK-06 over inferring safety from net zero.
5. Deliver read APIs now; defer report UI, export snapshots and supplier reconciliation. The parent
   explicitly calls for basic journal/trial-balance readback, not the finished BK-09 workflow.

## Acceptance and verification

Use TDD with GIVEN/WHEN/THEN comments and meaningful red evidence before changed behavior. Required cases:

- Exact balance, line count, one positive side, scale 0–4, maximum values, excess precision, overflow,
  malformed/duplicate fields and mismatched currencies fail or succeed at the SQL boundary as specified.
- Source/journal/receipt/freeze commit together. Inject failures before and after each write group;
  outer rollback and connection interruption leave no financial residue or false success receipt.
- Two independent connections post the same request concurrently: one journal and identical results.
  Different requests for the same source event create one journal and one conflict. Changed-payload
  retries conflict. A lost response followed by original-request retry returns the original IDs.
- Tenant/actor/session/permission failures hold through HTTP, RLS and actual restricted SQL principals;
  revoked authority cannot replay a receipt. Direct kernel execution and all financial DML are denied.
  Test grants are absent from migrated and re-provisioned production principal definitions.
- Real overlapping SQL connections exercise first post versus configuration edit, post versus archive,
  and mapping/account revision changes. The loser conflicts or validates the winner's state; no stale
  policy/account selection is committed. Use controlled barriers, not sequential simulations or sleeps.
- Readback traces every line to source and journal; exact trial balance ties at both cutoffs, including
  backdated entries and later-recorded events. Test pagination, aggregate bounds, archived-history
  visibility, empty books and independent reporting permission without setup authority.
- Fresh/upgrade migration preserves all prior data and receipt replay; old PO commitment/file flows
  still create no journal. Synthetic adapters are absent from shipped artifacts.

Mutation work targets balance/scale/authority/uniqueness and freeze/archive guards. Use available .NET
tooling and isolated SQL mutation experiments for procedure branches that .NET mutation cannot reach;
report meaningful survivors, equivalent mutations and unavailable tooling explicitly. Complete current
source `scripts/verify.ps1`, `scripts/smoke-container.ps1`, generated OpenAPI drift checks and isolated
preview verification. Exercise authenticated report requests and unchanged setup in that preview;
seed financial examples only in disposable test databases. Provide the preview URL and actual limits.

Implementation uses GPT-6 Sol subagents at Medium reasoning, with exclusive file ownership and
serialized database/build work. The primary agent integrates, inspects actual diffs/test evidence,
obtains independent internal review, commits and opens a ready-for-review PR. Approval of this design
authorizes that delivery; merge and production activation remain separately authorized.
