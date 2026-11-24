# Accounting setup

Accounting setup records a business's policies, general chart of accounts, mappings, and intended
transaction coverage. Internal accounting foundations supply read-only journal, trial-balance and
supplier-control APIs, but no public financial posting action is enabled. Bill and payment entry,
opening balances, report screens, exports and period closing remain unavailable. Completing setup
does not activate bookkeeping.

## Access

A tenant administrator opens **Administration**, opens a user's accounting roles, reviews the
proposed changes, and saves. Roles are not granted automatically to existing users or new invitations.
An administrator can explicitly assign accounting access to themselves.

- **Accounting administrator** can manage setup. The role also supplies permissions for reporting,
  reconciliation, and period closing as those later capabilities become available.
- **Accounting reader** can read journal APIs and has an export permission reserved for later exports.
  It cannot open or change accounting setup.

Neither accounting role grants user administration or supplier-payment authority. Existing tenant-user
administration controls assignment of these two roles. Owner and Bookkeeper are personas, not access
checks. Role changes apply on the next request; losing access clears private in-browser drafts.

## Policies

Open **Accounting** and record the country/region, functional currency, explicitly confirmed decimal
scale, fiscal start month, starting approach/date, and document retention. Incomplete policy
drafts can be saved. The current catalog offers US, Canada, Australia, UK, New Zealand, Germany,
France, Japan, Switzerland, and India, with state/province selections for US, Canada, and Australia.
Currency selection is independent of country. Catalog availability does not assert tax support.

The foundation is accrual accounting. Cash-basis views and jurisdiction-specific tax reporting are
separate designs. Fiscal years use calendar-month periods beginning on day one of the selected month.
There is no posting or closing action in this release.

Choose complete history from the business's beginning or a later cutover with reconciled opening
balances. These are plans only. An empty application does not prove that the business has no prior
cash, purchases, funding, inventory, liabilities, or other financial activity.

Retention settings govern new links to posted financial evidence. Choose 1–1000 calendar years and
record the rationale; an unset duration means indefinite protection. Existing links keep their frozen
policy even after configuration changes. Expiry permits explicit disposal; it never starts automatic
deletion or certifies compliance with a jurisdiction's rules.

## Accounts and mappings

Preview the optional starter chart before creating it, or add accounts individually. Accounts are
general assets, liabilities, equity, income, or expenses; no business-activity selection is required.
Codes use 1–32 ASCII letters, digits, dots, underscores, or hyphens and normalize to uppercase. Names
may contain Unicode. Account type and purpose are fixed at creation; edit descriptions or archive
an unused account and create a replacement when its financial meaning is wrong.

Map supplier payables, advances, credits, and refund clearing to their matching control accounts.
General inventory, expense, prepayment, and recoverable-tax mappings support the internal recognition
rules. Receipt accrual (`GoodsReceivedNotInvoiced`) requires an active general liability account.
Inventory, prepayment and recoverable-tax mappings must use distinct accounts. Naming or mapping
an account does not create a financial entry.

Archive keeps the account's code reserved and preserves its identity and revision history. Current
mapping targets and included funding accounts must be reassigned or removed from coverage before
archive. Include archived accounts in browsing to restore an account; restore retains its identity.
An account referenced by a journal cannot be archived, even when its net balance is zero. Historical
account labels remain in journal snapshots when current account descriptions change.

## Transaction coverage

For each bank, cash, or card account, record inclusion or an exclusion rationale. Included accounts
need representative statement evidence or a dated declaration of no prior activity, plus the expected
transaction classes. This screen records descriptions/references, not statement file uploads.

Inventory all activity, including receipts, fees, transfers, loans, payroll, taxes, and supplier
payments where applicable. Each financial class remains unsupported until its typed accounting source
ships. Attesting that the inventory is complete cannot override missing capabilities. Later cutover
and reconciliation must verify actual balances, evidence, and transaction completeness.

## Saving and concurrent changes

Save explicitly. A successful reload reads persisted state. On an uncertain outcome, retry the same
request; on a conflict, review the current saved values before reapplying the preserved draft. A
successful old retry returns its receipt and never rolls back newer settings or role membership.
Unsaved drafts are private browser memory and are lost on reload or loss of access.

The first journal freezes currency, scale, fiscal calendar and planned starting approach/date.
An internally closed month also freezes these fields, even when no journal has been posted; it does
not invent a first-journal reference. These freezes are not acceptance of starting balances or
permission to use the application for real bookkeeping; those release gates remain separate.

## Journal read APIs

Accounting readers and administrators can use these authenticated beta GET routes:

- `/api/beta/accounting/journals` lists recorded entries; append `/{id}` for source evidence, lines,
  and current correction relationships with their immutable reason and evidence digest.
- `/api/beta/accounting/accounts/{id}/journal` lists an account's journal lines.
- `/api/beta/accounting/trial-balance` reports exact debit/credit activity and debit-minus-credit balances.
- `/api/beta/accounting/periods?from=YYYY-MM-01&through=YYYY-MM-01` reads up to 120 inclusive
  calendar months, including eligible months that are open but not materialized in storage.

