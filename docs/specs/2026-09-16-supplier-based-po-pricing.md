# PO-03 follow-up: supplier-based line pricing

Status: approved by the owner on 2026-09-16. Supersedes the separate ordered/priced quantity design in the PO-03 implementation specification.

## Problem and choice
The owner records a purchase using the basis on which the supplier charges. Recording item count and a different pricing weight together is outside this workflow. Each line therefore has one optional positive quantity and one optional unit, plus an explicit pricing mode and nullable price. Retain the existing description, supplier metadata, notes, sources, and draft lifecycle.

The editor shows Description, Quantity + Unit, then a Pricing radio group: Per unit or Total line. Default new lines to Per unit, with blank quantity, unit and price. One amount field follows, labelled Unit price or Total line price. For a selected unit the per-unit choice can explain that basis. No separate pricing unit, denominator, or priced quantity appears on ordinary lines. Keep compact saved disclosures, optional details and sticky truthful save status. Visible labels omit index suffixes; accessible names retain line context.

Price entry retains cents-style typing and two decimals by default; meaningful third/fourth decimal digits remain available. Quantities use ordinary decimal input. Switching mode retains the entered number and explicitly changes its meaning; show the current calculation. Null is unknown; zero is a valid price. Clearing prices also clears retained legacy price data before currency changes.

## Contract and calculation
Add a versioned V4 API rather than changing V1-V3 meanings. DraftEntryV4 carries id, description, notes, sourceLink, quantity, unitOfMeasure, priceMode (perUnit or lineTotal), price, supplierSku and itemType. Preserve existing ordering, field limits, authorization, closed JSON shape, optimistic concurrency and request receipts. Retain nullable indicativePrice and nullable legacyPricing solely for unresolved older data.

Per-unit amount = quantity * price, requiring both quantity and unit. Total-line amount = price, independent of quantity and unit. Missing price yields unknown. Require currency for any monetary value, including retained legacy quotes. Exact arithmetic and four-place line rounding remain server-owned; avoid binary floating point. Per-unit input retains the existing 15 integer digit limit; total-line prices allow 19 integer digits so previously valid calculated totals can be represented. Existing gross/sum bounds apply.

## Existing data and compatibility
Do not rewrite an applied migration or silently mutate a draft on GET. A forward migration adds the V4 storage boundary. V4 reads project older content into the new representation; only an explicit save persists it. Old APIs retain their established behavior and cannot overwrite content they cannot represent.

For complete structured quotes, use the supplier's effective quantity and pricing unit. Normalize a batch quote to a per-unit amount only when exactly representable within four decimal places and it reproduces the previous rounded total. Otherwise retain its calculated amount as a total-line price. Thus 10 pieces priced as 12.5 carats at 20 becomes 12.5 carats at 20; 250 pieces at 8 per 100 becomes 250 pieces at 0.08. Both retain their previous amounts.

An old reference amount with no basis remains indicativePrice, not an inferred total. Offer explicit Use as unit price and Use as total line price actions. An unresolved structured quote retains its original six pricing fields in legacyPricing (quantity, unitOfMeasure, unitPrice, pricingUnit, pricePerQuantity, pricingQuantity). Display it read-only and exclude it from estimates until deliberately replaced. Saving unrelated edits must preserve this data exactly. Do not hide unresolved values or append them into potentially full notes.

## Acceptance and verification
Cover per-unit and total-line save/reload, blank and zero amounts, partial drafts, exact batch normalization, non-terminating division fallback, old mixed-unit conversion, unresolved references/quotes, currency clearing, immutable retry, conflicts, old-client protection and SQL validation. Verify both appearances, mobile and desktop, keyboard mode choice, accessible names, error focus and unknown/pending estimates. Use focused regression tests and meaningful mutation checks, then required full source/release and container gates. Rollback must not erase V4 draft data; deployment restores follow the existing migration/backup runbooks.
