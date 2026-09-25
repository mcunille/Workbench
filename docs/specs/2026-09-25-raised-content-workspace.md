# Raised content workspace

**Status:** Implemented

## Status and intent

The owner selected this visual direction through an interactive mockup on 2026-09-25.
The shared authenticated shell now places the content sheet in front of the desktop
navigation backdrop. Focused navigation browser checks cover sheet geometry and the
interaction layer. Owner review of the localhost preview and the full release gates
remain pending before PR delivery.

People using Workbench should perceive their collection, purchasing, and other active
work as the foreground. The solid neutral navigation provides a stable, quieter backdrop. The earlier
desktop shell placed a raised glass sidebar over the workspace; this implementation
reverses that visual hierarchy while retaining Workbench's Tanzanite identity.

The supplied Shopify image inspired the relationship between surfaces, not a replacement
brand, navigation structure, or collection design.

## Selected visual direction

- Recess the desktop navigation into a solid neutral surface. Remove its raised
  right-hand bevel, rounded right corners, lateral shadow, and concentrated reflections.
- Raise one continuous content sheet beside it. The sheet begins immediately at the
  navigation boundary and meets the viewport's top, right, and bottom edges with no outer
  inset. On long pages, it extends through the full document; short pages fill the available
  viewport height. Preserve the existing scroll model rather than introducing an internal
  workspace scrollbar.
- Keep the sheet's top-left and bottom-left corners rounded, initially at 16px. Its
  top-right and bottom-right corners are square. Internal page padding remains: removing
  outer margins does not put headings, controls, or records against the sheet edge.
- Place the fine material edge and shallow reflections on the sheet's left boundary.
  Use a soft shadow falling toward the sidebar to make the sheet read as foreground.
  Restore the original blue/violet atmospheric gradients over the canvas within the
  sheet. Keep them faint at the top, with no tint over photographs.
- Dark appearance uses the existing indigo, blue, and periwinkle Tanzanite palette.
  Light appearance uses the existing neutral Quartz treatment with a cool silver edge.
  Preserve Light, Dark, and Auto appearance behavior. Do not add a material selector.
- Retain the existing wordmark, typography, page controls, content density, navigation
  destinations, permissions, and selected-item treatment. The mockup's sample tables,
  account shortcut, before/after switch, and design adjustment controls are demonstration
  aids, not new product features.

## Layout and responsive behavior

Above the existing 48rem mobile breakpoint, apply the new relationship to both expanded
navigation and the collapsed icon rail. Preserve the current expanded width of 14.3125rem
(minimum 229px), 4.5rem rail, responsive defaults, separate narrow/desktop collapse choices,
and coordinated 220ms collapse transition. The sheet follows the navigation width without
jumping, overlapping its controls, or moving icons between alignment modes.

The outer sheet fills all remaining viewport width, including at 2400px. Its inner reading
content keeps the existing independent 76rem maximum width, centered in the available
space with equal inline margins. Keep wordmark and page heading aligned on the top row.
Do not conflate the sheet boundary with the reading-width container.

At 48rem and below, retain the production bottom navigation pill, its material, safe-area
handling, and content clearance. Use the existing full-width mobile content treatment;
the desktop left-edge elevation and corner treatment do not apply without a sidebar.
The mockup's compact top navigation is a prototype simplification and is not proposed
for production. This keeps the change focused on the desktop surface relationship.

## Interaction, accessibility, and stacking

Visual elevation must not be implemented by placing the entire navigation interaction
layer underneath the content. Profile menus and collapsed navigation labels still need
to overlay the sheet. Dialogs and their backdrops remain above both surfaces, and the
focused skip link remains visible. Decorative layers must not intercept pointer events.

Preserve current navigation semantics, accessible names, focus visibility, keyboard
order, Escape and outside dismissal, focus restoration, unsaved-change confirmation,
and authorization-dependent destinations. Do not clip popovers or focus rings to obtain
rounded sheet corners. Preserve sticky purchasing controls and their scroll behavior.

With reduced motion, remove the existing transitions as today. With reduced transparency
or unsupported material effects, use opaque neutral surfaces with a legible boundary.
Forced colors must expose navigation state and control boundaries without requiring
shadows, reflections, or color distinctions. Text and controls retain required contrast.

