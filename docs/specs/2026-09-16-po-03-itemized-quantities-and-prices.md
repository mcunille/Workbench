# PO-03: itemized quantities and prices

**Status:** Approved for implementation on 2026-09-16. Interface design uses impeccable within the existing Tanzanite surface.

## Problem, scope and baseline

Implement PO-03 from the [purchasing scenario](2026-09-11-purchase-orders-and-purchase-finances.md).
The business owner must distinguish what they intend to purchase from how the supplier prices it:
ten stones, ten carats and one parcel are different quantities. Preserve incomplete, resumable drafts.

Baseline inspected: `77bf770f0a2840d633fb41065673c63c67beb7eb`. PO-01/PO-02 currently store ordered
JSON entries with UUID, description, notes, source link and nullable reference price. Both HTTP and
restricted SQL commands validate a closed representation. Reference prices have no stated basis.
Existing versioned receipts, supplier snapshots, permanent references and concurrency recovery remain.

This increment adds structured draft lines and explainable merchandise estimates. It does not commit
orders, create inventory or financial obligations, allocate costs, or implement discounts, charges,
invoices, payments, shipments, receipts or amendments. Those remain separate stories. No claim of
accounting precision or a confirmed amount due follows from a draft estimate.

## Proposed line contract

Retain line UUIDs and array order. All optional fields are required nullable properties in the wire
representation; omission and unknown fields are rejected. Empty drafts and incomplete lines remain
saveable. Invalid supplied values are rejected, rather than silently rounded or discarded.

| Field | Meaning and validation |
| --- | --- |
| `id` | Existing nonempty UUID, unique within the draft; stable across edits and reloads. |
| `description` | Existing optional text, maximum 500 UTF-16 code units. |
| `quantity` | Ordered quantity; positive decimal string, up to 9 integer digits and 4 decimal places. Null means unknown. |
| `unitOfMeasure` | Null or `piece`, `carat`, `gram`, `kilogram`, `ounce`, `troyOunce`, `millimeter`, `centimeter`, `meter`, `parcel`, `pair`, `set`, `pack`, `box`, `lot`. Ounce means avoirdupois, separate from troy ounce. |
| `unitPrice` | Nonnegative decimal string, retaining existing maximum 15 integer digits and 4 decimal places. Zero is explicit and valid; null means unknown. |
| `pricingUnit` | Null or a unit from the same vocabulary; explicit even when equal to the ordered unit. |
| `pricePerQuantity` | Positive decimal string with the quantity bounds; price applies per this many pricing units, e.g. 100 pieces. |
| `pricingQuantity` | Total quantity to price when the pricing unit differs from the ordered unit. Same bounds as quantity. Null is required when units match; never inferred from item count, parcel count or notes. |
| `supplierSku` | Optional trimmed text, maximum 200 code units; not an inventory identity. |
| `itemType` | Optional trimmed free text, maximum 100 code units; suggestions Gemstone, Finding, Material, Supply, Jewelry, Other. Not a collection classification or foreign key. |
| `notes`, `sourceLink` | Existing limits and safe HTTP/HTTPS validation retained. |
| `indicativePrice` | Legacy reference amount only; retained for older data without inferring a pricing basis. Same amount validation as today. Cannot coexist with `unitPrice`. |

Quantities permit fractions even for count and packaging units: fractional lots, pairs or packs may
be commercially meaningful. A unit is a label for the owner's stated measure, not an inventory count
constraint. No automatic conversion occurs between any units, including grams and carats. A differing
pricing unit always requires an explicit pricing quantity before an amount can be calculated.
Custom unit definitions and conversion tables are deferred; unsupported units require a reviewed
extension to the vocabulary, not misuse of a supported label.

Partial combinations are saveable: quantity without a unit or price without a complete basis remains
incomplete. Currency is required whenever either price field is present. `pricingQuantity` requires
two known, different units, preventing hidden contradictory quantities. Unit changes that invalidate
an existing pricing quantity require the owner to clear it; the UI must not silently remove it.

