---
target: PO-04 UI changes
total_score: 26
max_score: 40
na_heuristics:
p0_count: 0
p1_count: 1
target_identity: "file:C:\\Users\\mcuni\\.codex\\worktrees\\d263\\Workbench\\src\\Workbench.Client\\src\\features\\purchasing\\DraftEditor.tsx"
target_fingerprint: "sha256:1b91faf62717ae30d6175392ecd20e3da148859b39e6908a0afe550e615657d0"
target_path: "C:\\Users\\mcuni\\.codex\\worktrees\\d263\\Workbench\\src\\Workbench.Client\\src\\features\\purchasing\\DraftEditor.tsx"
timestamp: 2026-09-18T02-52-04Z
slug: t-src-features-purchasing-drafteditor-tsx-efe6b605
closed: true
---
Method: dual-agent (A: /root/po04_critique_design · B: /root/po04_critique_evidence)

# PO-04 interface critique

The interface expresses the purchasing domain clearly, but the main reading and review views need stronger information hierarchy. The quiet Tanzanite styling fits Workbench. The largest opportunity is to make purchased items and actual changes immediately visible while keeping complete records available on demand.

Design health: **26/40 — Acceptable; material review-efficiency improvements needed.** This is a heuristic assessment, not a user-study result.

| Heuristic | Score | Main observation |
|---|---:|---|
| System status | 3/4 | Ordered/revision states are clear; review retains a disabled Review amendment action |
| Match with real world | 3/4 | Meaningful purchasing terminology; raw actor IDs and draft wording leak through |
| User control | 3/4 | Keep draft, Keep editing and deliberate discard work |
| Consistency | 3/4 | Established controls; ordered toolbar omits pinned-background behavior |
| Error prevention | 2/4 | Explicit confirmation, but relevant purchase evidence starts hidden |
| Recognition over recall | 2/4 | Complete snapshots force manual difference detection |
| Efficiency | 2/4 | Small amendments produce disproportionately long reviews |
| Minimalist design | 2/4 | Empty metadata competes with actual itemization |
| Error recovery | 3/4 | Source preserves input and retry paths; failures were not induced live |
| Help and guidance | 3/4 | Estimate explanations help; confirmation context can be more direct |
| **Total** | **26/40** | **Acceptable** |

## Specificity and strengths

This feels like Workbench purchasing rather than an unrelated generic dashboard: explicit order dates, preserved supplier snapshots, reasoned amendments and separate supplier/purchase estimates are meaningful domain distinctions. The structural weakness is reusing the same exhaustive comparison component for reading an order, verifying commitment and discovering changes.

- Order identity, supplier, Ordered state, date and revision form a useful record header. Amendment and history are separate actions.
- Estimate and unknown-cost language preserves financial meaning without implying payment or a balance due.
- Existing controls, visible focus and deliberate cancellation provide continuity with the editor. Both assessments used fresh tabs; no purchase records were changed.

The deterministic detector returned zero findings across DraftEditor.tsx, OrderedPurchase.tsx, CommitOrderDialog.tsx, DraftComparison.tsx and DraftList.tsx. It did not flag the runtime hierarchy and scrolling problems below. There were no false positives to discard.

## Priority issues

1. **[P1] Amendment review asks the owner to find the changes manually.** A title-only amendment produced roughly 4,083px of mobile review. In history, the old and new line details were 1,961px apart; the desktop columns were offset by 40px. This is a memory task at the moment a durable change needs verification. Lead with changed fields and paired before/after values; label Added, Removed and Changed in text. Keep corresponding values together on mobile and put complete unchanged snapshots behind Show complete contents. Include relevant old/new totals for monetary changes. Suggested command: `$impeccable shape`. Anchors: DraftEditor.tsx:313; OrderedPurchase.tsx:73; purchasing.css:165.

2. **[P2] The sticky ordered toolbar lets body text show through it.** Both assessments visibly observed text overlap during scrolling. OrderedPurchase uses the transparent base toolbar but never supplies the editor's `.is-pinned` treatment. Share that behavior or give the ordered toolbar an opaque surface, then check scrolling through details and history. This is a new PO-04 regression. Suggested command: `$impeccable polish`. Anchors: OrderedPurchase.tsx:11; purchasing.css:39.

3. **[P2] The actual purchased items are buried beneath empty metadata.** In a one-line order, Order lines began around 1,700px down the page. Repeated supplier information, unset references and blank contact fields appear before Blue sapphires, 3 pieces, USD20. Show compact itemization and charges near the top, followed by totals. Put optional metadata and supplier-contact snapshots in disclosures; retain explicit unknown monetary amounts. This is inherited comparison density newly exposed as the normal ordered view. Suggested command: `$impeccable distill`. Anchors: OrderedPurchase.tsx:18; DraftComparison.tsx:20,30.

4. **[P2] Commitment confirmation hides decision evidence, then becomes too long when expanded.** Initially, supplier, line count and financial summary are hidden behind Review saved contents. Expanding that disclosure pushes Keep draft and Confirm order about 2,300px down the dialog for a single incomplete line. Always show supplier, reference/title, line count, currency, estimates and unknown-cost status; disclose the exhaustive record below. Keep modal actions reachable while scrolling. Clarify that recording the order does not send it to the supplier. Suggested commands: `$impeccable clarify`, then `$impeccable layout`. Anchors: CommitOrderDialog.tsx:47,51.

5. **[P2] Amendment review still contains the entire disabled editor.** After the two snapshots and final actions, the page continues with another form full of inactive controls. The toolbar still shows a disabled Review amendment action. Present review as a distinct task state: hide the editing form while retaining its local data, restore it with Keep editing, and use a review-appropriate toolbar and stable calculation summary. Suggested command: `$impeccable shape`. Anchors: DraftEditor.tsx:313,337,441.

## Cognitive load and emotional journey

The problem is mostly reading and recall, not too many primary buttons. Commitment has two decision actions; ordered detail has three. Review nevertheless exposes nine order metadata fields, six supplier-snapshot fields and around ten line fields per version, often unchanged or empty. Progressive disclosure, changed-information hierarchy and mobile comparison all need improvement.

The flow begins calmly, then loses confidence at review: a small correction produces a large inspection task. The end should answer What changed and why? immediately, with the full record still available.

## Persona red flags

- **Alex, repeat purchasing operator:** A one-field correction requires scanning extensive unchanged content, encouraging skimming.
- **Jordan, first-time owner:** Confirm order starts without an immediately visible supplier or cost summary; draft wording can blur the distinction between a saved order and an unsaved amendment.
- **Sam, keyboard/cognitive-accessibility user:** Sequential full snapshots impose recall burden; duplicated disabled controls add verbosity. No actual screen-reader session was conducted.

## Minor observations

Recorded by followed only by a UUID is not a recognizable person. Preserve the immutable identifier, but provide a useful human-readable label where the contract supports one. None after line discounts and Untitled draft also need contextual wording in ordered views. Mobile pages fit horizontally at390px; the larger issue is vertical length and comparison distance.

## Decisions for the next pass

1. Priority: compact ordered detail, change-focused amendment/history review, or both?
2. Scope: address all five issues, or focus on the top three first?
