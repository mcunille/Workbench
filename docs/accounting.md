# Accounting setup

Accounting setup records a business's policies, general chart of accounts, mappings, and intended
transaction coverage. An internal journal foundation supplies read-only journal and trial-balance
APIs, but no financial posting action is enabled. Bills, payments, opening balances, report screens,
exports and period closing remain unavailable. Completing setup does not activate bookkeeping.

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
scale, fiscal start month, starting approach/date, and proposed document retention. Incomplete policy
drafts can be saved. The current catalog offers US, Canada, Australia, UK, New Zealand, Germany,
France, Japan, Switzerland, and India, with state/province selections for US, Canada, and Australia.
Currency selection is independent of country. Catalog availability does not assert tax support.

The foundation is accrual accounting. Cash-basis views and jurisdiction-specific tax reporting are
separate designs. Fiscal years use calendar-month periods beginning on day one of the selected month.
There is no posting or closing action in this release.

Choose complete history from the business's beginning or a later cutover with reconciled opening
balances. These are plans only. An empty application does not prove that the business has no prior
cash, purchases, funding, inventory, liabilities, or other financial activity.

Retention settings are proposals pending the later evidence-retention capability. Unset retention
stays unresolved; entering a duration does not start deleting documents or enforce a legal policy.

## Accounts and mappings

Preview the optional starter chart before creating it, or add accounts individually. Accounts are
general assets, liabilities, equity, income, or expenses; no business-activity selection is required.
Codes use 1–32 ASCII letters, digits, dots, underscores, or hyphens and normalize to uppercase. Names
may contain Unicode. Account type and purpose are fixed at creation; edit descriptions or archive
an unused account and create a replacement when its financial meaning is wrong.

Map supplier payables, advances, credits, and refund clearing to their matching control accounts.
General inventory, expense, prepayment, and recoverable-tax mappings are classification candidates
for later recognition rules. Naming or mapping an account does not create a financial entry.

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

The [BK-01 specification](specs/2026-09-21-bk-01-accounting-foundation.md) owns the accepted boundaries;
the [BK-02 specification](specs/2026-09-23-bk-02-atomic-journal.md) defines the journal boundary;
the [BK-03 specification](specs/2026-09-24-bk-03-corrections-and-period-controls.md) defines internal
period and correction controls;
the [PO-07 prerequisites](specs/2026-09-20-po-07-deposits-and-payments.md) describe later bookkeeping gates.