New UI lines start with null quantity, unit, price and basis, leaving the owner to choose the unit.
The approved critique follow-up leaves quantity, pricing quantity, pricing unit and denominator
blank until supplied explicitly; choosing an ordered unit does not infer a pricing basis.
Server normalization never invents defaults. Description remains optional until PO-04 defines
commitment validity.

## Arithmetic and unknown values

Use decimal strings on the wire and exact decimal/rational arithmetic, never binary floating point
for business calculations. Canonicalize supplied valid numeric strings to four decimal places for
storage and V3 fingerprinting. Reject signs, exponent notation, separators, whitespace and excess
precision; UI entry may normalize a pasted ordinary decimal before submission.

The effective pricing quantity is `quantity` when the units match, otherwise `pricingQuantity`.
Line gross = effective pricing quantity × unit price ÷ price-per quantity. Both the ordered quantity
and its unit must be known even when a separate pricing quantity is present. Missing inputs yield
null gross, including a zero price with an incomplete basis. Description is not an arithmetic input.

Round each calculated line gross once to four decimal places, midpoint away from zero. Sum rounded
line amounts to produce the merchandise estimate, without a second rounding step. Return/display up
to four decimal places and at least two; do not assume that all three-letter currency codes have two
minor units. This is an explicit draft estimation policy; invoice/tax settlement rounding is deferred.
Use sufficient intermediate precision (scaled integers/rational arithmetic is suitable) to avoid
overflow or pre-rounding at the supported bounds. Bound individual rounded gross to at most 19
integer digits plus four decimal places, and reject an otherwise complete line exceeding it with
an error on that line. A 100-line sum consequently requires up to 21 integer digits.

The server owns calculation and returns read-only per-line gross, incomplete line count and an
estimate summary outside the writable draft. For live edits, use an authenticated, antiforgery-protected
V3 calculation endpoint accepting the same draft and validation rules without a write or receipt.
Debounce requests, cancel obsolete work and ignore stale responses. Pending/failed calculation is
explicit; never display an earlier result as if it described the current input. Saving does not
depend on a successful preview request. Do not duplicate arithmetic as a client authority.

Display a complete sum as **Merchandise estimate**, with “Before discounts, shipping and tax.” If any
line is incomplete, show **Known line subtotal** and “N lines need quantity or pricing details,” not
an order total. With no priced complete lines show Unknown, not zero. An empty draft has no estimate.
A complete, explicitly free line contributes zero. Never sum quantities across units or orders.

| Entered example | Formula shown | Draft line gross |
| --- | --- | ---: |
| 10 pieces, USD 20 per 1 piece | 10 pieces × 20 / 1 piece | USD 200.00 |
| 10 carats, USD 20 per 1 carat | 10 carats × 20 / 1 carat | USD 200.00 |
| 10 pieces, priced quantity 12.5 carats, USD 20 per 1 carat | 12.5 carats × 20 / 1 carat; ordered 10 pieces | USD 250.00 |
| 1 parcel, USD 300 per 1 parcel | 1 parcel × 300 / 1 parcel | USD 300.00 |
| 250 pieces, USD 8 per 100 pieces | 250 pieces × 8 / 100 pieces | USD 20.00 |
| 1 lot, USD 1 per 3 lots | 1 lot × 1 / 3 lots | USD 0.3333 |
| 1 piece, USD 0.0001 per 2 pieces | 1 piece × 0.0001 / 2 pieces | USD 0.0001 |

## Editor interaction

Operate mode, extending the incumbent Tanzanite editor, system typography and field components.
Keep order details, lines, notes/sources and the explicit Save draft toolbar in their current order.
Rename Shopping list to Order lines; no new workspace or navigation destination is needed.

