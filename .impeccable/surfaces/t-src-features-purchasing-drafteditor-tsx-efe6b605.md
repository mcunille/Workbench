---
version: 1
slug: "t-src-features-purchasing-drafteditor-tsx-efe6b605"
primary_target: "src/Workbench.Client/src/features/purchasing/DraftEditor.tsx"
related_targets: ["src/Workbench.Client/src/features/purchasing/DraftComparison.tsx"]
---

# Purchase order line editor
Mode: Operate. Existing surface extension, approved in PO-03 on 2026-09-16.
Audience: business owner transcribing supplier quantities and prices into a resumable draft.
Scope: DraftEditor line fields, comparison, currency clearing and live calculation states.
Constraints: preserve explicit saves, conflict/retry recovery, mobile/keyboard access and Tanzanite.
## Direction contract
THESIS: Record the supplier basis once, then choose per-unit or total-line pricing with an inspectable amount. Avoid a compressed spreadsheet that hides units or requires horizontal scrolling.
OWN-WORLD: Inherit DESIGN.md neutral Tanzanite/Quartz surfaces, system typography, existing floating fields and quiet secondary actions; both appearances remain supported.
STORY: Describe the goods; state the supplier quantity and unit; choose Per unit or Total line and enter one price; inspect the estimate and explicitly save. Unknown values stay visible.
FIRST VIEWPORT: Keep Back/Save and concise save state together in the sticky toolbar. Saved lines reopen as compact descriptions, ordered quantities and estimates; expand to edit a full-width description, quantity/unit row and pricing row. New and restored lines open for editing. Keep Add line above and below the list. Optional metadata stays collapsed with a short summary unless invalid. Stack editing rows on narrow screens.
FORM: Local extension of the existing editor, approved spec composition; no concept seed is required for a local extension. Inline formula is the signature interaction, updated only for the current server-calculated input.
FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance
No shipping rasters are needed. DESIGN.md remains incumbent authority. Test pending/failed/stale calculation, legacy price conversion, full metadata comparison, responsive and focus behavior.

Approved critique follow-up: retain cents-style price typing, make the mode explicit, and preserve
whole pasted amounts. Default price display is two decimals; significant third/fourth digits remain
available without rounding. Quantities start blank and accept ordinary
decimal input. Trim insignificant quantity zeros in summaries and reopened fields. Collapsing a line
preserves local input and never substitutes for Save.

## Finish verdict
2026-09-16: independent finish review cleared the scoped interface for shipping after inspecting
desktop and mobile in both appearances and 320px-wide enlarged text. Action labels wrap between
whole words without horizontal overflow. The documentation handoff confirmed that this extension
adds no design-system rule requiring a change to DESIGN.md. Verification screenshots remain outside Git.

Approved critique follow-up: cents-style typing remains explicit, paste preserves monetary amounts, saved lines are compact disclosures, and save feedback stays beside the action. Desktop/mobile and light/dark browser checks passed, including keyboard expansion and precision persistence. Independent finish review found partial-quantity unit visibility and accessible-name issues; both received regression coverage and corrections. Existing design tokens and patterns remain appropriate.

Supplier-based pricing approved: remove separate pricing unit, denominator and priced quantity from ordinary lines. Use native radio choices with visible labels Per unit / Total line, one price input, stable price guidance and explicit legacy quote resolution. Preserve read-only unresolved quote details; do not infer a reference amount's meaning. Independent finish review cleared the supplier-based interface and documentation after desktop/mobile, both-theme and enlarged-text browser verification. No new design-system rule or shipping raster asset was introduced.
