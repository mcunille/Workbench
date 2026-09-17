---
target: PO-05 purchasing editor
total_score: 30
max_score: 40
na_heuristics: ""
p0_count: 0
p1_count: 0
target_identity: "file:C:\\Users\\mcuni\\.codex\\worktrees\\5816\\Workbench\\src\\Workbench.Client\\src\\features\\purchasing\\DraftEditor.tsx"
target_fingerprint: "sha256:13fff991ceeed23b094fff8c7410cd8f4d6ea2707910d783d687a87bae7a6f57"
target_path: "C:\\Users\\mcuni\\.codex\\worktrees\\5816\\Workbench\\src\\Workbench.Client\\src\\features\\purchasing\\DraftEditor.tsx"
timestamp: 2026-09-17T16-01-33Z
slug: t-src-features-purchasing-drafteditor-tsx-efe6b605
---
# PO-05 — independent Assessment A

Method: independent design review by /root/po05_critique_a. No detector output or other assessment findings were read. Scope: DraftEditor.tsx, DiscountFields.tsx, DraftCharges.tsx, DraftFinancialSummary.tsx; approved PO-05 specification and Tanzanite direction.

## Design specificity verdict

The extension is authored for the purchasing task. Its defining feature is inspectable arithmetic from merchandise gross through two discount scopes to supplier and whole-purchase estimates. Named payees, confirmed source amounts, and explicit draft wording give this form domain meaning. Neutral system typography and simple form controls are appropriate to the incumbent Tanzanite Operate surface; decorative novelty would add little. The stacked mobile form remains coherent, though repeated amount-entry guidance makes reconciliation lengthy.

## Heuristic scores

| # | Heuristic | Score | Evidence / limit |
|---|---|---|---|
| 1 | Visibility of system status | 3 | Saved state, last-saved timestamp, pending estimate, and final estimate are explicit. Live navigation showed pending then resolved values. All asynchronous failure states were not personally exercised. |
| 2 | Match system / real world | 3 | Shipping, taxes, supplier and bank costs follow source-document concepts. Cents entry requires learning, mitigated by an example. |
| 3 | User control and freedom | 3 | Explicit save, remove discount, charge-removal Undo and clear-amount confirmation exist. Undo is intentionally bounded by save/currency changes. |
| 4 | Consistency and standards | 3 | Existing floating fields, disclosures and Tanzanite appearance remain consistent across the extension. Context-independent monetary helper copy is the main inconsistency. |
| 5 | Error prevention | 3 | Locked currency and confirmed-correction guidance prevent accidental reinterpretation; blank-discount guidance contradicts validation. |
| 6 | Recognition rather than recall | 3 | Eligible bases, reductions, collapsed line discount indication, payee and status are visible. Mobile requires scrolling from source charge to aggregate. |
| 7 | Flexibility and efficiency | 3 | Optional adjustments and hidden source details keep initial work small; full precision supports advanced amounts. Repeated large charge forms make long lists slower to review. |
| 8 | Aesthetic and minimalist design | 3 | Quiet separators and aligned monetary values establish clear hierarchy. Repeated precision instructions create excess vertical density. |
| 9 | Error recovery | 3 | Source exposes inline errors and retry; supplied walkthrough verifies preservation of edits and correction explanation. Concurrent-save and failed-preview recovery not personally exercised. |
| 10 | Help and documentation | 3 | Contextual base, third-party and draft-only explanations are well placed. Amount helper has one incorrect instruction for constrained fields. |
| | Total | **30/40 — Good** | All ten apply to this Operate surface. No n/a heuristics. |

## Strengths

1. The worked example is explainable without mental arithmetic: the screenshots show USD 300.00 gross, minus USD 20.00 line discounts, USD 280.00 merchandise net, minus USD 10.00 order discount, plus USD 36.60 supplier charges, USD 306.60 supplier estimate, and USD 309.60 purchase estimate after the bank fee. The strongest final amount and subtotal separator make the difference legible.
2. Charge payee and amount status stay visible while source details are disclosed on demand. The third-party note explains exactly why the bank fee changes one aggregate but not the other.
3. All four supplied views retain the same reading order. Mobile labels wrap, fields stack, and the summary keeps labels paired with monetary values. The explicit Save draft toolbar provides a stable endpoint without introducing another sticky summary.

## Priority issue

**[P2] Monetary helper tells users to leave required discount values blank.** The fixed order-discount helper in all supplied screenshots says “Leave blank if unknown.” DiscountFields.tsx:22 uses ReferencePriceField without a context-specific helper; ReferencePriceField.tsx:75 always includes that instruction in cents mode. The approved specification requires a supplied discount to have a value; an unknown base is a separate concept. The same generic helper appears on charges currently marked Confirmed from source (DraftCharges.tsx:47,51), although confirmed charges require amounts. A first-time user can follow the field's own instruction into an avoidable validation error.

