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
THESIS: Make ordered quantity and pricing basis separately legible, with an inspectable formula. Avoid a compressed spreadsheet that hides units or requires horizontal scrolling.
OWN-WORLD: Inherit DESIGN.md neutral Tanzanite/Quartz surfaces, system typography, existing floating fields and quiet secondary actions; both appearances remain supported.
STORY: Describe the goods; state quantity and unit; enter price per chosen quantity/unit; supply a separate total weight when needed; inspect the estimate and explicitly save. Unknown values stay visible.
FIRST VIEWPORT: Keep the sticky Back/Save toolbar and existing order identity/details. In each line use a full-width description, a two-column quantity/unit row, then a three-column pricing row. Put the formula immediately below; optional metadata opens inline. Stack rows in the same reading order on narrow screens.
FORM: Local extension of the existing editor, approved spec composition; no concept seed is required for a local extension. Inline formula is the signature interaction, updated only for the current server-calculated input.
FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance
No shipping rasters are needed. DESIGN.md remains incumbent authority. Test pending/failed/stale calculation, legacy price conversion, full metadata comparison, responsive and focus behavior.
