---
target: purchase order details
total_score: 26
max_score: 40
na_heuristics: ""
p0_count: 0
p1_count: 1
target_identity: "file:C:\\Users\\mcuni\\.codex\\worktrees\\5816\\Workbench\\src\\Workbench.Client\\src\\features\\purchasing\\DraftEditor.tsx"
target_fingerprint: "sha256:13fff991ceeed23b094fff8c7410cd8f4d6ea2707910d783d687a87bae7a6f57"
target_path: "C:\\Users\\mcuni\\.codex\\worktrees\\5816\\Workbench\\src\\Workbench.Client\\src\\features\\purchasing\\DraftEditor.tsx"
timestamp: 2026-09-17T16-21-54Z
slug: t-src-features-purchasing-drafteditor-tsx-efe6b605
---
Method: dual-agent (A: /root/details_design_review · B: /root/details_detector_review)

The purchase-order details screen explains its arithmetic well, but makes reviewing a saved order feel like filling it out again. The biggest opportunity is a compact saved-order view with editing available on demand.

| Heuristic | Score | Key observation |
|---|---:|---|
| System status | 3/4 | Saved and calculation states are clear; cost is distant. |
| Real-world fit | 3/4 | Purchasing concepts fit; cents entry needs learning. |
| User control | 3/4 | Explicit save, confirmations and recovery controls. |
| Consistency | 3/4 | Saved lines collapse; saved charges remain expanded. |
| Error prevention | 3/4 | Currency and correction safeguards are explicit. |
| Recognition | 2/4 | Reviewing lines and totals requires scrolling and recall. |
| Efficiency | 2/4 | No compact charge-review path. |
| Minimalist design | 2/4 | Repeated editing controls dominate the page. |
| Error recovery | 3/4 | Recovery paths exist in source; not induced live. |
| Help | 2/4 | Repeated field tutorials outweigh concise task guidance. |
| Total | 26/40 | Acceptable; substantial efficiency opportunities. |

Design specificity: The supplier/third-party split, price bases, scoped discounts and honest draft estimates are grounded in purchasing. The generic long-form structure weakens that identity. The deterministic source scan returned zero findings, no rule locations or false positives; it does not contradict the usability findings. No reliable user-visible overlay was available.

What works:
- The summary makes USD 306.60 supplier cost versus USD 309.60 total purchase cost auditable.
- Sticky save status and explicit draft wording provide reassurance.
- Supplier/line disclosures and stacked mobile fields retain useful context.

Priority issues:

1. [P1] Saved charges bury the purchase estimate. The two-line, three-charge example takes roughly four desktop page scrolls to reach the total/footer. Every saved charge exposes its full edit form, while saved lines collapse. Reopening an order to understand its cost becomes a form-reading task. Give saved charges expandable summaries showing label, payee, amount and estimated/confirmed status. Put a compact estimate or clear jump to the breakdown near order identity/lines; preserve separate supplier and purchase totals and unknown/pending states. Suggested commands: $impeccable distill, $impeccable layout.

2. [P2] Monetary fields repeatedly teach an unusual entry convention. Every discount/charge repeats cents-entry instructions, precision controls and the 1234-to-12.34 example. Source also shows cursor navigation intercepted in cents mode; that behavior was not exercised live in this critique. Move detailed teaching to focus/contextual help and keep precision available as an advanced choice. Preserve existing entry semantics unless a separate behavior change is explicitly approved. Suggested commands: $impeccable clarify, $impeccable distill.

3. [P2] Saved source URLs lack a direct open action. Order and line source references render as editable inputs without an Open source action. This is source-backed: the live sample had empty order references. Add an explicit action for valid supported URLs while preserving editability; leave invoice identifiers and arbitrary reference text as text. Suggested commands: $impeccable harden, $impeccable polish.

4. [P2] Discount removal uses inconsistent action styling. User-reported after the independent assessments and confirmed in source: DiscountFields.tsx uses className="quiet" for both Remove order discount and Remove line discount, while Remove charge and Remove line use "quiet danger". Apply the existing red danger treatment to the shared discount-removal button. Suggested command: $impeccable polish. This supplementary finding does not imply a new heuristic rescore.

Cognitive load: Three weaknesses—charge chunking, distance between lines and total, and missing disclosure for saved charges—produce moderate load. The grouped 11-category selector and 15-unit vocabulary are not inherently defects; do not remove useful domain choices simply to reduce counts.

Emotional journey: Saved state reassures on entry; repeated editors create a long middle; the clear arithmetic is the payoff but arrives late.

Persona red flags:
- First-time buyer: must learn cents entry and distinguish source-confirmed amounts from an uncommitted draft.
- Keyboard user: repeated charge controls lengthen navigation; custom monetary caret behavior deserves a focused audit.
- Mobile reviewer: title/supplier/currency maintenance dominates the first viewport, with totals far below.

Minor observations: Clear supplier and Clear all amounts are prominent during review; put maintenance guidance where a user initiates changes. Keep a concise PO identifier in the scrolled toolbar. Validate terms such as Eligible base with buyers.

Evidence limits: Independent live dark desktop inspections; Assessment A also inspected 390×844 mobile. Both used fresh authenticated in-app tabs after separate Edge tabs reached sign-in. No persisted data changed. Light appearance, screen-reader output, measured contrast, full keyboard flow, populated references and induced failure states were not tested in this critique.

Questions to consider: Should saved orders prioritize reviewing costs or editing fields? Should monetary input keep its current convention with quieter help, or receive a separately scoped behavior review?