Each numbered line presents description first, then Quantity + Unit, then Unit price + Per quantity
+ Pricing unit, followed by its formula and estimate. When the units differ, reveal Total quantity
priced with the chosen pricing unit beside the field. Explain “Enter the total weight or quantity
used for this price; it is separate from the ordered quantity.” Keep prices in the existing monetary
entry component with explicit extra-precision mode; quantity and denominator use direct decimal entry.

Supplier SKU, item type, notes and link share a Line details disclosure with a short populated-value
summary, automatically opened when invalid. On narrow screens, stack the same DOM order rather than squeezing a wide table.
Preserve accessible per-line labels, inline errors plus linked error summary, focus after add/remove,
Undo removal, saved/local comparison, unknown-outcome retries and unsaved navigation protection.
Comparison includes every new field, the legacy amount and explicit units, not just the computed gross.

Legacy prices appear as **Reference price — basis not recorded**. **Use as unit price** copies the
amount and clears the reference field only in local state, leaving the basis for the owner to enter
or confirm before save. Clearing/removing fields is recoverable under the existing unsaved-state
rules. Clear all prices clears both price fields, retains quantities/bases, names its affected count,
and keeps the existing confirmation and Undo-history boundary. Currency changes require saved prices
to be cleared first; no reinterpretation or conversion of amounts is permitted.

## API, persistence and compatibility

Introduce `/api/v3/purchase-order-drafts` list/create/detail/update/delete routes, following V2
envelopes, authority, pagination/search, saved version and compact receipt semantics. Add
`POST /api/v3/purchase-order-drafts/calculate` for the nonpersisting preview, with `{draft}` input
and the same calculated summary returned in V3 detail. V3 lists retain V2 summary fields; this story
does not add list totals or line search. Define and generate precise OpenAPI DTOs and field errors.
Validation remains HTTP 400 `draft_validation_failed`; concurrency and replay conflicts retain the
existing codes. Missing/foreign draft identities are indistinguishable. Calculation errors retain
input and point to fields using existing `draft.entries[index].field` paths.

Continue the bounded JSON aggregate and atomic draft replacement; normalize line tables only when
later line-level events require that design. Content schema V2 adds the fields above. Existing
schema V1 content is projected to V3 with new fields null and the existing indicative amount intact.
On first V3 save, write schema V2 without changing UUIDs or inferring quantities, units or prices.
Keep schema V1 and V2 readers; no bulk content rewrite is required. Read-only calculation results
are derived, never accepted as writes or stored as independently mutable amounts.

Add fingerprint version 3, covering all editable content. Preserve V1/V2 canonicalizers and receipt
bytes. Old create/update routes become replay-only: exact successful old requests still resolve,
changed-input reuse conflicts, and requests without a matching receipt return HTTP 426
`draft_contract_reload_required` without mutation. Legacy reads retain their DTO shapes; after V3
conversion, expose only retained `indicativePrice`, never project `unitPrice` as a basisless reference
price. Old delete contracts stay valid with version checks, clearing all structured content.

One forward migration extends content/fingerprint constraints and adds restricted V3 commands,
legacy replay-only handling and compatible deletion. SQL validates the closed canonical representation,
numeric bounds, units, inter-field invariants, size limits and immutable identity/retry conditions.
Server calculation validates gross bounds before mutation; restricted SQL writes must enforce the
same gross bound so a direct command cannot persist a draft the application cannot calculate.
Reuse tenant RLS, tenant-qualified relationships, no direct runtime UPDATE/DELETE, request locks,
rowversions and atomic receipt insertion. Preserve supplier selection/archive locking and counters.
Deletion retains the permanent PO number and receipts and clears both supported content schemas.

Retain the 100-line, 20-source-link, existing request/body and 1 MiB UTF-16 content limits. Bounds
apply equally to calculation, persistence and canonicalization. Preview never fetches links, logs
draft bodies or creates suppliers, drafts or receipts; responses are private/no-store.

