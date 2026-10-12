# Auction market research and results history

**Status: Proposed** — product proposal for review. This document does not authorize implementation,
source access, scheduled collection, purchases, or deployment.

## Summary and decision

Add a tenant-owned market research library to Workbench so that preparing for a daily auction
starts with what we already know. Save lots, product links, comparable listings, observations,
valuation decisions, and the next morning's published auction results in connected records.
Reuse the research, recheck what can change, and retain the history that explains each decision.

The first useful release should support one complete cycle:

1. Prepare the published lots for a live show, including descriptions and photo references.
2. Retrieve past research and find current wholesale and retail comparisons only where needed.
3. Review an evidence-backed whole-lot valuation and suggested bid ceiling.
4. Revisit the saved product links the following morning to capture published results.
5. Use those observations in the next auction's research without presenting yesterday's prices
   as current inventory or treating a published winning bid as a verified completed transaction.

This is a concrete business use case for the proposed [MCP agent interface](2026-09-28-mcp-agent-interface.md):
an authorized agent can retrieve and contribute research while a person inspects and continues the
same work in Workbench. Recommend accepting the product direction and validating the evidence and
source-access contracts before implementation planning. Exact screens, schemas, tool names, and
delivery dates remain design decisions.

## Problem, users, and outcomes

Daily auction preparation repeats much of the same work: opening product pages, interpreting lot
descriptions, finding comparable stock, normalizing quantities, and explaining a price range.
When that work lives only in chat or a daily spreadsheet, useful references are hard to retrieve,
the reasoning behind a number is lost, and sold or changed listings can silently become today's
evidence. Published auction outcomes are another opportunity: the links already saved for the
preview can tell us what the market did after the show.

The primary user is a gemstone or jewelry buyer researching acquisitions for a business.
Collectors can use the same records without configuring accounting or a resale model. A research
assistant needs bounded access to contribute evidence; a reviewer needs to know where an assertion
came from and what is still uncertain.

Desired outcomes:

- Spend less time rediscovering sources and more time checking differences that affect value.
- Explain a wholesale range, retail comparison, and buying stance from inspectable evidence.
- Preserve both successful and rejected comparisons so tomorrow's work improves.
- Build a reliable history of published auction results across all tracked lots, including
  unsold lots and unavailable results, rather than only memorable wins.
- Continue human work through MCP, and agent work in the browser, without separate databases.

## Current product and fit

Workbench already has tenant-owned collection records, acquisition evidence, supplier purchase
drafts with source links and explicit unknown costs, and a sourced gem reference. The
[product context](../../PRODUCT.md), [collection guide](../collection.md),
[purchasing guide](../purchasing.md), and [gem reference guide](../gem-reference.md) describe those
implemented boundaries. The MCP interface and its
[delivery roadmap](2026-09-28-mcp-milestones-and-stories.md) remain proposals.

Market research extends acquisition planning. A candidate lot can be researched without creating
inventory, a placed purchase, a supplier payment, or an accounting entry. A later authorized
purchase may link back to its research, but this proposal does not implement that transition.
Gem reference entries can supply vocabulary where available; a species-level reference does not
prove an individual lot's identity, treatment, origin, or value. Inventory-reference linking must
not become an accidental prerequisite or an implied delivered feature.

Follow the existing React / ASP.NET Core / SQL Server application boundaries and shared command
model described in [Architecture](../ARCHITECTURE.md). A new search service, vector database,
pricing engine, or separate MCP-owned store needs evidence before it becomes a dependency.

## Scope and deliberate limits

### MVP

- Tenant-private, searchable records for auction occurrences, their lots, saved source links,
  comparable listings, timestamped observations, and research sessions.
- Manual capture and bounded, evidence-backed agent ingestion. Cover every published preview lot
  available to the authorized source reader, with explicit coverage gaps and late additions.
- Reusable comparisons, transparent inclusion/exclusion decisions, and a freshness/recheck queue.
- Versioned whole-lot valuation reports with uncertainty, assumptions, and source references.
- A next-morning results workflow that revisits saved lot links, stores published outcomes,
  exposes partial failures, and can run manually or on an explicitly enabled schedule.
