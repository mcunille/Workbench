# BK-01: accounting configuration, accounts, and authorization

**Status:** Proposed — the product policies below were agreed during the BK-01 discussion.
The bounded implementation design, including the explicitly listed choices for review, awaits
approval. This document delivers no runtime, schema, reporting, or bookkeeping activation.

Parent: [PO-07 bookkeeping prerequisites](2026-09-20-po-07-deposits-and-payments.md).
This specification supersedes the parent's persona-based authorization proposal for BK-01.

## Problem and outcome

Before financial events can create dependable ledger entries, a business needs explicit policies,
typed accounts, controlled mappings, and authorization. Configuration must be general enough for
businesses that buy tools, materials, consumables, and inventory in different combinations. An
activity category must not silently determine an accounting policy or restrict available accounts.

BK-01 delivers a tenant-scoped setup workflow that can be saved and resumed, a general chart of
accounts, mapping validation, role assignment, and an inventory of transaction coverage. A saved
setup is not permission to keep real books: the journal, recognition, retained evidence, cutover,
and reconciliation prerequisites remain separate deliveries.

The implementation uses the existing modular monolith, tenant boundary, SQL Server RLS, session
resolution, and role/permission model. Current evidence:

- [WorkbenchPermissions](../../src/Workbench.Server/Authorization/WorkbenchPermissions.cs)
  defines `TenantAccess` and `TenantUsersManage`; it has no accounting permissions.
- [RequestActor](../../src/Workbench.Server/Authorization/IRequestActor.cs) contains the tenant,
  user, session, and resolved permissions. [SessionService](../../src/Workbench.Server/Identity/SessionService.cs)
  resolves authoritative permissions through the database rather than trusting client role claims.
- [Tenant user administration](../../src/Workbench.Server/Administration/TenantUserEndpoints.cs)
  manages users, invitations, and sessions, but does not supply an accounting-role assignment workflow.
- The parent spec's purchase commitment and file-attachment boundaries remain operational; neither
  produces a journal. There is no existing accounting balance to backfill from purchase estimates.

## Agreed product policies

| Area | Decision |
| --- | --- |
| Business configuration | Country and state/region belong to the business, shared by its users. They are contextual configuration, not automatic tax-rule selection or a compliance claim. |
| Accounting foundation | One accrual ledger per business; eventual accrual and cash-basis reporting views must not rewrite transactions. |
| Currency | Explicit functional currency and posting scale, with scales 0–4 supported initially. No currency or scale changes after the first posting in this release. Purchase calculations retain their existing precision. |
| Calendar | Configure the business fiscal year. Monthly accounting periods are the proposed initial calendar implementation; closing behavior is a later story. |
| Accounts | General asset, liability, equity, income, and expense accounts. Accounts describe financial purpose, not a business-activity category. Archive instead of deleting history. |
| Financial actions | Authorized business actions automatically create their required accounting effects. No separate blanket Bookkeeper or manual-posting permission is required. An order commitment alone still creates no journal. |
| Authorization | Owner and Bookkeeper describe personas. Assignable roles contain explicit permissions; code checks permissions, never persona names. Configuration, operational actions, closing, financial reporting, and role administration are separate authorities. |
| Starting books | Support complete history entered from a genuine zero-balance beginning, or a later cutover with reconciled opening balances. Starting with an empty database does not erase earlier financial activity. |
| History and retention | Preserve posted accounting history. Configure supporting-document retention separately; retention and holds must be enforced under BK-07 before real bookkeeping use. |
| Portability | Support primary bookkeeping without trapping data. Source-neutral import and export designs are separate; do not add a primary/secondary-system setting without defined behavior. |

No personal business settings, private source-system names, local paths, actual statements, or
filing deadlines belong in public specifications or fixtures. Each business supplies its own values.

## Scope and exclusions

BK-01 includes configuration, account creation/edit/archive/restore, controlled mapping assignment,
accounting-role assignment, coverage planning, and an authenticated setup UI. It establishes stable
IDs and revision evidence for later financial use. It does not implement journals, monetary opening
balances, supplier bills/payments/allocations, recognition, tax calculations, financial reports,
period close, fiscal-year earnings roll-forward, document holds, or imports/exports.