Advance readiness, permission provisioning and backup compatibility markers together. Drain older
writers before deployment. Verify fresh creation and upgrade from the current PO-02 schema with
existing active drafts, tombstones and both receipt versions. Do not rewrite prior migrations or
retained preview databases. Block destructive Down; use forward correction or guarded backup recovery.
This design does not authorize production operations or deletion of retained data.

## Alternatives and tradeoffs

- Restricting all prices to the ordered unit is smaller but cannot explain stones priced by carat.
  Explicit pricing quantities support that core case without inferring weight or building conversions.
- Treating old amounts as unit prices is convenient but invents historical meaning. Retaining a
  transitional reference amount adds UI/API complexity while preserving the owner's original data.
- Free-text units accept any label but make meaningful equality and validation unreliable. A bounded
  vocabulary covers the proposed scope; adding units remains an explicit compatible design decision.
- Client arithmetic is faster without a network request, but duplicates financial rules. A shared
  server calculation path keeps one authority; preview latency/failure must be visible and recoverable.
- Two-decimal rounding is familiar but loses existing four-place reference precision and assumes
  currency policy. Four-place draft estimates preserve detail until a separate invoice design.

## Acceptance and verification

1. Save/reopen incomplete lines and every unit/basis example above, preserving IDs, order, optional
   metadata and null versus explicit zero. Never create inventory, acquisition or financial records.
2. Unknown quantity, price or basis never creates a misleading complete total. Mixed units remain
   distinct; mismatched units require explicit pricing quantity, including ounce versus troy ounce.
3. Reject zero/negative quantity and denominator, invalid units, excess precision, overflow, duplicate
   IDs, invalid links and oversize content. Test exact halfway rounding, repeating division and bounds.
4. Verify server/SQL agreement, restricted-principal checks and cross-tenant list/read/write/delete;
   calculation is authenticated and antiforgery-protected. New input cannot bypass command validation.
5. Upgrade retains legacy amounts without assigning meaning, preserves old receipts, blocks unmatched
   old writes, and resolves retries before and after subsequent edits or deletion. Test changed-input
   reuse and simultaneous saves, unchanged supplier behavior and currency-clear safeguards.
6. Exercise browser add/edit/remove/Undo, legacy conversion, mixed-unit entry, reload, error retention,
   stale and failed preview responses, conflicting edits and uncertain save recovery. Inspect desktop
   and mobile in both appearances, keyboard operation and enlarged text from current source.
7. Use focused failing tests before behavior changes with GIVEN/WHEN/THEN comments, then real SQL
   integration/migration tests, client helper/component/API tests and browser tests. Run affected
   mutation testing and report justified exclusions or unavailable tooling accurately.
8. Run `./scripts/verify.ps1` and `./scripts/smoke-container.ps1`, refresh the isolated preview through
   `./scripts/dev-up.ps1`, inspect it and share its reported URL. Review the complete change, update
   purchasing/architecture/migration documentation, commit and open a ready-for-review PR. Capture
   representative nonsensitive visual evidence outside Git and attach using the supported CLI.

## Approval boundary

The owner approved the unit vocabulary and fractional quantities, separate pricing quantity,
four-place rounding, preservation of legacy reference amounts, server-calculated preview and V3
compatibility strategy. These are new durable contracts beyond the scenario's unapproved story.
Proceed through implementation and verification without repeating this design gate.

## Approved critique follow-up (2026-09-16)

Saved lines reopen as compact disclosures with description, ordered quantity and current estimate.
New or restored lines open for editing; validation reveals the affected line and optional fields.
Metadata is summarized rather than automatically expanded solely because it is populated. Add line
is available at both ends of the list. Concise saved/unsaved/pending/uncertain state stays beside Save.

The owner explicitly retained cents-style price typing and two-decimal defaults. Pasted monetary
amounts preserve their magnitude, including whole numbers. Meaningful third/fourth digits enable
full precision without rounding. Quantity fields use normal input, start blank, and omit insignificant
trailing zeros when displayed after loading; this does not change the API's canonical four-place format.
