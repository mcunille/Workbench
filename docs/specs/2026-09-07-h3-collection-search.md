# H3: find an item when I need it

**Status: Implemented** — the owner approved this design for implementation on 2026-09-07.

## Scope and current behavior

Deliver [H3](https://github.com/mcunille/Workbench/issues/41) using the existing
authenticated collection, Grid/List views, and H2 thumbnails. A hobbyist searches
remembered words in names, notes, or locations, opens details, and returns to the
same collection position. Advanced syntax, saved searches, configurable columns,
editing, and production deployment are outside this increment.

`InventoryEndpoints.ListAsync` currently pages 50 items by ascending creation time
and SQL Server UUID order. `Collection` loads pages locally but loses its state when
unmounted. The database already has an index on `(TenantId, CreatedAtUtc, Id)`.
Extend these mechanisms and retain existing session, tenant filtering, SQL RLS,
private response headers, and photo authorization boundaries.

## Search contract

Add optional `q` to `GET /api/items`; preserve the existing response shape and
50-item page size. Omitted, empty, or whitespace-only queries show all items.
Trim outer whitespace and limit the normalized query to 200 UTF-16 code units;
reject longer queries with 400 Problem Details, without truncation. Reject embedded
NUL. Preserve internal whitespace, punctuation, and accents without tokenization,
stemming, or Unicode compatibility normalization.

Match one contiguous literal substring within any one of name, notes, or location.
Do not join fields to create a match. Matching is case-insensitive and
accent-sensitive using explicit SQL Server `Latin1_General_100_CI_AS_SC` collation,
independent of the database default. Thus `BLUE stone` matches `Blue stone`, while
`cafe` does not match `café`. Percent signs, underscores, brackets, and escape
characters are ordinary text, never wildcard operators. Use a parameterized
literal substring predicate with real SQL regression coverage.

Apply tenant and search predicates before cursor filtering and the 51-row fetch
used to detect the next page. Every next-page request repeats the normalized query.
Changing or clearing the query starts a new traversal. Retain the existing cursor
format and validation; it remains an ordering boundary, not an authorization or
snapshot token. The API permits using an ordering boundary with another query;
the UI always resets it on a query change. Results reflect committed data at each
request and do not promise snapshot consistency during concurrent changes.

Keep the existing tenant/chronology index and introduce no migration or search
service. Arbitrary substring matching can scan the tenant's candidate rows;
an ordinary text B-tree does not remove that cost. Retain request cancellation and
the database command timeout. Measure representative SQL search execution plans
and latency during verification, including absent matches and later pages. Report
the dataset size and limits rather than claiming unbounded scale. A full-text or
specialized substring index requires separate evidence and design because SQL
full-text token semantics do not satisfy this literal substring contract.

## Interaction and state

Provide a labeled search input, Search submit button, and Clear action. Submit on
Enter or Search rather than issuing a request for every keystroke. Clear immediately
loads the unfiltered first page. Keep the selected Grid/List view when searching,
clearing, retrying, or changing appearance. Distinguish initial loading, loading more,
no matches, an empty collection, and failure; include accessible status and retry.
On a new search, remove previous-query results rather than presenting them as matches.
A failed later page preserves existing results and retries the same cursor. Ignore
outdated requests after a query change, navigation, or authentication change.

Keep query draft, submitted query, loaded summaries/cursor, view, selected item, and
scroll offset in memory owned by the authenticated application. Preserve one current
collection traversal when opening details and returning through the Back to collection
link or browser Back/Forward. Restore the loaded rows before position, and return
keyboard focus to the selected item without overriding the restored scroll position.
If that item is unavailable, use the collection heading as the focus fallback.
Update or invalidate cached photo summaries after successful detail photo changes so
returning does not advertise a removed or superseded photo.

Scope state to the current authenticated user and tenant. Clear it on sign-out,
authentication loss, or identity change, and discard stale asynchronous results.
Do not store private queries or item summaries in localStorage, sessionStorage,
browser history state, or page URLs. A full reload or new tab starts a fresh
collection; recovery across browser restarts is outside H3. API query strings are
necessary for the GET contract and must not be deliberately logged or copied into
telemetry or narrated demonstration data containing real private information.

## Compatibility and alternatives

Existing callers without `q` retain their results, cursors, and response shape.
Regenerate OpenAPI client declarations. No durable data or schema changes are
needed, so deployment rollback does not require data recovery.

Client-only filtering misses unloaded records. Full-text search changes literal
phrase behavior and adds an operational dependency. URL or browser-storage
persistence improves sharing/reload restoration but retains private search text
beyond the authenticated in-memory lifetime. A submit-based search provides an
explicit, predictable request boundary with less request churn than live search.

## Acceptance and delivery

Cover each searchable field,
case/accent semantics, literal wildcard characters, whitespace, limits, null fields,
empty queries, matches beyond the initial 50 items, multiple matching pages,
invalid cursors, cancellation/failure, and cross-tenant search under real SQL RLS.

Client and browser checks cover submit/clear, both views and thumbnails, stale
responses, retry of initial and subsequent pages, returning from details using
both navigation paths, restored position after loading multiple pages, and
clearing protected state on authentication loss. Verify keyboard operation,
accessible feedback, 44px targets, contrast, both themes, and no page overflow at
320 CSS pixels. Appearance changes must preserve search and position.

Follow [CONTRIBUTING](../../CONTRIBUTING.md) for verification gates and the
[development workflow](../development-workflow.md) for implementation and delivery. Production deployment
and merge require separate authorization.

## Verification evidence

Historical evidence from the 2026-09-07 implementation; not current verification.

SQL coverage includes literal punctuation, field boundaries, case/accent behavior, UTF-16 limits,
null/empty values, pagination beyond the unfiltered first page, authenticated tenant isolation,
cancellation, and denied reads followed by retry. Client coverage includes unavailable cached items,
stale responses, pagination retry, photo reconciliation, and authentication-state clearing.

Six bounded manual mutants were detected: the maximum-query boundary, accent sensitivity,
stale-response suppression, query forwarding, photo-cache invalidation, and immediate identity
clearing. Client mutations ran in an isolated copy. These are targeted experiments, not an
automated mutation-tool run or a repository-wide mutation score.

### SQL measurement and indexing limit

A disposable dataset contained 5,000 records in each of two tenants (10,000 total), with 250
matching records per tenant. Actual SQL plans for first-page, later-page, and absent-phrase queries
reported 22/19/22 ms elapsed respectively. Corresponding HTTP samples were 407/75/42 ms; the
first included cold API compilation. All three plans selected an Items clustered-index scan with
sorting and Top, plus photo and tenant-proof key seeks. The existing chronology index was retained
but was not selected by these plans. Search predicates and SQL RLS still enforce tenant isolation.
These are single local synthetic samples, not load testing or an unbounded scaling guarantee.

### Integrated application gates

`scripts/verify.ps1 -SkipDependencyInstall` passed locked restore, formatting, generated-contract
drift, current-source builds, all 416 server and 58 client tests, clean/upgrade/reversible/restore
migration drills, all 21 browser tests, and the published release-unit probe. Locked npm dependencies
were installed explicitly before this run. `scripts/smoke-container.ps1` also passed its hardened
SQL-backed non-root runtime and local Compose checks. No H3 schema migration was needed.

Browser checks cover 320px and desktop layouts, both themes,
keyboard operation, contrast/touch targets, no overflow, later-page result restoration through
app Back and browser Back/Forward, and initial/continued-page failures. Historical walkthrough footage was inspected at delivery; the retired walkthrough files are recorded in the [demonstration index](../demos/README.md).

Human collector usability, production deployment, public CA issuance, and SMTP delivery were not
verified by this increment. Reload deliberately clears the in-memory search traversal.

### Internal review correction

The authenticated memory owner notifies mounted collection subscribers on
photo changes, including completion after details unmounts. Subscriptions are removed on unmount;
late failures and completions for an old identity cannot alter the new identity's traversal.

For the final client-only change, all 62 client tests and all 22 browser tests passed, including
delayed photo completion and the original photo workflows. Lint/typecheck/build, the published
probe and hardened container/Compose probe were rerun successfully. The earlier 416-server-test and four-migration
evidence applies to unchanged server/schema code; that broader suite was not repeated for the
client-only fix. The walkthrough was refreshed from the corrected build.