Do not introduce arbitrary manual journals, writable balance columns, a configurable posting-rule
language, activity-specific charts, or an automatic compliance engine. Preserve existing collection,
supplier, and purchasing behavior for businesses that have not configured accounting.

## Configuration and lifecycle

One accounting configuration belongs to the current tenant; tenant identity is never supplied by
the browser. Proposed fields and constraints:

| Field | Contract |
| --- | --- |
| Jurisdiction | Explicit country code and optional state/region; require a region where the selected country supports it. Store stable codes from a versioned application catalog; changing jurisdiction does not apply tax rules retroactively. |
| Reporting policy | Accrual foundation is fixed. Optional framework/tax-policy notes record a business decision or an unresolved question; they do not assert compliance or select a cash-basis implementation. |
| Functional currency | Explicit selection from the supported currency catalog; no inference from locale, a bank account, or a PO. |
| Posting scale | Explicit integer 0–4. The UI may suggest a catalog scale but requires confirmation; it cannot silently round source money. |
| Fiscal calendar | Proposed initial scope: a start month, day one, and twelve calendar-month periods per fiscal year. Fiscal years are identified by their start date; 4-4-5 calendars and short transitional years require later design. |
| Start approach | `FromBeginning` or `OpeningBalances`, plus an optional planned date while setup is incomplete. The date is required for setup readiness and remains a plan until BK-10 verifies it. |
| Supporting-document retention | Optional proposed minimum number of years (positive whole years) and policy rationale/reference. Unset is explicitly unresolved, never zero-day retention. BK-07 must define the retention clock, exceptions, holds, and enforcement before this can become an effective policy. |

Incomplete drafts can be saved with omitted required values; malformed supplied values are rejected.
Readback distinguishes missing configuration, validation failures, and future capability blockers.
Do not store an operator-editable `ReadyForProduction` boolean. The server computes setup completeness
and returns `BookkeepingAvailable = false` throughout BK-01, even when every setup field is filled.
No activation endpoint or journal-writing path is delivered.

`SetupComplete` means country/required region, currency, scale, fiscal start month, start approach/date,
and a proposed retention duration/rationale are supplied; the four supplier controls are assigned;
and at least one active funding account has a coverage inventory, with every included account's
inventory attested complete. Framework notes and general classification mappings are optional at
this stage and report their unresolved status separately. A complete inventory can contain unsupported
classes: completeness describes the recorded plan, not implementation support or accounting approval.
Return per-section missing items plus separate capability/policy blockers; never collapse those into
a green activation indicator. A business with no intended funding account can save setup but remains
incomplete pending an explicit accounting perimeter design.

Use required opaque version tokens for updates and append configuration revisions with actor and
UTC recording time. Saving a new version never overwrites revision evidence. Plan dates are date-only;
audit times are UTC. Do not use browser time zones to change fiscal boundaries.

BK-02 must atomically freeze the currency, scale, calendar, and accepted start boundary with the first
posting and serialize competing configuration changes. BK-01 can verify revision concurrency, but
cannot claim to have tested a race with a journal that does not exist. BK-02 cannot enable posting
until those race tests pass. Later jurisdiction or retention changes must retain prior revisions and
must not shorten an existing evidence obligation; BK-07 owns their effective-policy transition.

## General chart of accounts

Each account has a tenant-qualified stable UUID, unique normalized code, display name, one of the
five account types, a purpose, optional description, archive timestamp, and version token. Proposed
limits are 32 characters for the code, 160 for the name, and 2,000 for the description. Trim text;
normalize codes consistently in API and SQL using ordinal case-insensitive uniqueness. Names need
not be unique; pickers show code and name. Account codes remain reserved when archived.

The proposed first release fixes account type and purpose at creation. Correct a mistaken type by
archiving an unused account and creating a replacement; allow descriptive edits with revision history.
This avoids a type-change contract that becomes unsafe when later stories reference the account.

Purpose distinguishes `General`, `Bank`, `Cash`, `CardLiability`, and the four supplier controls below.
It does not encode gemstone resale, manufacturing, retail, or another business activity. Bank/cash
purposes require Asset; card purpose requires Liability. BK-01 funding accounts use the business
functional currency and contain no bank credentials or editable balance. A general account may be
used for tools, materials, consumables, inventory, revenue, or expenses under an approved later rule.

