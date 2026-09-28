# BK-05: structured supplier bills

**Status:** Proposed written specification. The owner approved the internal-foundation scope;
this specification and its subsequent implementation plan require separate review before code.

Parent: [PO-07 bookkeeping prerequisites](2026-09-20-po-07-deposits-and-payments.md).
Prerequisites: [BK-02](2026-09-23-bk-02-atomic-journal.md),
[BK-03](2026-09-24-bk-03-corrections-and-period-controls.md), and
[BK-04](2026-09-25-bk-04-classified-purchase-recognition.md).
Baseline inspected: `50bef8f` (merged BK-04, PR #181).

## Outcome and delivery boundary

Give purchasing durable, reviewed supplier bills whose liabilities are constructed from their
stored source records through BK-04. A bill is distinct from a PO estimate, uploaded invoice,
recognition event, payment, and allocation. Multiple bills may belong to one ordered PO.

Deliver internal draft/review/post commands, immutable revisions and posting snapshots, private
document references, duplicate resolution, SQL-enforced authority, and bounded readback queries.
Keep `BookkeepingAvailable` false. No public bill-write endpoints, bill-entry UI, activation
override, payment, allocation, or production bookkeeping ships in this change. Ordinary PO and
document workflows remain usable without accounting configuration.

The production schema contains the source adapter, but the web principal has no EXECUTE grant on
bill mutation procedures. Disposable test wrappers and permission assignments exercise it and
are absent from production migrations and published output. This matches the foundation staging
of BK-02–04; it is not completion of the eventual user-facing PO-07 workflow.

BK-07 remains responsible for physical evidence holds, retention, cleanup and recovery guarantees.
BK-05 stores evidence identity and metadata honestly without claiming that linked bytes are held.
Production activation remains blocked by that and the other PO-07 release prerequisites.

## Existing boundaries and approach

`Purchasing.PostRecognition` already enforces recognition-unit identity, source capacity, exact
components, matching, periods and balanced journals inside an outer transaction. Its caller is
trusted to supply source-derived eligibility and capacity. Today only disposable fixture adapters
establish that trust. BK-05 supplies a durable invoice source; it must construct the recognition
command from stored bill data rather than forward caller-provided recognition JSON.

Use separate bill persistence/model, validation, command and query units alongside the existing
purchasing code. Reuse the journal and recognition kernels; do not add a second AP posting path.
Readback uses bill-to-recognition links to explain journal effects, not a mutable balance column.

Alternatives considered: a public bill workflow would require additional product and release
gates beyond the approved foundation; an application-only bill table would leave source authority,
immutability and replica races outside the database boundary. Neither is selected.

## Source model and lifecycle

Use tenant-qualified bill identities and foreign keys throughout. A bill header identifies its
ordered PO, supplier and currency, current revision and concurrency version. Those three business
identities are fixed at creation; changing them requires abandoning the draft and creating another.
Capture the PO revision and supplier display snapshot in each reviewed revision and final posting.
Supplier contact edits do not rewrite those snapshots.

Lifecycle: `Draft` -> `Reviewed` -> `Posted`, with `Abandoned` available before posting. Editing a
reviewed bill creates a new immutable revision and returns it to Draft. Abandonment preserves
revision history and creates no journal. Posted and abandoned bills cannot be edited or deleted.
Review records actor, database UTC time and rationale against an exact revision. The same actor
may draft and review; this release does not impose a two-person approval rule.

Each revision records:

- Bill kind (`Invoice` or `ProForma`), original supplier reference and normalized duplicate key.
- Document date, effective date, proposed posting date, optional due date and terms text.
- Exact declared total, currency, stable cost-unit identities and classified component breakdown.
- Explicit invoice eligibility, tax-policy and variance evidence required by BK-04.
- Optional private document links and an explicit missing-document indicator.

Incomplete drafts may omit posting-required data but cannot contain malformed values or foreign
references. Review requires an invoice reference, all three dates, due date or nonblank terms,
complete units/components, and a total equal to their exact sum. A due date before document date
is rejected; effective date cannot follow posting date. Do not derive dates from upload time or
infer liability from a due date. Posting revalidates accounting-start and open-period rules.

Pro forma records can be saved and reviewed as non-posting source records; Post always rejects
them. A later actual invoice is a new bill with an optional tenant-qualified predecessor link,
not conversion of the pro forma's history into an invoice. Its duplicate warning still needs
resolution when the same supplier reference is reused.

Bill corrections are outside this delivery. Do not expose a bill reverse/replace command or
permit generic journal/recognition correction to bypass bill ownership. Reserve source ownership
checks so that later bill correction must update the complete source and recognition dependency
graph atomically. A posted bill remains immutable even when correction support is unavailable.

## Components and recognition adapter

Use BK-04's exact decimal conventions: money `decimal(28,4)` with configured currency scale,
quantities `decimal(28,6)`, and decimal strings at command boundaries. Reject excess scale and
overflow before conversion can round values. No floating point or silent rounding.

Each cost unit has a stable source-component identity, positive quantity/unit, goods or service
reference, eventual Expense/Inventory classification, and explicit components for base cost,
discount, freight/charges, nonrecoverable tax, recoverable tax and rounding where present. Apply
BK-04's assignment, sign and rounding rules. Declared total must equal the component-derived total;
a zero-total invoice may preserve evidence without inventing a zero-only journal.

Post consumes the complete reviewed bill once. Each bill cost unit maps to exactly one recognition
unit in this release; partial invoices use separate source units/bills before posting. No caller
can invent subdivisions to increase source capacity. Derive invoice source identity, revision,
quantity and capacity from the immutable reviewed revision; keep an explicit bill-unit/event link.
The generated recognition envelope remains within BK-04's 1,000-unit and 262,144-byte limits.
Validate the generated envelope size before committing source state.

For an unmatched unit, require reviewed evidence of an eligible present obligation and enforceable
future right; post through BK-04's invoice-first Prepayment and tax rules. For an existing recognition
unit, require its ID and expected prior event revision; verify supplier, PO, currency, classification,
quantity and goods identity. Never match by total or copy recognition estimates from the PO.

Expense variances require BK-04's explicit rationale and original classification. Inventory variances
requiring proof that inventory remains held reject in this release: a bill reviewer assertion cannot
replace the missing trusted inventory-state adapter. Equal-value receipt-first inventory matching
remains supported. Unsupported tax entitlement or recognition eligibility also fails closed.

BK-05 creates invoice sides only. It does not mint receipt/service-recognition evidence or claim
that reviewing an invoice proves control transfer. Tests may establish prior recognition through
BK-04's disposable adapter; no such adapter is shipped to production.

## Duplicate references and recorded resolution

Detect duplicates across all POs for the same tenant and supplier, independently of currency.
Retain original reference text. Version 1 normalizes by trimming ASCII spaces/tabs/CR/LF,
collapsing runs of those characters to one space, and uppercasing ASCII a–z. Preserve punctuation,
leading zeros, accents and non-ASCII characters; compare normalized keys ordinally. This
deterministic conservative rule avoids merging potentially distinct references and makes no
claim to detect visually equivalent Unicode or every supplier's numbering convention.

Draft save permits duplicates and returns the matching bill identities. Review and Post require
a recorded resolution for every other non-abandoned bill with that normalized key, including
pro forma records and previously posted bills. A resolution records the reviewing actor/time,
current bill revision, conflicting bill ID/revision and nonblank reason for why these are distinct
legitimate records. An acknowledgement flag alone is insufficient. A confirmed duplicate is
abandoned before posting; there is no force-post duplicate operation.

Recompute conflicts under the accounting coordination lock on review and post. Resolution covers
only the exact reviewed conflict set. A new conflicting bill or changed conflicting revision
invalidates that coverage and returns a conflict; the draft/review data remains available for
explicit re-review. Concurrent duplicate creates and posts must not bypass this check. Serialize
all bill-reference changes through the same lock, including abandonment.

## Evidence and readback

Resolve attachments through the existing PO document and immutable attachment revision records.
Require the same tenant and PO; capture document identity, revision identity, digest, length and
available descriptive metadata from stored records, never trust supplied digest/availability claims.
Reject a requested link that is removed, foreign, incomplete or marked unavailable by recovery.
Validate availability again at review and posting. Metadata-only bills are permitted with explicit
missing-evidence status and reviewer rationale; never manufacture an attachment for them.

Use the document workflow's existing coordination when reading/linking revisions. A removal that
commits first causes linking/posting to reject. A removal after posting remains possible until
BK-07 supplies holds: preserve the snapshot and report linked bytes unavailable rather than
erasing financial history or claiming retention. This limitation is a production release blocker.

Internal bounded queries return bill revisions, review/resolution history, document availability,
posting receipt and linked recognition/journal IDs. Paginate lists and histories with stable
ordering; detail reads must respect the bounded source envelope. Reads apply tenant isolation and
explicit business/report authority. Document download retains existing private-file authorization;
bill read access alone grants no new download authority. No outstanding/paid balance is invented
before BK-06, and zero AP is not purchase completion.

## Authority, atomicity, retries and concurrency

Define fixed `SupplierBillsManage` and `SupplierBillsPost` business permission identifiers.
Create, revise, abandon and review require Manage; Post requires both. No procedure accepts a
caller-selected required permission. Bill queries require Manage or AccountingReportsRead.
Do not automatically assign these new write permissions to accounting roles in production.
Disposable fixtures assign them explicitly to prove allowed and denied cases.

Validate current actor/session/tenant authority before any successful-receipt replay. Every command
uses tenant, operation, request UUID and normalized complete payload, including expected versions,
to identify its immutable receipt. Reject unknown/duplicate JSON properties, wrong types, oversized
text and invalid UUIDs before narrowing values. Bind receipts to actor as existing kernels do.
Same identity and payload returns the original result; changed input conflicts. Successful replay
does not revalidate subsequently changed source state. Read failures never require another post.

Post accepts bill/revision identities, expected bill and PO versions, expected configuration version
and request identity. It does not accept replacement amounts, eligibility flags, account IDs or
arbitrary recognition JSON. The adapter derives the complete command from the reviewed revision.
Derive and persist an internal recognition request identity so bill receipt identities cannot
collide with unrelated recognition commands. Bill source uniqueness also prevents repost under
a new request UUID.

Use one outer SQL transaction with the existing tenant Accounting application lock, then PO/source
rows in stable ID order, recognition rows and finally document evidence coordination. Follow the
existing kernel's exclusive lock convention for this release. All bill writes and duplicate checks
participate. Period close, configuration changes and PO amendment keep their existing coordination;
extend supplier-identity guards to cover durable reviewed/posted bills as appropriate. Do not lock
an evidence row first and then enter accounting coordination.

Review/Post require current PO identity and expected version. An intervening PO amendment conflicts
and requires explicit review against its new revision, without changing monetary bill history.
Reject supplier reassignment while any non-abandoned bill exists; abandoning all unposted bills
can remove that draft-only restriction, while posted accounting evidence retains the permanent guard.

Post atomically commits final bill snapshot/state, invoice-side events, matches, journals, links
and receipt. Faults at any stage roll back everything. There must be no committed Posted header
without its complete recognition result. Source events from one post use a common recorded instant
consistent with BK-04; historical reporting must not expose partial results.

Use tenant-qualified uniqueness/FKs, RLS and runtime direct-DML denial on all new tables. Protect
posted snapshots and append-only revisions from modification through command paths. Generic
correction entrypoints must reject bill-owned sources without a complete authorized bill adapter.
Missing and foreign source IDs are indistinguishable. SQL administrators remain privileged; this
design does not claim protection against an operator rewriting the database.

## Migration, compatibility and recovery

Introduce one forward migration from merged BK-04. Do not alter merged migration histories or
backfill bills from estimates, files or fixture recognition events. Existing sources, receipts,
journals, configuration and PO documents retain their bytes and retry behavior.

Update schema/readiness markers, provisioning, principal checks, backup/restore compatibility and
test migration expectations together. Block destructive down-migration. Rollback uses compatible
binaries, forward repair or verified coordinated recovery, not deletion of financial history.
Consolidate development-only migrations before delivery under the current migration runbook.

## Verification and completion

Use test-first behavior changes with Gherkin comments and distinct regression claims. Pure tests
cover normalization and exact validation where sufficient; real independent SQL connections own
source authority, transaction, race, RLS and direct-DML claims. Required cases include:

1. Multiple bills per PO; draft/review/edit/abandon transitions; posted edit/delete rejection;
   pro forma rejection; explicit missing-document records; exact totals and unsupported eligibility.
2. Invoice-first and receipt-first posting, mixed classifications and tax, zero-value evidence,
   stable unit matching, source-capacity enforcement and unsupported inventory variance rejection.
3. Cross-PO duplicate references, conservative normalization, per-conflict reasons, invalidated
   resolution after edits/new conflicts, and independent-connection duplicate/post races.
4. Revoked authority before replay, foreign/missing IDs, changed request payload, same/new UUID
   post races, stale PO/bill/configuration versions, amendment/post and close/post races.
5. Injected failure after source/recognition/journal writes leaves no partial state or success
   receipt. Exact retries after later PO/configuration changes return the original authorized result.
6. Removed/foreign/unavailable evidence cannot be newly linked or posted; later removal is reported
   honestly. Generic correction cannot orphan the immutable bill from its accounting result.
7. Clean migration and upgrade from the merged base preserve existing records and exact replay;
   recovery preserves bill revisions, links, journals, receipts, permissions and missing-file status.
8. Production grants and published output contain no test adapter or public bill mutation path;
   BookkeepingAvailable remains false and ordinary PO/document workflows still work.

Run focused mutation checks against meaningful validation and transition decisions; document
equivalent mutants and tooling limits. Then run CONTRIBUTING.md's full verification and separate
container smoke gates from current source. Refresh this checkout's isolated preview and inspect
existing accounting/PO/document behavior; internal posting acceptance belongs to real-SQL tests,
not an invented browser posting workflow. Report each verification boundary separately.

Update living accounting/purchasing/architecture guidance when implementation makes this contract
current. Deliver the verified implementation as a ready-for-review PR; merging and production
operations remain separately authorized. This document alone claims no runtime verification.