Amounts are decimal strings. Lists default to 50 rows and allow up to 100 per page. Responses include
posting-date and UTC recorded-time cutoffs; pagination preserves those cutoffs. Both filters must
match for an event to appear. Trial-balance totals cover the entire filtered ledger, not just the page.
An empty ledger has zero activity; it does not prove that a business has no opening balances.

Each response is internally consistent. Recorded timestamps are not commit timestamps, so entries
that were in flight can become visible between pages. These APIs are not frozen exports or reconciled
financial statements. No report screen or financial write route is delivered with this foundation.
An internal correction appends an exact reversal and optional replacement with one database-owned
recorded time for all new components. A recorded-time cutoff therefore includes either the whole
committed correction or none of it. Original entries and their timestamps remain unchanged; posting
date and recorded-time filters still apply independently. Current journal detail can show later
correction relationships even when an earlier report cutoff excludes the correction.

Period closure and correction writes are internal primitives exercised only with synthetic sources
in disposable test databases. The closure evidence v1 envelope requires schema version 1, a bounded
nonblank typed kind, and the exact canonical period start; the synthetic kind is
`SyntheticReconciliation`. A production reconciliation schema and close checklist remain future work.

An exact reversal retains historical account snapshots and swaps the original debit/credit lines;
an optional replacement uses currently eligible mappings. Closed-period corrections use an explicitly
selected open posting date. For example, a September expense/payable of 300 remains 300 in September
after an October reversal of 300 and replacement of 280; through October the balance is 280. A
recorded-time cutoff before that correction still sees only the original 300. Correction does not
mean that money was refunded. [Architecture](ARCHITECTURE.md#atomic-journal-foundation) owns the
SQL transaction, replay and close-adapter constraints.

## Internal purchase recognition

[Recognition policy and posting examples](accounting-purchase-recognition.md#internal-purchase-recognition).

## Internal supplier bills

BK-05 stores immutable bill revisions and reviews for an ordered PO. Explicit classified components
must equal the declared total exactly. Review checks currency precision, required component reasons,
assigned cost bounds, tax policy evidence and the single-source rounding limit. Invoice posting derives every recognition claim from the
reviewed revision; it cannot accept caller-supplied accounting overrides. Pro forma requests may be
reviewed but cannot post. Posted bills cannot be edited, abandoned or corrected through generic
recognition/journal commands; a complete bill correction workflow remains future work.

Normalized supplier references span all POs and currencies for that supplier. Review records reasons
for the exact current set of conflicts, and posting rechecks that set under the shared financial lock.
Editing creates another immutable revision and requires another review. Exact retries recheck current
permissions before returning the original receipt. Nonabandoned bills also prevent PO supplier changes.

Duplicate normalization trims and collapses only ASCII spaces, tabs, CR and LF, then uppercases ASCII
a–z. Punctuation, leading zeros, accents and other non-ASCII characters remain significant; keys
compare ordinally. This conservative rule avoids merging distinct references and does not claim
Unicode-equivalence detection. Resolution records a reason and both exact conflicting revisions;
a new conflict or changed revision requires re-review. Drafts may contain duplicates, but an actual
duplicate must be abandoned rather than force-posted. A later invoice is a new identity optionally
linked to its pro forma predecessor; it never converts that predecessor's history. Reusing the
reference still requires duplicate resolution.

Bounded internal reads expose source history, posting links and honest current evidence availability.
History retains each review's PO version, supplier snapshot and captured evidence after revision or
abandonment clears the current review. Document metadata/digests survive in reviewed snapshots;
posting atomically acquires BK-07 retention links. Recovery loss can still make bytes unavailable
without erasing that evidence. No paid/outstanding amount
is inferred from these records. Runtime mutation grants and production role assignments for
`SupplierBillsManage`/`SupplierBillsPost` remain absent. Review alone acquires no retention link;
posted evidence follows [durable financial evidence](#durable-financial-evidence). Source read access
does not grant private-document download authority.

## Supplier open items and read APIs

BK-06 derives immutable payable items from posted structured bills and supported invoice-side
recognition. Recognition payables without a bill retain that distinction and cannot be allocated
as bills. A zero-value bill retains evidence without creating a monetary item. PO estimates, invoice
files, receipt accrual (GRNI) and invoice-first Prepayment are not allocatable supplier advances.

Accounting readers and administrators can use these authenticated beta GET routes with
`AccountingReportsRead`:

- `/api/beta/accounting/supplier-open-items` lists items; append `/{id}` for an item or
  `/{id}/history` for its immutable movements, application and correction links.
- `/api/beta/accounting/supplier-reconciliation` compares independently aggregated journal and
  subledger amounts by historical control account and currency.

Reports separate Payable, Advance, CreditReceivable and RefundClearing. Net supplier position is
Payable + RefundClearing - Advance - CreditReceivable. They retain fully settled items, historical
account identities and unsupported evidence diagnostics. Missing, duplicate or invalid attribution
prevents complete reconciliation even when differences net to zero. Supplier/PO/bill filters cannot
hide tenant control activity whose owner is unknown. Zero AP does not prove a purchase is complete.

Use `supplierId`, `purchaseOrderId` and `billId` filters, inclusive `postingThrough` and UTC
`recordedThrough` cutoffs, and `pageSize` (default 50, maximum 200). Amounts are decimal strings at
the configured scale. Pages distinguish whole-filter and page totals. Protected continuation cursors
bind the tenant, route, filters, page size, resolved cutoffs and captured financial-group/journal
ceilings, so newly committed backdated activity cannot change a continued report. Responses are
private and non-cacheable; report permission does not grant private-file download access.

Internal SQL commands record an actual Bank/Cash payment, apply advances to bills, reverse an
application once, and preview/execute payment corrections with explicit replacement allocations.
They do not initiate transfers. Same-PO/supplier/currency rules, exact scale, current authority,
replay identity and both sides' capacity at every affected later posting date are enforced atomically.
Fully allocated payments retain gross capacity evidence even when their journal has no advance line.
Payment correction reverses recorded cash evidence; it is distinct from an actual supplier refund.

These commands have no runtime mutation grants, assigned production write permissions or public
write UI/routes. `BookkeepingAvailable` remains false. Posted evidence is protected by the retention
boundary below. See the
[BK-06 specification](specs/2026-09-27-bk-06-supplier-open-items-and-allocations.md) for implementation
evidence and remaining release gates, and [Architecture](ARCHITECTURE.md#supplier-open-items-and-allocations)
for the unmeasured reporting-cost limit.

The [BK-01 specification](specs/2026-09-21-bk-01-accounting-foundation.md) owns the accepted boundaries;
the [architecture](ARCHITECTURE.md#atomic-journal-foundation) owns the journal, period and correction
boundaries;
the [PO-07 prerequisites](specs/2026-09-20-po-07-deposits-and-payments.md) describe later bookkeeping gates.

## Internal supplier credits, refunds and corrections

BK-08 adds internal SQL commands for credit notes, incoming refunds, outgoing refund repayments,
and complete corrections of posted bills, credits and cash sources. Credit notes reference exact
posted bill revisions and component claims. They preserve the original account and currency scale.
A credit creates a supplier receivable and may be explicitly applied to open debt. An actual refund
uses Bank or Cash and consumes a receivable or unused advance. An actual repayment uses Bank or
Cash to settle refund clearing. These commands record money that moved outside Workbench.

Source corrections preserve originals and append exact inverses and any approved successors.
A bill or credit correction preserves the original cash. For example, correcting a credit of 18
already refunded in full to 10 leaves the bank receipt at 18 and an outstanding clearing liability
of 8 after explicit reapplication. Paying back the 8 is a separate outgoing repayment.
Correcting a mistaken cash record instead reverses that record and can record an explicit replacement.

Journal and supplier-item detail expose typed source IDs and revisions, original amounts, claims,
settlements and correction links. History uses the same posting date, recorded time, and frozen
group and journal ceilings as balances. Older pages exclude relationships committed after capture.
Report access does not grant private-document downloads. Exact historical control ownership is
required even when journal and subledger net amounts agree.

Supported claims include original expense and eligible unrecognized prepayments. Inventory credits
require a trusted held-inventory owner. Unsupported tax or inventory evidence and card-funded
refunds are outside this release. Production permissions, public writes and bookkeeping activation
remain gated. See the [approved BK-08 contract](specs/2026-10-19-bk-08-supplier-credits-and-refunds.md).

## Durable financial evidence

Financial evidence links authentic posted bill, payment, recognition, credit, refund and repayment sources to exact private document
revisions, digests and lengths in the source transaction, including zero-value sources. Review alone
does not acquire a hold. Original source snapshots, labels, links and receipts remain immutable;
corrections, settlement and permission loss never release them. Internal source-owned supplements
append reasoned history and require the original source's current mutation authority.

Each new link freezes the policy version, years, rationale, and deadline. Its anchor is the later of
the posting date at midnight UTC and SQL's recorded instant; calendar-year addition clamps leap days
and rejects overflow. Legacy posted evidence receives indefinite protection. Descriptive recognition
strings cannot establish authenticated document ownership or supplement authority.

Ordinary file removal rejects linked evidence even after expiry. Explicit disposal requires document
management plus `AccountingConfigurationManage`, a nonblank reason, current versions, finite expired
deadlines for every link, no independent hold and completed recovery verification. One durable receipt
removes live visibility and schedules cleanup after at least seven days. Physical cleanup rechecks
holds, deadlines and receipt membership; the receipt alone does not prove bytes were deleted.

Retention grants no access. Source readback also requires document-read authority, every request and
retry reauthorizes, and access loss clears private client state. Availability distinguishes declared
absence, available content, missing/corrupt recovery content and disposed bytes. Recovery preserves
links and policy while recording unavailable bytes explicitly. Backup-set expiry follows its separate
recovery window; old backups may retain disposed bytes until expiry.

See [purchasing disposal](purchasing.md#keep-invoice-files-with-an-ordered-purchase),
[recovery operations](operations/blob-and-service-providers.md#financial-evidence-retention), and
the [BK-07 specification](specs/2026-09-28-bk-07-durable-financial-evidence.md).