Propose an optional, explicitly confirmed starter chart with general accounts for bank, cash,
inventory, prepayments, equipment, receivables, payables, tax assets/liabilities, equity, income,
cost of sales, and expenses. Users can instead create their own chart. No chart is silently seeded;
names/codes are editable and no suggested account establishes recognition or tax policy. Preview
every proposed row before atomic creation; retries must not create duplicate starter accounts.

Archive prevents new selection/use without deleting identity or revisions. Reject archive while
an account is a current mapping target or an included funding account in a coverage plan; require
explicit reassignment/removal from the plan first. Restore preserves its identity and code.
Historical references remain readable. BK-02 and later must additionally check unresolved balances,
open items, and concurrent posting before allowing archive. There is no account DELETE API.

## Controlled mappings

Mappings connect a fixed accounting purpose to an eligible account, rather than interpreting an
account name. Proposed initial control slots are:

| Slot | Required account type and purpose |
| --- | --- |
| Supplier payable | Liability / SupplierPayable |
| Supplier advance | Asset / SupplierAdvance |
| Supplier credit receivable | Asset / SupplierCreditReceivable |
| Supplier refund clearing | Liability / SupplierRefundClearing |

Control accounts are distinct from one another and from bank/cash/card/general accounts. SQL rejects
cross-tenant, archived, missing, wrong-type, and wrong-purpose targets. Four assigned control slots
are required for supplier setup completeness; partial mapping drafts can still be saved.
Bank/cash/card accounts are selected explicitly by the eventual money-movement source, not by one
global default bank mapping.

BK-01 also records proposed general classification mappings for inventory, expense, prepayment,
and recoverable tax (Asset for inventory/prepayment/recoverable tax; Expense for expense). They are
configuration candidates, not implemented posting rules. Nonrecoverable tax follows the approved
cost/expense classification and remains unresolved until BK-04; never silently map all tax to expense.
BK-04 owns purchase-specific selection, rounding, uninvoiced receipts, and recognition requirements.

Mapping changes append a version and require the prior version token. Future postings capture the
mapping/account revision they used; changing a mapping never moves an existing balance. Once control
balances/open items exist, control reassignment requires a separately designed reconciled transition;
it cannot be enabled as an ordinary settings edit by BK-02. Ordinary typed financial sources cannot
target supplier controls except through their designated supplier accounting adapters.

## Roles and permissions

Use the current Identity roles and resolved permission claims. A person can have multiple roles;
effective permission is their union. No controller or SQL command checks whether a user is an Owner
or Bookkeeper. Do not add a generic `Accounting.Post` permission.

Use only two fixed accounting roles initially; a custom role editor and finer role splits are deferred:

| Role | Permissions and boundary |
| --- | --- |
| Accounting administrator | Configuration, accounts, mappings, and coverage plans; financial report read/export, reconciliation, and monthly/fiscal-year closing as those capabilities ship. No authority to assign roles or administer users merely from this role. |
| Accounting reader | Financial report read/export as those capabilities ship. No configuration changes, reconciliation, closing, role assignment, or payment authority. |

Accounting administrator includes the reader's permissions, so assigning both is unnecessary.
Keep explicit permissions internally: `AccountingConfigurationRead`, `AccountingConfigurationManage`,
`AccountingReportsRead`, `AccountingReportsExport`, `AccountingReconcile`, `AccountingPeriodsClose`,
and `AccountingFiscalYearsClose`. The administrator role contains all seven; the reader contains only
the two report permissions. This keeps endpoint checks specific without asking users to manage five
roles. Register only shipped endpoint policies; future actions remain unavailable. The reader role
can be assigned in BK-01, but financial reports themselves remain a later delivery.

Later source stories introduce action permissions for recording a payment, recognizing a purchase,
or correcting a financial source. The business action and its required journal commit atomically;
failure to create the journal rolls back the source action. A user authorized for that action needs
no additional persona or posting role. They see the operational result without automatically gaining
access to company-wide financial reports. Neither accounting role grants those future operational
actions implicitly. BK-01 does not retrofit new restrictions onto existing nonfinancial PO commitment
or ordinary purchase browsing.

