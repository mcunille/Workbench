# Accounting architecture

[Return to the main entry](ARCHITECTURE.md).

### Accounting configuration

BK-01 adds tenant-scoped accounting policies, typed general accounts, controlled mapping candidates,
and complete-statement coverage planning. It creates no financial entries or balances and never
activates bookkeeping. [Accounting setup](accounting.md) describes the user workflow.

Accounting administrator and Accounting reader are fixed roles with explicit permission claims;
existing `TenantUsersManage` authority assigns them through a restricted command. Neither role is
granted automatically. Role/claim tables deny direct runtime mutation, and accounting permissions
are resolved only from roles. Assignment and user-state changes serialize to preserve access checks.

`Accounting.Accounts`, `Configurations`, `Revisions`, and `Receipts` have tenant RLS and restricted
writes through `Accounting.Save`. The command validates current session/role authority, serializes
tenant accounting changes, validates mapping and coverage references, and commits state, revision,
audit, and durable replay evidence together. UUID version tokens reject stale edits. Archived accounts
retain their codes and history. The [BK-01 design](specs/2026-09-21-bk-01-accounting-foundation.md)
defines later first-posting, retention, cutover, and reconciliation obligations.

### Atomic journal foundation

BK-02 adds immutable source events, journal entries/lines, posting receipts and a first-posting policy
freeze. The internal `Accounting.PostJournal` procedure requires an outer transaction and has no
runtime execute grant. Future typed source commands own their authorization, source revision and
transaction; the kernel validates exact same-currency balanced entries at the SQL boundary. No
production source adapter or financial write endpoint is enabled in this increment.

The kernel and configuration writer share the tenant accounting application lock. First posting
freezes currency, scale, fiscal calendar and planned start boundary; any referenced account is
ineligible for archive until a later design can prove historical/open-item safety. Tenant-qualified
keys, RLS, direct-DML denials and source/request uniqueness protect atomic history across replicas.
Amounts use decimal(28,4), checked wider accumulation and scale 0–4; no silent rounding is allowed.

Journal and trial-balance GET APIs independently require `AccountingReportsRead`, use exact decimal
strings and capture each response in a SERIALIZABLE read. Posting and recorded-time cutoffs apply
together. Recorded time is not commit order and paginated reads are not frozen export snapshots.
The conservative tenant lock shares configuration, posting and archive coordination across replicas.
Narrowing it requires measured contention and an explicit lock-order design; an in-process mutex
cannot replace it. Typed source commands check current authority before receipt replay, then validate
mutable source/account state for new writes. Exact retries return the original outcome; a different
request for an already posted source event conflicts. Event uniqueness prevents duplicate postings
independently of request identity. Snapshot and canonical-command digests use SHA-256 over stored
SQL Unicode text bytes (UTF-16LE without a byte-order mark).

BK-03 adds durable calendar months, immutable closures and receipts, and correction groups with exact
inverse and optional replacement entries. Posting and correction use the same tenant lock as closing;
new commands reject a closed month, while matching committed receipts replay after closure only with
current authority. Closing an empty month freezes the calendar without fabricating a first journal.
The restricted `Accounting.ClosePeriod` and `Accounting.CorrectJournal` procedures have no runtime
execute grant. New tables use tenant RLS, tenant-qualified links and denied direct runtime writes.
Only disposable SQL tests install a synthetic writer; no production source or close adapter exists.

All new reversal, replacement, internal receipt and group rows in one correction share a single
database-owned recorded time. This makes recorded-time report cutoffs include a complete correction
atomically while preserving the original committed records. Journal detail adds current correction
relationships; the period GET reports explicit ranges of up to 120 months without materializing an
open month. Bookkeeping activation, production sources, reconciliation acceptance, public close
and correction actions remain gated by the parent prerequisites. BK-07 supplies financial-file retention.

