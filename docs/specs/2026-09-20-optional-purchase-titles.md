# Optional purchase titles

Issue [#135](https://github.com/mcunille/Workbench/issues/135) asks for purchases to
start with suppliers and items, without requiring an invented name.

## Design

Keep supplier details and order lines first in the existing editor. Move the title
to the notes section, label it **Custom title (optional)**, and explain that it is
a personal label rather than a required purchase identifier. Keep it visible there
so existing titles remain easy to edit and validation retains its existing behavior.

Lists use the existing custom title, otherwise supplier name, otherwise the first
nonblank item description in saved line order. A completely empty draft displays
**Empty draft**; an ordered record with no context displays **Purchase order**.
Always retain the PO reference to distinguish purchases from the same supplier.
Deletion and commitment confirmations use the same label plus the PO reference.
Comparison views continue showing supplier and item contents and render an absent
optional title as **None**, not as an incomplete purchase name.

Add nullable `firstItemDescription` to both list response schemas. This is derived
from existing saved content, including supported legacy versions. It avoids a
detail request per row and gives item-only drafts an identifier. Existing clients
can ignore the additive field. Search semantics are unchanged: reference, supplier
and custom title remain searchable; item-description search is outside this change.

Generated labels are display-only. No database migration, title backfill, automatic
title writes, purchase transition change, or amendment/history rewrite is needed.
Existing tenant filtering and pagination stay authoritative. Reverting the UI and
additive response field requires no data rollback.

## Acceptance and verification

- Supplier-only, items-only, combined and empty drafts save with a null title.
- Supplier and order lines precede the explained optional title field.
- Lists distinguish same-supplier purchases by reference and retain custom titles.
- Delete confirmation identifies the saved record even if local edits differ.
- HTTP tests cover both list routes, empty content and first described item selection.
- Component and browser tests cover creation, reload, labels and dialog identity.
- Run the full verification and container smoke gates and inspect the current-source
  isolated preview at desktop and mobile widths.