Use existing `TenantUsersManage` authority to assign/revoke the two accounting roles for enabled users
in the current tenant, including explicit self-assignment. No new access-administration role or
`AccountingRolesManage` permission is needed. Create the two role definitions for existing and new
tenants without automatically assigning accounting access. Existing tenant administrators can make
the initial assignment through user administration. New invitations receive no accounting role.
There is no requirement to retain an accounting administrator: existing tenant-user administrators
can restore accounting access when needed. Preserve existing last-tenant-administrator protections.

Role assignment is an audited, versioned, restricted command with a fixed allowlist containing only
the two accounting role IDs. It cannot change arbitrary roles/claims or grant `TenantUsersManage`.
Require the actor's current session and permission at command execution, not a stale UI/request claim.
Coordinate assignment with user disable and competing membership changes through transactional locks;
reject assignment to a user already disabled at the command's serialization point. A subsequent
disable still revokes effective access. Authoritative role changes take effect on the next request;
command execution must also recheck access if a request was admitted before revocation.

The proposed Identity security scope is explicit: deny direct web-principal writes to `Identity.Roles`,
`RoleClaims`, `UserRoles`, and `UserClaims`; preserve scoped reads and fixed-role creation through the
existing restricted provisioning/invitation commands and the new accounting-role assignment command.
`ResolveSession` currently also reads user permission claims, so leaving direct `UserClaims` writes
available would bypass role assignment. Preserve existing non-accounting claims, but do not accept
new accounting permission grants through user claims. Accounting checks use the role-derived set.

Update principal provisioning/permission expectations as well as migrations so re-provisioning cannot
regrant a bypass. Test invitation acceptance, recovery, restore sanitation, fresh tenant provisioning,
and existing user-disable/last-tenant-administrator behavior. This increment does not introduce a
second last-administrator invariant or replace user-state management with a new command. Its Identity
scope protects role/claim writes; it is not a redesign of all identity storage or a claim of protection
against a fully compromised web process. Resolve any additional mutation path before broadening scope.

## Complete-statement coverage planning

For every intended bank, cash, or card account, retain a versioned coverage plan with representative
statement date range, a bounded evidence reference/description, a completeness attestation, transaction
classes, and expected recurring activity. This is a manual inventory, not statement import or retained
document storage. Do not upload actual statements through an unprotected new file path.

For a genuinely new account/business with no prior statement activity, allow an explicit
`NoPriorActivity` declaration with an as-of date and rationale instead of fabricated statement dates
or evidence. Expected future transaction classes must still be inventoried. This declaration neither
proves zero opening balances nor bypasses BK-10 verification.

Each class has a label, proposed typed-source/policy/reconciliation reference, and an unresolved
prerequisite reference where support is missing. A class can be a supplier payment, fee, transfer,
receipt, interest, loan movement, payroll, tax payment, or another explicitly described class; the
list is not restricted to currently implemented sources. Unsupported entries remain visible.
Include an explicit account-exclusion rationale so an excluded account cannot silently disappear
from the proposed accounting perimeter.

Implemented support is resolved from server-owned capabilities, never a user-editable Supported
checkbox. BK-01 has no implemented financial adapters, so every financial class is unsupported.
Manual attestation and complete configuration cannot override this. Unresolved classes identify
prerequisite work, but saving setup does not automatically publish GitHub issues or private evidence.
BK-10 repeats this assessment using actual opening schedules/statements and reviews the completeness
of any FromBeginning history before activation; BK-11 verifies ongoing reconciliation.

## Setup experience

Extend the existing Tanzanite app in Operate mode, following [DESIGN.md](../../DESIGN.md). Use the
existing navigation/settings pattern, form controls, feedback, and responsive layouts. No dashboard
of invented balances, activity selector, or replacement visual design is needed.

- Accounting setup presents Policies, Accounts and mappings, and Transaction coverage as resumable
  sections. Each distinguishes saved configuration from unanswered items and unavailable capabilities.
- Start with explicit policy choices and an optional chart preview. Show account code, name, type,
  purpose, and archive state in a searchable, paginated list, ordered by normalized code then ID.
  Include-archived is explicit and does not make archived accounts selectable as current mappings.