Fix: make unknown-value guidance context-aware. For discounts, say to enter a value or remove the discount; for confirmed charges, require the source amount or explain that the status must change to Estimated before the amount can be unknown. Keep blank-is-unknown guidance for estimated charges and optional prices. Suggested command: $impeccable clarify. This is one shared-copy issue, not multiple findings. No P0/P1 issue is supported by this assessment.

## Cognitive load

Checklist: single focus passes; grouping passes; hierarchy passes; one decision at a time passes through conventional form progression; working-memory support passes through visible arithmetic; progressive disclosure passes for supplier, lines and sources. Chunking and minimal choices have qualifications: each charge has five or six visible fields plus precision and removal controls; the opened category selector offers eleven categories. Its four optgroups (Delivery, Taxes and duties, Fees and services, Other), each with no more than four choices, appropriately organize this approved domain breadth. Treat this as moderate long-form load, not a reason to remove needed categories.

Repeated cents-entry guidance, precision controls, category and label fields consume much of the mobile page before the estimate. This is a minor efficiency opportunity, not a release-blocking failure in the three-charge example. Preserve visible payee/status and the in-flow summary; do not add a competing sticky panel. At larger charge counts, a future compact review presentation could be investigated with actual use evidence.

## Persona red flags

- First-time hobbyist/collector: the incorrect blank-value instruction is the main concrete trap. “Eligible base” is explained by the adjacent merchandise scope sentence; it does not require additional accounting setup.
- Business owner reconciling a supplier document: supplier versus bank costs and the final arithmetic are strong. Repeated charge editing furniture slows scanning of a long source document, especially on mobile.
- Low-vision / enlarged-text user: the supplied script reports no horizontal overflow at 320px and 200% root font size. This is useful evidence but does not by itself establish every control's usability at that size. The four screenshots show normal-size layouts; no independent screen-reader session or measured contrast audit was performed here.

## Emotional journey and minor observations

The opening Saved state and draft badge reduce uncertainty. Source-confirmed charges can feel authoritative, but the summary's draft-only sentence and supplier/purchase labels appropriately prevent a false sense of payment obligation. The long mobile charge section is the emotional valley; the clear final total provides a satisfying endpoint. Save remains explicit.

The long Payment / bank / currency-conversion category and matching editable label clip in narrow mobile controls. Full text is available through the native selector or field interaction; this is a minor scanability tradeoff rather than demonstrated data loss. “Charge 1/2/3” legends orient repeated fields, although the labels carry more business meaning.

## Evidence and limits

- Inspected all four full-page PNGs: po05-light-1440.png, po05-dark-1440.png, po05-light-390.png, po05-dark-390.png under C:/Users/mcuni/.codex/visualizations/2026/09/17/01a0acc6-52ce-7050-9226-1deb37c2e71f/po05/. Normal image display resized the tall screenshots; live desktop inspection supplemented them.
- Opened a fresh background native browser tab at http://localhost:32775/purchase-orders and privately authenticated. Opened PO-000002, observed pending-to-resolved calculation, inspected charge forms and final summary. No financial inputs or saved draft state changed. Live draft reflects the walkthrough's later shipping correction: USD 307.60 supplier and USD 310.60 total, while screenshots correctly show the earlier worked example.
- Read artifacts/po05/preview-qa-final.log and preview-qa.ts. Persist/reload, mobile/desktop both appearances, 200% text overflow, and confirmed-correction evidence are attributed to that existing walkthrough, not claimed as independently re-executed.
- No detector run, overlay, contrast measurement, keyboard traversal, maximum-charge stress test, live unknown-amount, failure or concurrent-save scenario was performed in Assessment A. Their absence limits confidence, not evidence of a failure.
- Read PRODUCT.md, DESIGN.md, craft-floor, critique Assessment A/report/scoring guidance, approved specification and surface contract. Ignore file was absent.

Questions skipped: this is the delegated independent assessment of an already approved implementation; the parent owns synthesis and follow-up. Future design inquiry: what is the smallest charge review representation that preserves label, amount, payee and status for a busy owner?


## Independent detector assessment

The independent scan reported zero findings. No overlay was available. See the limitations above; a clean pattern scan does not establish accessibility.

## Resolution

The amount hints now distinguish required discounts and confirmed source amounts from estimated unknown amounts. The finish reviewer scored this and the PRODUCT.md truth correction resolved. Original heuristic scores are retained without claiming a rescore.
