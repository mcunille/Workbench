# Tanzanite application extension

Status: approved for implementation by the request to update the rest of the app after
merging the sign-in design. This extends the accepted visual language; it does not change
application architecture or workflows.

## Scope

- Apply the neutral white/charcoal palette with faint blue/violet atmosphere at the top.
- Carry the original geometric stag, system sans Workbench wordmark, and muted
  “by The White Stag Collection” attribution into the workspace and public account states.
- Use subtle glass for chrome, opaque readable content surfaces, and neutral depth.
- Apply the approved radius scale: small 2px, badges 4px, inputs/buttons 6px, cards 12px.
- Cover collection grid/list, details, editors, dialogs, account, administration, recovery,
  invitations, loading, unavailable, and denied states in both appearances.
- Preserve the approved sign-in geometry and all existing copy, navigation, permissions,
  API contracts, photos, validation, and persistence behavior except shared branding copy.

## Acceptance and verification

Shared-brand regression assertions must fail before implementation and pass afterward.
Run client tests, lint, build, repository verification, and the container smoke gate.
Inspect actual dark/light desktop and mobile screens, including a populated collection,
record/editor/dialog, account/administration, and public account forms. Check horizontal
overflow, readable control boundaries, keyboard focus, and reduced-motion/transparency
fallbacks. Document unavailable checks accurately. No mutation tooling is currently
configured for these client presentation changes.

The source-backed palette, materials, typography, and dimensions are maintained in
[DESIGN.md](../../DESIGN.md).
