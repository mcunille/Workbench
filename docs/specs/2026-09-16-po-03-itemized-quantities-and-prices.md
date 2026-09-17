# PO-03: itemized quantities and supplier-based prices

**Status:** Implemented. Approved by the owner on 2026-09-16, including the supplier-based pricing revision. Interface design uses impeccable within the existing Tanzanite surface. This document consolidates the original PO-03 design and its supplier-pricing follow-up.

## Problem, scope and baseline

Implement PO-03 from the [purchasing scenario](2026-09-11-purchase-orders-and-purchase-finances.md).
The owner records each purchase line on the basis the supplier charges: pieces, carats, a parcel,
or a fixed line total. Recording an item count and a different pricing weight together is outside
this workflow. Each line therefore has one optional quantity and unit, plus either a per-unit price
or a total-line price. Preserve incomplete, resumable drafts.

The PO-01/PO-02 baseline stores ordered JSON entries with UUID, description, notes, source link and
nullable reference price. Both HTTP and restricted SQL commands validate a closed representation.
Reference prices have no stated basis. Preserve receipts, supplier snapshots, permanent references,
ordering, authorization and concurrency recovery.

This increment adds structured draft lines and explainable merchandise estimates. It does not commit
orders, create inventory or financial obligations, allocate costs, or implement discounts, charges,
invoices, payments, shipments, receipts or amendments. A draft estimate is not a confirmed amount due.

## Current line contract

Retain line UUIDs and array order. Optional fields are required nullable properties on the wire;
omission and unknown properties are rejected. Empty drafts and incomplete lines remain saveable.
Reject invalid supplied values instead of silently rounding or discarding them.

| Field | Meaning and validation |
| --- | --- |
| `id` | Nonempty UUID, unique within the draft and stable across edits and reloads. |
| `description` | Optional text, maximum 500 UTF-16 code units. |
| `quantity` | Supplier quantity: positive decimal string, up to 9 integer digits and 4 decimal places. Null means unknown. |
| `unitOfMeasure` | Null or `piece`, `carat`, `gram`, `kilogram`, `ounce`, `troyOunce`, `millimeter`, `centimeter`, `meter`, `parcel`, `pair`, `set`, `pack`, `box`, `lot`. Ounce is avoirdupois, distinct from troy ounce. |
| `priceMode` | Required `perUnit` or `lineTotal`. New UI lines default to `perUnit`. |
| `price` | Nullable nonnegative decimal string with at most 4 decimal places; up to 15 integer digits for per-unit prices and 19 for line totals. Zero is explicit and valid. |
| `supplierSku` | Optional trimmed text, maximum 200 code units; not an inventory identity. |
| `itemType` | Optional trimmed text, maximum 100 code units; suggestions Gemstone, Finding, Material, Supply, Jewelry, Other. Not a collection classification or foreign key. |
| `notes`, `sourceLink` | Existing limits and safe HTTP/HTTPS validation retained. |
| `indicativePrice` | Nullable legacy reference amount without a recorded basis; retained until explicitly adopted or cleared. |
| `legacyPricing` | Nullable compatibility object retaining the original quantity, unitOfMeasure, unitPrice, pricingUnit, pricePerQuantity and pricingQuantity of an unresolved older structured quote. |

Quantities permit fractions even for count and packaging units. Units label the owner's measure;
they do not impose inventory counts or automatic conversions. Custom units and conversion tables
remain deferred. Quantity without a unit is saveable; a per-unit amount cannot be calculated until
both are present. A total-line price works without quantity or unit. Currency is required for any
monetary value, including legacy fields. Current price cannot coexist with unresolved legacy pricing;
older records containing both a reference and structured quote must retain both visibly until resolved.
Description remains optional until PO-04 defines commitment validity.

## Arithmetic and unknown values

Use decimal strings on the wire and exact decimal/rational arithmetic, never binary floating point.
Normalize valid values to four decimal places for storage and fingerprinting. Reject signs, exponent
notation, separators, whitespace and excess precision; UI entry may normalize pasted ordinary decimals.

- Per-unit line gross = quantity × price, requiring quantity and unit.
- Total-line gross = price, independently of quantity and unit.
- Missing price or incomplete per-unit basis yields unknown gross, including zero with a missing basis.
- Round each calculated gross once to four decimal places, midpoint away from zero. Sum rounded
  lines without a second rounding step. Bound line gross to 19 integer digits plus four decimals;
  a 100-line sum needs up to 21 integer digits. Reject overflow with an error on the affected line.