Periods are materialized on demand, avoiding a fixed calendar horizon. A future production close
adapter must validate reconciliation evidence under the same accounting lock, or compare a recorded
ledger version after acquiring it; a stale pre-lock checklist cannot authorize closure. Reversals
derive exact inverse lines and account snapshots from persisted history. Replacements use current
eligible mappings and account revisions. A correction appends history; it does not establish that
money was refunded. [Accounting readback](accounting.md#journal-read-apis) owns report semantics;
the [migration matrix](operations/database-migrations.md#migration-compatibility-matrix) owns retained-history
and replay compatibility.

### Classified purchase recognition

BK-04 adds Purchasing-owned immutable units, side events, components, explicit matches, correction
groups and receipts. Restricted `Purchasing.PostRecognition` and `Purchasing.CorrectRecognition`
derive journals through the accounting kernel in the source transaction; neither has a runtime
execute grant. There is no public financial write route or activated production source permission.
Tenant-qualified links, RLS and direct-write denials protect the new evidence tables.

Whole units carry Expense or Inventory classification. Receipt-first posting accrues cost; invoice-first
posting records prepayment. Matching clears historical account mappings and posts reviewed differences.
Strict typed input, exact currency-scale arithmetic, source-wide capacity across surviving revisions,
one bounded invoice rounding component, and posting-date floors reject unsupported matches. A shared
tenant accounting lock serializes configuration, source authority, PO supplier identity, posting,
correction and closing. Once any recognition history exists, the PO supplier identity is frozen.

Corrections discover the complete stored dependency group and append inverses and optional replacement
events with one database-owned instant. The generic accounting kernel refuses isolated correction of
a recognition journal. Authorized journal detail includes immutable recognition and matching evidence;
posting-date and recorded-time report filters continue to select financial activity independently.
Replacement creates a fresh successor unit linked to its predecessor and correction group. Original
classification and quantity remain immutable; consumers follow successor links across corrections.
Capacity and the single rounding allowance count surviving claims across all revisions of the
logical source. A new source revision creates no new allocation budget. Released capacity becomes
reusable only from the correction posting date, including claims against later source revisions.

An invoice-only recognition policy would be smaller, but would not establish control transfer or
prevent double-counting uninvoiced receipts. The two evidenced sides establish those rules before
public bill/receiving workflows exist. Bookkeeping remains unavailable; [recognition policy and
examples](accounting.md#internal-purchase-recognition) describe the supported cases and limitations.

### Structured supplier bills

BK-05 adds a Purchasing-owned bill head, immutable revision/review/evidence tables, posting links and
command receipts. Restricted SQL commands enforce tenant-qualified identity, versions, current
permissions, exact totals and duplicate resolution. The posting adapter constructs BK-04 invoice
events from the stored reviewed revision in the same transaction; generic corrections cannot detach
bill-owned financial evidence. Read procedures expose bounded history and live file availability.
The same tenant accounting lock serializes bill commands with period closing and PO changes.

The single additive migration installs no runtime mutation grants or production role assignments;
`BookkeepingAvailable` remains false. Public bill entry, allocation and bill corrections are separate
release work; BK-07 supplies evidence holds. Posting derives invoice identity, revision and capacity
from reviewed stored bill components, never caller-authored recognition JSON. An application-only
bill store would not enforce source authority or replica races at the database boundary; reusing
the recognition and journal kernels avoids a second AP writer. [Bill policies](accounting.md#internal-supplier-bills)
own duplicate resolution, pro-forma identity and evidence limitations.

### Supplier open items and allocations

BK-06 adds immutable Purchasing-owned items, signed movements, applications/inverses, payment
sources/corrections, control attribution, financial groups and canonical receipts. Tenant-qualified
keys, RLS and denied direct runtime DML protect them. One group has a database-owned recorded instant;
item/application/payment versions are coordination metadata, not mutable balances. Structured bill
posting and supported non-bill recognition corrections derive item evidence atomically from stored
source/journal ownership. Gross funding capacity and net journal effects remain distinct, including
fully allocated payments with no advance journal line.

Restricted commands enforce decimal(28,4), configured scale, one supplier/PO/currency, current
authority before replay and historical capacity on both sides at every affected posting boundary.
The tenant Accounting exclusive transaction lock precedes ordered PO/source, item/application and
document locks. Payment correction recomputes a bounded versioned plan/fingerprint under those locks,
inverts embedded and standalone effects according to their exact source ownership, and optionally
posts a replacement. Generic corrections cannot detach supplier item/application history. Source-owned
bill/credit correction participants are tested through disposable adapters; those business workflows
are not installed. Commands and correction closures are bounded to 262,144 bytes and 1,000 targets/events.

Four GET accounting routes expose items, detail, history and reconciliation with AccountingReportsRead.
Under a shared Accounting lock, each transaction captures journal/group sequence ceilings and reads
tenant-qualified immutable metadata into an in-memory snapshot, then commits and disposes the
transaction before indexing evidence and computing responses. Protected cursors bind those ceilings,
tenant, route, filters, page size and inclusive posting/recorded cutoffs. Exact fixed-unit BigInteger
aggregation compares all four control families against independent historical journal totals, including
archived mappings and unknown or zero-net unattributed activity. Source validation follows durable
source/revision/correction/receipt links and exact historical journal ordinals, not equal amounts or
timestamps alone. The web role has narrow read access to three RLS-protected proof/identity functions.

Pages contain at most 200 rows; this does not bound capture or processing. Duplicate-preserving keyed
lookups replace repeated whole-history scans, and indexing, projection and reconciliation observe
request cancellation outside the shared Accounting lock. Snapshot capture still holds that lock and
loads metadata proportional to tenant history; capture memory and latency remain unmeasured. No
throughput, latency or maximum-history bound has been established.

The single forward migration follows merged BK-05. Supported recognition attribution can be rebuilt
only after full source/group/item/movement/journal identity proof; it adds missing derived attribution
without changing financial history. Conflicting, detached or unknown evidence remains unresolved.
There is no runtime repair API. Runtime financial mutation grants and production write-permission
assignments remain absent; `BookkeepingAvailable` stays false. Public financial
entry remains a separate gate. See [supplier read APIs](accounting.md#supplier-open-items-and-read-apis),
[migration/recovery boundaries](operations/database-migrations.md#supplier-open-item-migration-and-recovery)
and the [BK-06 specification](specs/2026-09-27-bk-06-supplier-open-items-and-allocations.md).

### Supplier credit and cash sources

BK-08 keeps credit notes, incoming refunds and outgoing refund repayments as separate immutable
Purchasing sources. Source identity includes kind, ID and revision. Cash settlements consume exact
historical control positions. A noncash release opens a clearing item tied to the original incoming
settlement. Cash recording errors use linked original journal inverses. Bill and credit corrections
append recognition or credit successors without rewriting the cash movement.

The shared Accounting lock covers the complete authorized correction and affected posting dates.
Each correction has a finalized owner receipt, typed original and successor links, and exact journal
membership. Report capture uses retained owners and the original account revisions to prove the
four control families. Typed item and journal history preserve the captured cutoffs and ceilings.
See [credit and refund behavior](accounting.md#internal-supplier-credits-refunds-and-corrections).

### Durable financial evidence

BK-07 adds immutable tenant-qualified evidence sets, revision links, additions and disposal receipts
at authentic source posting. RLS and denied runtime DML protect ownership; no public financial writer
or supplement editor is enabled. Frozen policy deadlines use the later posting/recorded anchor;
legacy links are indefinite. Corrections inherit exact authenticated evidence without reacquiring
missing bytes or changing original hashes. Free-text references do not invent ownership.

Accounting coordination precedes the PO lock; stable attachment-before-revision acquisition
serializes capture, removal and disposal. Cleanup takes no PO/accounting lock after its attachment
lock. SQL guards protect preparation, completion, generic deletion and old-worker compatibility via
effective `Held`, while independent holds remain separate. Explicit authorized disposal records all
link membership and schedules seven-day grace; physical deletion revalidates it under the lock.

Authorized readback separates retention from availability and reauthorizes every replay. Recovery
fingerprints bind links, frozen policy, receipts, holds and removal state; protected removed/purged
identities remain required content until disposal. SQL manifests drive strict recovery and online
capture completeness. Missing bytes require explicit recovery disposition and cannot advance backup
freshness. Backup-set expiry remains independent of authoritative financial retention.

See [accounting invariants](accounting.md#durable-financial-evidence) and
[migration/recovery operations](operations/database-migrations.md#financial-evidence-migration-and-recovery).
