# Tanzanite visual language

**Status:** Sign-in implemented; the application extension was accepted after the sign-in design
merged. This record preserves that approval sequence, not a new assertion of deployment.

[DESIGN.md](../../DESIGN.md) owns the current palette, materials, typography and dimensions.
This document records why the visual direction was selected and how its scope expanded.
[Floating-label fields](2026-09-08-floating-label-fields.md) remain a separate decision.

## First acceptance: sign-in

The sign-in page introduces the approved centered glass composition. The previous
small bronze stag, serif wordmark, and opaque panel do not provide the desired
brand presence or depth. This change is scoped to the signed-out sign-in surface.

### Accepted design

- Center a translucent neutral panel with a subtle highlighted edge and layered shadow.
- Use the approved radius scale: small details 2px, badges 4px, inputs/selects and
  buttons 6px, cards 12px. Card and control radii remain the same on desktop and mobile.
- In dark mode, use near-black with faint blue and violet confined to the top of the
  viewport. Avoid a colored lower background or pink glow.
- Use the supplied geometric stag and star, preserving their vector paths. Remove
  the source wordmark and Illustrator metadata; display the mark in white in dark
  mode and black in light mode.
- Use a clean sans-serif Workbench wordmark. Directly below it, show
  `by The White Stag Collection` in smaller, muted gray text.
- Retain the centered composition in light mode with white glass, charcoal text,
  and faint blue/violet light. Preserve the existing appearance preference control.
- Keep existing sign-in copy, labels, autocomplete, pending/error states, and recovery
  navigation. Use neutral primary controls and visible keyboard focus.
- Fit narrow viewports without horizontal scrolling; allow vertical scrolling on
  short screens. Disable decorative button movement for reduced-motion users.

The user selected the centered alternative over a split brand/form layout and approved
the restrained gradient revision. Light-mode support and the appearance selector are
retained adaptations. No authentication/API/storage contract changes are required.

### Verification

Use focused branding and authentication tests, frontend build/lint, desktop and mobile
browser inspection in both appearances, keyboard and rejected-login checks, and the
repository verification/container gates. Record unavailable gates accurately in the PR.

Reverting the scoped sign-in component, shell, stylesheet, and asset restores the
previous presentation without data migration.

## Subsequent acceptance: application extension

Status: approved for implementation by the request to update the rest of the app after
merging the sign-in design. This extends the accepted visual language; it does not change
application architecture or workflows.

### Scope

- Apply the neutral white/charcoal palette with faint blue/violet atmosphere at the top.
- Carry the original geometric stag, system sans Workbench wordmark, and muted
  “by The White Stag Collection” attribution into the workspace and public account states.
- Use subtle glass for chrome, opaque readable content surfaces, and neutral depth.
- Apply the approved radius scale: small 2px, badges 4px, inputs/buttons 6px, cards 12px.
- Cover collection grid/list, details, editors, dialogs, account, administration, recovery,
  invitations, loading, unavailable, and denied states in both appearances.
- Preserve the approved sign-in geometry and all existing copy, navigation, permissions,
  API contracts, photos, validation, and persistence behavior except shared branding copy.

### Acceptance and verification

Shared-brand regression assertions must fail before implementation and pass afterward.
Run client tests, lint, build, repository verification, and the container smoke gate.
Inspect actual dark/light desktop and mobile screens, including a populated collection,
record/editor/dialog, account/administration, and public account forms. Check horizontal
overflow, readable control boundaries, keyboard focus, and reduced-motion/transparency
fallbacks. Document unavailable checks accurately. No mutation tooling is currently
configured for these client presentation changes.

The source-backed palette, materials, typography, and dimensions are maintained in
[DESIGN.md](../../DESIGN.md).