## Implementation boundaries

The implementation uses `src/Workbench.Client/src/navigation.css` and
`src/Workbench.Client/src/styles.css`, with a presentation wrapper around the existing
authenticated `main` in `App.tsx`. The appearance spectrum is inherited by the desktop
sheet edge and mobile pill. The sheet body repeats the original canvas atmosphere;
the desktop menu is solid `--surface`. Edge effects stay near the exposed boundary
without full-surface backdrop blur.

Apply the shared sheet consistently to existing authenticated routes, including
collection, purchasing, supplier, account, accounting, and administration views where
available. Loading, empty, error, and permission states stay inside the same surface.
Sign-in, invitations, and other unauthenticated pages retain their current composition.
There are no API, persistence, authorization, accounting, or schema changes.

Update `DESIGN.md` when the implementation makes this proposal current, explicitly
superseding its raised desktop navigation material placement while preserving its mobile
and interaction contracts. Keep the earlier navigation decision as historical context.

## Alternatives and remaining design risks

Retaining the raised sidebar would preserve the current hierarchy but would not meet
the selected direction. An inset floating content card was explored in the mockup; the
owner removed its outer margins and then its right-hand rounding to connect it to the
viewport. Elevating both surfaces would weaken the intended foreground distinction.

The main implementation risks are trapped navigation overlays, accidental changes to
sticky positioning or scrolling, and excessive tint or blur across a much larger surface.
The stacking, layout, and material constraints above address those risks. Validate the
initial 16px left radius and restrained edge intensity against real content during browser
review; the mockup's tuning controls do not establish a new saved user preference.

## Acceptance criteria

1. In expanded and collapsed desktop states, the sidebar reads as background and one
   continuous sheet reads as foreground in both appearances.
2. The sheet has zero outer top, right, and bottom inset. Both right corners compute to
   0px; the left corners use the agreed radius. Internal content spacing is preserved.
3. The sheet spans the remaining width at 1280px and 2400px, while the inner reading
   area remains independently constrained. Short and long pages preserve usable scrolling.
4. Navigation collapse, route changes, loading, empty, and error states do not produce
   overlapping content, unexpected horizontal overflow, or a detached floating panel.
5. Profile disclosure and collapsed labels appear above the sheet; dialogs, confirmations,
   skip links, and sticky page actions retain their existing behavior and focus handling.
6. Mobile navigation remains the bottom pill. At 320px, 390px, and the 48rem boundary,
   controls remain reachable and the last content can scroll clear of the pill. Check
   200% text, long labels, and short viewport heights as well as normal content.
7. Reduced motion, reduced transparency, forced colors, and unsupported blur retain a
   clear, usable interface. No decorative overlay obscures photographs or captures input.
8. Prototype-only controls, data, and alternate navigation do not ship.

## Verification and delivery

Extend existing navigation browser coverage with focused geometry and overlay assertions.
Use the repository's characterization/TDD guidance for any changed interaction behavior;
avoid tests that merely duplicate CSS declarations. Exercise the real collection and a
long purchasing editor, plus account/administration overlays and permission-dependent
navigation. Assess meaningful behavioral changes with available mutation tooling; report
any limitation rather than claiming CSS rendering has been mutation-tested.

The focused `navigation.spec.ts` browser selection passed from current source (11/11).
The isolated localhost preview was refreshed and inspected at dark/light desktop widths
through 2400px and mobile widths down to 320px. The current-source `verify.ps1` and
`smoke-container.ps1` delivery gates remain pending, as do the wider affected browser
selection and PR attachment of representative external visual evidence. Keep screenshots
outside Git history and attach reviewed evidence through the supported PR workflow.

The interactive sample mockup has exercised comparison, collapse, local search, details,
sample creation, navigation, appearance switching, and widths down to 320px. That is
prototype evidence only; it does not establish production behavior, full accessibility,
or completion of repository application gates.

The written proposal and implementation plan were reviewed before source changes.
Deliver through a ready-for-review PR after preview feedback and release gates. A
client-only revert restores the previous surface treatment without data conversion.
Merge and production rollout remain separately authorized operations.