- Human review, conflict recovery, audit history, and web/MCP capability mapping.
- An Excel daily report and portable structured research export with source and version identifiers.

### Non-goals

- Automated bidding, purchases, payment, sale execution, or financial posting.
- An authoritative universal gemstone price list, automatic gem identification, independent
  authentication from photos, or guaranteed profit.
- Invented wholesale transactions or a standard percentage discount from retail used as wholesale.
- Continuous crawling of the web, bypassing source restrictions, or unattended access to sites
  without a supported authorized route.
- Cross-tenant shared market data, resale of third-party content, exhaustive historical backfill,
  or bulk storage of copyrighted page/media collections.
- A predictive pricing model, image-similarity model, or full inventory/accounting integration.

The MVP captures useful evidence before automating more research. This follows the
[development workflow's first-principles order](../development-workflow.md#first-principles-the-five-step-algorithm):
justify the repeated work, remove unnecessary collection, simplify capture/reuse, measure time
saved, and then automate the remaining checks. An empty or uncertain result is a valid outcome.

## Daily user journey

### Before the show

Create a research session for a specific seller, show date, timezone, and auction occurrence.
Capture the available preview's lot roster and coverage time. Each lot keeps its seller SKU or
lot identifier, source URL, description, permitted photo references, quantities, and stated facts.
The session shows how many published lots were captured, reviewed, or unavailable. A changing
preview creates additions or revisions; it must not silently drop an earlier lot.

For each lot, Workbench first retrieves prior observations, similar comparisons, rejected matches,
and previous auction occurrences. The researcher checks the current product facts and reuses
eligible comparisons. Search expands outward only for missing evidence, outdated availability,
material differences, or a deliberate new source. A saved search attempt can say **No close
comparable found**, with its query, scope, date, and exclusion reasons; this is not a claim that no
comparison exists anywhere.

The review view presents the whole lot in USD with wholesale low/high, retail average where
supported, buying stance, suggested maximum bid, rationale, and references. Values that cannot be
supported stay unknown. The buyer can inspect included and excluded evidence, correct a mismatch,
change a named assumption, and save a new valuation version. A final pre-bid review highlights
anything that changed since the report, including availability, lot contents, price, and costs.

### The following morning

Use the saved auction occurrence and original lot links to create a results-check batch. The user
chooses the morning time and timezone, and explicitly enables any recurring run. Do not infer a
timezone from the server or silently start a schedule when saving a link. A manual **Check results**
action uses the same workflow.

The authorized source reader visits each saved page or the provider's supported results source.
It verifies the original auction occurrence before attaching a result. A reusable SKU or URL alone
cannot establish that a new listing is the same auction. A relisted lot gets a new occurrence,
linked to the earlier one when supported by evidence.

Store the published outcome, amount and currency if present, the source's event time when known,
the capture time, and evidence of what the source actually said. Keep **published winning bid /
hammer**, **source reports sold; transaction unverified**, **verified completed transaction**,
**unsold**, **reserve not met**, **withdrawn**, and **relisted** distinct. A sold badge is useful
evidence even when price is unknown. Do not infer a reserve failure from a missing result, or a
completed paid transaction from a sold badge. Availability of a current listing is a separate
observation.

The batch reports completed, pending publication, source unavailable, access blocked, identity
ambiguous, and review-needed rows. Those are capture states, not sale outcomes. Every original lot
remains visible in the coverage denominator. A failed fetch never becomes a zero-dollar result.

Proposed pilot retry policy: check once the next morning and once on each of the following two
mornings for pending publication or transient unavailability, honoring source limits. Then leave
an explicit unresolved row for manual follow-up; later checks remain possible. An access denial
pauses that source for human action rather than prompting repeated attempts or another route.
This bounded policy and the actual morning time require review before scheduling is implemented.

Review results alongside the original valuation, stance, and bid ceiling. Report, for example,
how many outcomes were available and whether the published hammer fell above the suggested cap.
That comparison can inform the next research session; it does not establish resale profit or
prove a valuation was correct. New results and corrections never rewrite the original report.

## Evidence and valuation rules

### Preserve what the source supports

Every substantive observation has a source/seller, canonical URL plus meaningful original locator,
source SKU where available, captured-at time, recorder, and evidence locator or concise permitted
excerpt. Record source publication/event time separately when known. A URL alone is not enough to
show what was observed after the page changes. Retain raw stated units beside any normalization.

Distinguish seller claims, document/certificate assertions, photo observations, user assumptions,
and independently verified findings. Unknown, unstated, conflicting, and explicitly absent are
different states. A photo observation must not silently become a claim about treatment or origin.
Certificate identity, issuer, referenced stone or subset, and actual scope matter. A user may
choose to rely on seller-supplied certification within its stated scope; record that as a
valuation assumption, not independent authentication by Workbench.

Useful comparison attributes include material, cut, color, clarity description, dimensions,
treatment, stated origin, certification scope, condition, stone count, lot quantity, metal/fineness,
and price basis. Store only supported attributes; do not require invented grades to save a record.
Total jewelry weight in grams and gemstone weight in carats are separate measures. Preserve
per-stone versus total carats, per-piece versus parcel pricing, minimum order quantity, and tier
conditions. Normalizing a whole lot must explain the quantity and denominator used; missing inputs
block that normalization rather than inviting a guess.

### Keep price evidence in the correct category

- Separate retail asking prices, trade/wholesale asking prices, published auction hammer prices,
  and verified realized transactions. A wholesale listing supports an available trade offer,
  not evidence of a wholesale sale. Record why a source is classified as trade or retail.
- Preserve amount, original currency, availability, observation time, quantity/MOQ, and included or
  excluded costs. USD conversion records its rate source, effective time, and calculation.
- A retail average uses only close, currently available inventory whose availability has been
  checked within the freshness rule. Show included count and comparison basis. One listing is
  labeled **Single current comparable**, not presented as a robust average. Sold, historical,
  stock-unconfirmed, and materially mismatched listings remain inspectable but are excluded.
- Store a comparability decision for each lot/comparison pair, with relevant differences,
  normalization, inclusion or exclusion, reviewer, and reason. Similarity is a search aid, not an
  automatic license to average prices. Do not count mirrored listings as independent evidence.
- Wholesale low/high must disclose their evidence and any reasoned adjustments. With insufficient
  support, use **Wholesale range not established** instead of fabricating transactions or
  converting retail to wholesale with an arbitrary haircut. Scarcity or replacement value is
  separate from a realizable wholesale exit value.

### Make the bid model explainable

A valuation version references exact lot and observation versions, selected comparisons, excluded
candidates, assumptions, calculation method, exit basis, currency conversion, and reviewer.
Asking retail inventory does not automatically supply an expected resale price. A qualitative
stance remains available when a reliable numeric cap cannot be calculated.

For a resale scenario, a proposed illustrative target is **20% return on all costs**, configurable
per valuation. It is not a 20% sales margin or a promised return. Define return as
`(gross exit proceeds - total costs) / total costs`.
Total costs include hammer, buyer's premium, acquisition and selling fees, shipping, tax, testing,
and other applicable preparation or selling costs without counting a cost twice. Solve
for a hammer cap using the actual fee/tax basis and show the calculation. Unknown fees remain
unknown, not zero, and prevent an unconditional calculated cap; a clearly labeled scenario may
use explicit assumptions. No universal flat testing reserve is imposed: a user-entered zero
testing cost remains a visible assumption with a reason.

Use text labels alongside optional colors: **Green: corroborated trade-inventory support**,
**Yellow: potential value, not yet corroborated**, and **Neutral: hold / pass**. Evidence strength
and the buyer's action are separate fields; green does not mean authenticated, profitable, or
automatically worth bidding on. Explain the stance and any remaining conditions in words.

## Search, freshness, and history

Search starts with exact seller/SKU/URL/auction identity, then structured filters for material,
item type, size/weight and unit, cut, quality descriptions, treatment, certificate scope, price
category, seller, date, availability, and evidence status. Text search finds notes and aliases.
Show why a result matched and which important fields are missing. A first release needs bounded
search and transparent filters; semantic search can follow measured retrieval gaps.

Suggested pilot defaults, to validate with actual source behavior:

| Evidence | Reuse rule |
| --- | --- |
| Current asking price and stock | Recheck within 24 hours for a live valuation and again before a bid decision if conditions may have changed. A failed check means stock/price is unconfirmed. |
| Seller lot description | Capture each auction occurrence and recheck before final review. Changed contents invalidate dependent normalization and flag affected valuations. |
| Historical asking price or published result | Retain as a dated historical observation. It never becomes current available inventory merely because it was recently retrieved. |
| Certificate or other stable document | Reuse its scoped assertions while identity and document version match; flag replacements, conflicts, and newly relevant limitations. |

Freshness is visible on each observation and derived report. An agent reading a stored record
does not refresh its source verification time. Preserve the old price and append a new observation
when a source changes. Correct errors with an attributed superseding record and reason; preserve
the earlier version and its report links. Archive removes an item from default discovery without
breaking references. Retention or deletion must follow an explicit privacy and recovery policy,
not an unlimited promise to keep third-party content forever.

## Conceptual records and relationships

These are domain concepts, not a proposed SQL schema:

| Record | Responsibility |
| --- | --- |
| Research session | One show/date/occurrence, preview coverage, researcher, review status, and report versions. |
| Auction lot occurrence | The candidate as offered in a particular event, its seller identifier, source links, lot contents and revisions. It may link to other occurrences without merging them. |
| Source listing | A seller's identifiable listing or document, including aliases, permitted access route, and content-retention limits. A comparable may be reused across many lots. |
| Observation | A dated, attributed fact or claim about that listing or lot: description, price, stock, document scope, or published result. |
| Comparison decision | A specific lot-to-observation assessment with matching differences, normalized basis, inclusion/exclusion, and reason. |
| Valuation version | A saved assessment with evidence versions, assumptions, calculation/exit basis, stance and suggested cap. |
| Results-check batch | The intended lot set, authorized run configuration, per-lot capture state, attempts and completion summary. |
| Auction outcome observation | The source-supported disposition and any price, tied to the original occurrence and capture evidence; later corrections supersede rather than overwrite. |

Deduplication uses source identity and explicit occurrence keys, not a title match. Equivalent URL
variants may map to one listing without dropping variants that identify an auction or item. An
identical import is a no-op with its original receipt; new observations are distinct from duplicate
delivery of the same observation. Ambiguous matches stay unresolved for review. Corrections,
comparison decisions, and valuations have their own version history.

## MCP integration and authority

Use the MCP proposal's tenant-owned service principals, predefined roles, shared commands/queries,
server-derived tenant context, and protected SQL boundaries. Research is an additional capability
family in the parity inventory, including search/detail, capture, correction, comparison decisions,
valuation preparation, results batches, and exports. A browser workflow and its MCP equivalent must
produce the same business result and conflict behavior.

- Offer a research-reader preset for retrieval and an explicitly selected research-editor preset
  for ordinary evidence capture and descriptive corrections. Exact role names and composition need
  catalog review; do not silently add research write access to existing grants.
- Return bounded structured evidence, missing fields, freshness, provenance, version tokens, and
  inclusion/exclusion reasons. Distinguish preparing an assessment from approving its assumptions.
- Ordinary authorized descriptive writes need not require a click for every fact. Human review is
  required to resolve ambiguous identities, approve material valuation assumptions, and accept
  changes to a reviewed decision. The independent-approval categories remain subject to the MCP
  proposal's policy decision; any required approval must be enforced server-side for exact inputs
  and versions, not an agent-supplied flag. A source claim remains a claim after capture or review.
- Ingestion accepts bounded batches with per-record outcomes, stable retry identities, and
  conflict detection. Reusing an operation identity with changed inputs fails; lost responses can
  be reconciled through authorized receipts. Attribution records the actual human or principal.
- Recheck authority before each protected write and scheduled execution. Disabling the principal
  or schedule stops future authorized work. A schedule carries only the approved source set,
  occurrence scope, timing, and research permissions; it cannot bid or expand its own authority.

External retrieval belongs to a supported source adapter or the authorized agent's source
integration. The research MCP surface must not accept a URL as permission for arbitrary server
network access. Any Workbench-hosted fetching requires a separate reviewed contract for destinations,
redirects, private-network/SSRF protection, size/type limits, timeouts, rate limits, and rights.
Saved text, photos, descriptions, and documents are untrusted content. Instructions inside them
cannot approve actions, change identity, trigger credential entry, or redirect exports.

This proposal does not select MCP authentication protocols, credentials, SDKs, or final tool schemas.
It depends on the existing MCP identity/authorization, outcome/retry, and transfer contracts where
applicable. A human-only pilot can validate the domain first; it must visibly report MCP gaps and
cannot be labeled the complete web/MCP release.

## Delivery, portability, and recovery

The daily Excel view retains the familiar ten-column review shape:
**SKU, Item, Weight, Wholesale low, Wholesale high, Retail avg, Buying stance, Suggested max bid,
Reason, References**. Amounts describe the whole lot in USD, weight cells state units and scope,
and references resolve to evidence with dates and inclusion decisions. Extra sheets can expose
comparison counts, freshness, assumptions, calculation details and next-morning outcomes. A header
records the session, auction occurrence, report version, generation time, and coverage. Text labels
carry meaning even without color.

Excel is an output, not the only copy of the database. Provide a structured export of linked
research records, versions, observations, and permitted evidence references. Untrusted source text
must export as inert text, never executable spreadsheet formulas or active content. Use the existing
[export guidance](../collection-export.md) as an interoperability reference without changing its
current formats or implying an export is a restorable backup. Imports, if later supported, need
their own preview, conflict, provenance, and deduplication rules.

Keep all research private to the tenant by default, including signed URLs, receipts and exports.
Source links must not contain reusable credentials. Do not publish private auction research as
repository fixtures; acceptance uses synthetic examples or separately authorized public samples.
Store concise facts and links by default. Only retain photographs, page snapshots or document
copies when authorized and compatible with the source's rights and access terms.

New storage must be additive and preserve existing collection, purchasing and accounting behavior.
Research corrections cannot rewrite financial records. Failed ingestion preserves saved work and
reports partial outcomes. Duplicate or overlapping scheduled runs must not duplicate observations;
stopping a run leaves its completed results and unresolved remainder visible. Feature disablement
stops new work while preserving authorized reads/exports. Backup and restore must cover research
metadata and permitted evidence consistently; schema downgrade is not a data-recovery strategy.
Detailed migration and recovery design follows acceptance, under the existing repository runbooks.

## Alternatives

| Approach | Trade-off |
| --- | --- |
| Keep only daily spreadsheets and chat | Low initial effort, but weak reuse, provenance, deduplication and next-day reconciliation. Keep spreadsheets as outputs. |
| Save URLs and a current price per product | Simple capture, but mutable pages erase history and reused URLs conflate separate auctions. Insufficient for trustworthy comparisons. |
| Tenant-owned observations plus versioned assessments | Recommended. Supports reuse, corrections and human/agent continuity with a bounded first release. Requires disciplined identity and evidence rules. |
| Shared pricing warehouse or predictive engine | Potential later benefit, but requires licensing, cross-tenant consent, calibration data and substantial maintenance before it can justify itself. |

## Acceptance and success measures

The release is accepted only when the integrated workflow demonstrates:

1. A reviewer captures the complete accessible lot roster, with explicit unavailable/late lots,
   and resumes the same saved session in the browser and through authorized MCP operations.
2. Research from one show is retrieved for another, with included/excluded evidence and the reason
   for reuse visible. Freshness checks find a changed or sold listing and exclude it from live retail.
3. Fixtures with grams versus carats, per-stone versus total weight, MOQ, mixed currencies,
   jewelry versus loose stones, and certificate subsets cannot produce undisclosed normalization.
4. A single retail comparison is labeled as such; zero eligible comparisons produces no fabricated
   average. Historical asks, sold listings, unknown stock and mirrored listings do not inflate it.
5. A valuation with unknown fees exposes that uncertainty. A reviewed scenario reproduces its cap,
   uses gross proceeds and total acquisition/preparation/selling costs for return on all costs,
   and leaves its original version unchanged after later evidence.
6. A next-morning batch checks every original occurrence and correctly handles published hammer,
   reported-sold/unverified, verified transaction, unsold, reserve failure, withdrawn, relisted,
   delayed and unavailable cases, including a reported sale with an unknown price.
   No result is attached to a later auction merely because it reuses a URL or SKU.
7. A retry after a lost response has one effect; overlapping runs, duplicate ingestion, stale
   corrections and conflicting identities produce recoverable, understandable outcomes.
8. Reader/editor restrictions, cross-tenant ID substitution, changed approvals, revoked principals,
   disabled schedules, and malicious instructions embedded in source content are tested across
   actual API and SQL boundaries. No research operation can bid, purchase, or post finances.
9. Excel and structured exports reproduce the selected report version, preserve source/category
   distinctions, neutralize formula injection in source text, and are accessible only to authorized
   users. Restore preserves linked history.
10. The pilot uses a supported source-access route and passes source-to-record spot checks. Missing
    source support is reported as a blocker, not presented as automated results coverage.

Before the pilot, record a baseline for manual preparation time and repeated source searches.
Proposed pilot success target: at least a 30% reduction in median preparation time across ten
comparable auction sessions, without dropping lot coverage or evidence quality. This is a target,
not a measured result or delivery promise. Track reuse rate, time spent rechecking, correction
rate, duplicate rate, proportion of current eligible comparisons, and next-morning coverage with
denominators. Investigate a time gain caused by reviewing fewer lots rather than calling it success.

Outcome calibration must include unavailable and unsold lots, separate source/seller/date cohorts,
and disclose sample sizes. Historical hammer distributions can inform research but do not establish
resale value, liquidity, or profit. Avoid learning only from purchased lots or treating asking prices
and verified sales as interchangeable training data.

## Milestones and decisions

These are reviewable product outcomes, not implementation tasks or an approved schedule:

| Milestone | Exit evidence and dependencies |
| --- | --- |
| MR-01: Validate the capture contract | A small synthetic or authorized sample exercises lot identity, units, evidence categories, revised pages and next-day outcomes. Confirm at least one lawful, technically supported results source; resolve morning timezone and access limits. |
| MR-02: Deliver reusable research | Save/search/detail/history and comparison decisions work in a tenant-private human workflow; prove conflicts, deduplication, source attribution and export/recovery boundaries. Depends on the accepted MR-01 contract. |
| MR-03: Produce the daily decision report | Versioned whole-lot assessment, disclosed assumptions, current-comparison rules, final recheck queue and ten-column Excel report pass representative cases. Depends on MR-02. |
| MR-04: Close the next-morning loop | Manual and explicitly scheduled batches retain per-lot outcomes, original occurrence identity, bounded retries and corrections, then compare against the original report. Depends on MR-02 and the reviewed source-access/scheduling contract; report linkage integrates with MR-03. |
| MR-05: Demonstrate the integrated MCP cycle | Reconcile capability inventory and predefined roles; prove browser/agent continuation, authority/revocation, receipts, safe evidence transfer, and the complete daily/next-morning journey. Depends on MR-02–04 and applicable accepted MCP foundations. |

Open decisions before implementation planning:

- Which initial auction source exposes sufficiently reliable result pages, and what can its
  permitted interface actually distinguish: final bid, reserve outcome, sale, payment, cancellation?
- What morning time/timezone, source rate limits, retry window, retention policy, and user-visible
  review queue suit the pilot? Which scheduler or agent runtime owns execution and operational cost?
- Which predefined roles and server-enforced human approvals cover evidence corrections, reviewed
  valuations, schedules and exports without imposing approval on every ordinary descriptive save?
- Which match tolerances and freshness defaults survive the pilot for loose stones, jewelry and
  parcels? What minimum evidence is required for a numeric wholesale range and unconditional cap?
- What source media may be retained, for how long, and what happens to citations when a copy must
  be removed? How should authorized private trade evidence be represented without redistribution?

Primary risks are sparse or biased comparisons, changing source pages, ambiguous sale status,
identity mistakes across relistings, and false precision in valuations. The product earns trust by
showing those limits, preserving provenance, and making review efficient. Its central benefit is
cumulative research: tomorrow begins with a useful history and a clear list of what still needs
checking.