Price entry and display default to two decimals. Preserve meaningful third/fourth decimal digits
when full precision is required. This is draft estimation policy, not invoice/tax settlement policy.
The server owns calculation; return read-only line gross, incomplete-line count and subtotal outside
the writable draft. The authenticated, antiforgery-protected V4 calculate endpoint uses the same
validation without writes or receipts. Debounce requests, cancel obsolete work and ignore stale
responses. Pending/failed preview is explicit; save does not depend on a successful preview request.

Show **Merchandise estimate** and “Before discounts, shipping and tax.” for a complete sum. If any
line is incomplete, show **Known line subtotal** and the number needing quantity or pricing details.
With no complete priced lines, show Unknown, not zero. An empty draft has no estimate. An explicitly
free complete line contributes zero. Never sum quantities across units or orders.

| Entered example | Formula shown | Draft line gross |
| --- | --- | ---: |
| 10 pieces at USD 20 per piece | 10 pieces × 20 | USD 200.00 |
| 12.5 carats at USD 20 per carat | 12.5 carats × 20 | USD 250.00 |
| 1 parcel at USD 300 per parcel | 1 parcel × 300 | USD 300.00 |
| 250 pieces at USD 0.08 per piece | 250 pieces × 0.08 | USD 20.00 |
| USD 20 total, quantity unknown | Total line price | USD 20.00 |
| 0.5 carats at USD 0.0001 per carat | 0.5 carats × 0.0001 | USD 0.0001 |

## Editor interaction

Use Operate mode within the existing Tanzanite editor, typography and combined label/field controls.
Keep order details, Order lines, notes/sources and the explicit Save draft toolbar. Saved lines reopen
as compact disclosures showing description, quantity and estimate; new/restored lines open for editing.
Validation reveals affected lines and optional fields. Add line appears at both ends of the list,
with the lower action and Undo removal before Merchandise estimate. Save status remains visible and
truthfully distinguishes saved, local, pending and uncertain outcomes.

Within each line show Description, Quantity + Unit, then a Pricing radio group: **Per unit** or
**Total line**. One amount field follows, labelled **Unit price** or **Total line price**, followed by
its calculation. New quantity, unit and price fields start blank. No separate pricing unit,
denominator or priced quantity appears on ordinary lines. Switching modes retains the entered amount
and explicitly changes its meaning. Visible labels omit line numbers; accessible names retain context.

Price input retains cents-style typing and two-decimal defaults: typing 1234 enters 12.34. Pasted
amounts preserve their magnitude, including whole numbers. Extra precision preserves significant
third/fourth digits without rounding. Quantities use normal decimal input and omit insignificant
trailing zeros on display, while the API keeps its canonical four-place format.

Supplier SKU, item type, notes and link share a Line details disclosure with a populated-value summary;
metadata alone does not force expansion. Stack the same DOM order on narrow screens. Preserve inline
errors and linked summaries, keyboard operation, focus after add/remove, Undo removal, saved/local
comparison, uncertain-save retries and unsaved-navigation protection. Comparison includes every
editable field and retained legacy data, not merely the computed amount.

Clear all prices clears current and legacy monetary fields, retains quantity/unit, names the affected
count, and preserves confirmation and the Undo-history boundary. Currency changes require saved prices
to be cleared first; amounts must never be reinterpreted as another currency.

## API, persistence and older data

Use `/api/v4/purchase-order-drafts` list/create/detail/update/delete routes and
`POST /api/v4/purchase-order-drafts/calculate` with `{draft}` input. Retain existing envelopes,
pagination/search, field limits, authority, saved versions and compact receipt semantics. Lists do not
add totals or line search. Generate precise OpenAPI DTOs and field errors. Validation remains HTTP 400
`draft_validation_failed`; retain concurrency/replay codes and indexed field paths. Missing and foreign
draft identities remain indistinguishable. Preview is private/no-store and never fetches links or logs
bodies. Keep 100-line, 20-source-link, request/body and 1 MiB UTF-16 aggregate limits.

V4 writes content schema 3 and fingerprint 4. Preserve V1/V2 canonicalizers and receipt bytes; their
create/update routes remain replay-only. Exact old requests resolve, changed-input reuse conflicts,
and unmatched writes return 426 `draft_contract_reload_required`. V3 can still write schema 1/2 and
replay receipts, but cannot read or overwrite schema 3. Resolve recognized retries before compatibility
rejection; enforce the V3 update guard under the row lock. Delete retains version checks, permanent
PO identity and receipts while clearing content. Do not project a unit price as a basisless reference.