- Place accounting role assignment with user administration, visible only with the existing `TenantUsersManage`
  permission. Summarize effective capabilities and the proposed grants/revocations before saving.
- Keep setup and role-management screens independently reachable for their authorized users.
  Neither screen grants report access. Hide unauthorized navigation and enforce the same checks
  server-side; an authenticated unauthorized deep link returns access denied.
- On conflicts retain the unsaved draft in private memory and require reload/reconciliation; never
  silently overwrite another administrator's changes. Clear private drafts on sign-out or permission
  loss. Provide field errors, loading, empty, saved, conflict, and permission-revoked states.
- Keep keyboard navigation, visible focus, narrow layouts, long names, and light/dark appearances
  usable. State clearly: setup can be saved, but bookkeeping is not yet available.

## API, persistence, and security contract

Add an Accounting module under the beta API with configuration, accounts, mappings, and coverage
resources; add accounting-role catalog and per-user assignment resources under tenant administration.
No caller-supplied arbitrary SQL, journal lines, tenant ID, or permission claims are accepted. Reads
are private/no-store; writes require the existing authentication and antiforgery protections.

Create operations use request UUIDs and durable canonical receipts; matching retries return the
original result, while changed payload reuse conflicts. Updates/archive/restore/role assignment use
request UUIDs plus expected versions. Replay never rolls back a later change or restores revoked
authority. Check current authorization before returning any receipt. Missing/stale versions, invalid
mapping targets, and occupied codes have deterministic actionable responses: 400 for invalid input,
403 for missing permission, 404 for unavailable tenant-scoped identities, and 409 for version or
replay conflict. Paginated reads use bounded cursors and deterministic ordering.

Use tenant-qualified keys/foreign keys and RLS for every new tenant table. The web principal reads
permitted rows and executes narrow write commands; direct INSERT/UPDATE/DELETE of accounting tables,
revisions, and receipts is denied. Restricted commands revalidate actor authority, account eligibility,
versions, and uniqueness atomically. Configuration/mapping/archive races serialize on a consistent
tenant/account locking order. Accounts, revisions, receipts, role changes, and audit evidence must
commit together as appropriate; never report success with missing audit/replay evidence.

Use one additive migration for this coherent release, including required Identity command changes,
role-definition provisioning/backfill, SQL grants/denials, schema readiness, and the EF snapshot. Preserve all
base migrations. Verify fresh creation and upgrade with existing users, sessions, and POs; existing
tenants start with unconfigured books. No production migration is authorized by this design.
Block destructive down-migration once accounting configuration/history exists; document a reviewed
forward correction or protected restore and the actual application/schema compatibility window.
Never claim an older application can safely write the new schema without a verified rollback drill.

## Observable acceptance and verification

Use TDD with GIVEN/WHEN/THEN comments for implemented behavior, unit tests for validation, real SQL
for integrity/concurrency, authenticated HTTP tests for authorization, and focused component/browser
tests for the setup workflow. Required acceptance includes:

1. Two tenants configure different countries, currencies, scales, calendars, and start approaches
   independently. Missing fields remain explicit; unsupported scales and malformed dates are rejected.
2. An ordinary member and an Accounting reader cannot read or change accounting setup or assign
   roles. An Accounting administrator can configure accounts but cannot assign roles without
   `TenantUsersManage`. That permission alone permits assignment, not reading accounting data.
3. Roles compose; persona/display-name changes grant no permission. Grants/revocations work through
   fresh and already-admitted requests; self-escalation without `TenantUsersManage` fails. Concurrent
   disable/assignment preserves authorization, and existing last-tenant-administrator protection holds.
4. All five account types are usable without an activity category. Duplicate normalized codes and
   changed-payload retries fail. Starter-chart retries create exactly one set, atomically.
5. Wrong-type, archived, cross-tenant, and nonexistent mapping targets fail through both HTTP and SQL.
   Concurrent mapping/archive operations cannot leave an active mapping pointing to an archived account.
6. Archive preserves account identity, code, and revision evidence; restore preserves history. Direct
   runtime writes/deletion of protected data fail under the actual restricted principal.
