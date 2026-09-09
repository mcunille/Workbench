# Refined responsive navigation

## Decision and scope

The accepted direction is a collapsible glass pane on desktop and a floating bottom pill
on mobile, refined through local preview feedback. The original proposal of an opaque
sidebar and wrapping mobile top bar is superseded by this decision. The living
[style reference](../../DESIGN.md#authenticated-navigation) guides future changes.

The previous sidebar consumed space and scattered account controls. The desktop pane
keeps destinations legible while permitting an icon rail; the mobile pill preserves
content height and puts the existing destinations within reach. This change is navigation
presentation only: authentication, authorization, routes, API contracts, collection search,
and unsaved-change confirmation remain intact. No database migration is needed.

## Desktop and material

- Above 48rem, start expanded at 13rem; collapse to 4.5rem. The choice is in-memory and
  retained across navigation within the authenticated shell, not a persisted preference.
- Glass shares the page atmosphere, with faint Tanzanite tint, neutral fill, reflection,
  blur, and soft shadow. It is flush top/left/bottom, square on the left, with 10px right
  corners and a right border only. Avoid a flat divider or saturated crystal treatment.
- Align the wordmark and page heading on the same top row. Keep the gap to the first
  destination compact. Collapse hides the wordmark without moving the toggle to another row.
- Fixed icon columns balance rail padding. The toggle follows the pane edge smoothly;
  no alignment switch or icon bounce. Pane and label widths transition together over
  220ms; label opacity stays within that interval. Reduced motion removes transitions.
- Preserve accessible names, current-page markers, visible keyboard focus, and collapsed
  destination labels on hover/focus. Inventory is primary; Administration remains near
  the bottom and is visible only with TenantUsersManage.

## Shared profile

Show tenant at the top of the disclosure, then email; do not repeat tenant in the rail.
Rows use leading icon, label, flexible space, and trailing navigation cue or value.
Account has a cue. Appearance cycles Dark → Light → Auto directly, with its current value
visible; it has no child submenu. Auto follows system changes, including while closed.
Sign out and release version stay here. The trigger remains an accessible disclosure.
Escape closes it and returns focus; outside interaction closes it. Opening overlays the
content instead of changing the page layout. Storage failure must not prevent appearance
changes for the current session.

## Mobile

At 48rem and below, use the same Inventory, permission-gated Administration, and User
controls in a centered glass bottom pill. Only the active destination label is visible;
all names remain accessible, with at least 44px targets. No search element or desktop
collapse control belongs in the pill. Desktop collapse state does not hide mobile controls.

The profile opens upward with viewport-bounded width/height and internal scrolling.
Content bottom padding derives from pill height + bottom offset + safe-area inset + 1rem
clearance. The height includes control height, both padding edges, and both borders.
The same variables position the pill and reserve clearance, so the last item and page
controls can always scroll above it. Avoid unrelated fixed padding that can drift.

## Alternatives and tradeoffs

The fancy morphing crystal dock with expanding search is preserved as a
[standalone design idea](../design-ideas/README.md), not another supported desktop mode.
The mobile pill borrows its compact shape without moving collection search into chrome.
A wrapping mobile top bar pushed content down, especially with profile open. A full inset
pane, strong purple tint, and visible top/left/bottom borders were rejected in preview.
Instant desktop collapse was tried; synchronized motion with stable icon columns was the
final preference. Production keyboard focus remains visible regardless of prototype styling.

## Acceptance and verification

Exercise collapse/expand, permission visibility, accessible labels, profile dismissal and
focus restoration, appearance cycling/system updates, and unsaved-change behavior.
Check dark/light desktop and mobile, short screens, reduced motion/transparency, forced
colors, and scrolling the final content above the pill. Unsupported blur must retain a
readable opaque surface. Existing domain controls and photos remain unchanged.

Run the repository verification and container smoke gates. Record actual results and
unavailable checks in delivery evidence; this spec does not assert they have passed.
Capture current-source screenshots outside Git and attach representative evidence to the
PR through the supported attachment workflow. Reverting this client-only change restores
the prior shell without data conversion.