V4 GET projects older content without persisting it. For a complete structured quote, use the supplier's
effective quantity and pricing unit. Normalize a batch rate only if exactly representable within four
decimals and it reproduces the old rounded total; otherwise retain that gross as a total-line price.
Thus 10 pieces priced as 12.5 carats at 20 becomes 12.5 carats at 20, while 250 pieces priced at 8 per
100 becomes 250 pieces at 0.08. Neither conversion changes the amount. Only explicit save persists it.

An old amount with no basis remains **Reference price — basis not recorded**. Offer **Use as unit price**
and **Use as total line price**; adopting it clears legacy pricing fields in local state. An unresolved
structured quote retains all six original fields in `legacyPricing`, is displayed read-only, and stays
out of estimates until deliberately replaced. Unrelated saves preserve its data. Do not hide unresolved
values or append them to potentially full notes. Replacement and clearing follow unsaved-state recovery.

Keep the bounded JSON aggregate and atomic replacement. Restricted SQL enforces closed shape, numeric,
unit, compatibility, gross and size bounds, tenant RLS, relationships, request locks, rowversions and
atomic receipts. Runtime roles have no direct UPDATE/DELETE. Preserve supplier/archive locking and
counters. Calculation results are derived, never independently writable storage.

## Migration and recovery

One consolidated unmerged migration, `20260917010000_AddSupplierBasedDraftPricing`, follows PO-02.
It extends content/fingerprint constraints, installs V3 compatibility commands before V4 commands,
and advances readiness and backup markers. Preserve the final designer/model snapshot and provisioning
requirements. Drain incompatible writers before deployment. Destructive Down remains blocked; use
forward correction or guarded recovery under the [migration runbook](../operations/database-migrations.md).

At the owner's request the two development migrations are combined while retaining the final ID.
Retained previews that applied both original IDs keep their history and data unchanged; the final
migration is already applied. A preview with only the removed structured-line migration requires a
separately planned transition. Never reset history or delete retained data automatically. This design
does not authorize production operations.

## Alternatives and tradeoffs

- Separate ordered and priced quantities can model counts alongside weight, but the owner does not
  use both on a PO. One supplier basis with an explicit total-line alternative removes that complexity.
- Inferring a basis for older references invents historical meaning. Explicit compatibility fields
  retain amounts until the owner resolves them, despite extra transitional complexity.
- Free-text units complicate validation; extend the bounded vocabulary through a reviewed change.
- Client arithmetic would avoid preview latency but duplicate authoritative financial rules.
- Always rounding to two decimals would lose meaningful legacy precision; default to two while
  retaining exact four-place calculations and an explicit precision option.

## Acceptance and verification

1. Save/reload per-unit and total-line prices, incomplete lines, blank and explicit zero amounts,
   all supported units, stable IDs/order and optional metadata. Create no inventory or financial records.
2. Verify exact multiplication, halfway rounding, bounds and unknown/pending/error summaries. Reject
   nonpositive quantities, invalid units/modes, excess precision, overflow, duplicate IDs and unsafe links.
3. Cover old mixed-unit quotes, exact batch normalization, non-terminating division fallback, references
   and unresolved quotes (including coexistence), explicit adoption, replacement and currency clearing.
4. Verify HTTP/SQL agreement, restricted roles, cross-tenant access, antiforgery, request replay after
   edits/deletion, changed-input conflicts, simultaneous saves, stale versions and old-client protection.
5. Verify fresh creation and PO-02 upgrade preserving drafts, tombstones and receipts; test retained
   two-migration history as a no-op, readiness/provisioning, backup markers and destructive-Down refusal.
6. Exercise add/edit/remove/Undo, mode selection, comparison, reload, error focus, stale/failed previews,
   uncertain saves and navigation protection. Inspect desktop/mobile, both themes and enlarged text.
7. Use focused failing tests and GIVEN/WHEN/THEN comments, real SQL integration tests, client/browser
   coverage and scoped meaningful mutations. Report limitations without claiming unavailable checks.
8. Run affected verification and the repository source/release/container gates, refresh the isolated
   preview with `./scripts/dev-up.ps1`, review the complete change and update the ready PR. Keep visual
   evidence outside Git and attach it through the supported CLI.

## Approval

The owner approved the unit vocabulary, fractional quantities, exact four-place calculation, legacy
preservation and server preview, then approved the simpler supplier-based model and V4 compatibility
contract. Cents-style price entry, two-decimal defaults, blank ordinary quantity input, compact lines,
and the lower Add line placement are approved. The owner also requested migration and spec
consolidation. Continue implementation and verification without repeating these design gates.