7. Simultaneous configuration/role changes cannot lose updates; stale commands preserve the winner.
   Failed commands leave no partial chart, mapping, role membership, or audit/receipt state.
8. A complete statement inventory containing a supplier payment and an unsupported receipt remains
   blocked even when the supplier-payment subset is described. Empty coverage and excluded accounts
   cannot produce a false bookkeeping-ready result. Client-supplied supported/activation flags fail.
9. FromBeginning and OpeningBalances remain plans, not invented financial entries. Unset retention
   stays unresolved. Configuration completeness never enables posting, reports, or period closing.
10. Existing collection, purchasing, and account-recovery workflows remain usable before/after upgrade;
    changing a PO from draft to ordered or attaching an invoice file still creates no accounting entry.
11. Browser verification exercises save/reload, role separation, archive/mapping errors, stale edits,
    keyboard use, narrow layouts, and both appearances with synthetic data.

Mutation testing targets permission checks, eligibility/type rules, stale-version rejection, and
coverage readiness logic. Investigate surviving meaningful mutants and report unavailable tooling
honestly. Run current-source `./scripts/verify.ps1` and `./scripts/smoke-container.ps1`, migration and
restricted-principal checks, regenerate the API declarations, and inspect the isolated preview.
The delivery PR reports actual results and remaining limits. BK-02 owns first-posting freeze and
post-versus-edit/archive tests; BK-07 owns retention enforcement; no BK-01 test substitutes for them.

For this design-only PR, check local links, parent/child consistency, privacy, and repository
documentation assertions. No runtime, migration, browser, or mutation evidence is claimed.

## Delivery dependencies and later designs

Implementation proceeds inline through schema/domain commands, authenticated APIs and role assignment,
then setup UI and integrated verification. Keep the execution plan untracked. An independent review
checks the accepted specification and immutable delivery range before opening the implementation PR.

| Follow-up | Boundary |
| --- | --- |
| [#143: reporting views](https://github.com/mcunille/Workbench/issues/143) | Accrual/cash-basis views and reconciled adjustments; no rewriting posted transactions. |
| [#144: accounting exports](https://github.com/mcunille/Workbench/issues/144) | Versioned portable records, exact amounts, stable IDs, source/evidence relationships. |
| [#145: import and cutover](https://github.com/mcunille/Workbench/issues/145) | Source-neutral staged import, complete history or opening balances, duplicate prevention, BK-10 reconciliation. |
| [#146: optional jurisdiction reporting](https://github.com/mcunille/Workbench/issues/146) | Washington excise/B&O/sales/use-tax design; country/region selection alone enables no tax logic. |
| Parent BK-02–12 and PO-15 | Journal, periods, recognition, bills, allocations, retention/holds, corrections, reconciliation, non-supplier sources, and FX remain separately scoped. |

The agreed retention direction does not set a jurisdiction-specific legal duration. A business must
approve its effective policy before BK-07/activation. Complete-statement coverage may reveal additional
required source stories; they must be scoped explicitly rather than hidden behind supplier payments.

## Material implementation choices for review

The agreed policies above do not automatically approve every mechanism. Proposed choices are:

1. Two accounting roles only: Accounting administrator and Accounting reader, assigned through existing
   tenant-user administration with no automatic membership grants. Retain explicit internal permissions
   and the specified protection of Identity role/claim writes; defer custom roles and finer role splits.
2. Calendar-month periods with a configurable fiscal start month; other fiscal calendars are deferred.
3. Account type/purpose immutable from creation, with an optional general starter chart and explicit
   mapping assignments. This favors stable meaning over editing an unused account's type in place.
4. Manual coverage inventory and proposed retention settings in BK-01, with server-owned capability
   blockers and no activation override; actual evidence retention and cutover enforcement remain later.

Alternatives considered: persona checks would conflate users with authority; activity-based charts
would restrict general purchasing; immediate custom-role/calendar engines would broaden this first
delivery; accounting inferred from POs would fabricate history; and a settings checkbox declaring
readiness would bypass the parent spec's evidence and reconciliation gates.

Approval of this bounded design authorizes its implementation and verification through a reviewable
PR. It does not authorize merging, production activation, importing private data, or tax submission.
