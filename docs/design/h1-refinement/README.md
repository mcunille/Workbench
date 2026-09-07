# H1 studio refinement

This refinement implements the owner's request for a more beautiful, polished and modern H1 UI
within the accepted [UI direction](../../specs/2026-09-06-ui-design-guidance.md). It changes
presentation, not inventory capability, authentication, API contracts, or data ownership.

## Design and research

The earlier implementation used the control-border color for nearly every structural divider,
placed appearance in a separate full-width strip, and gave technical build metadata too much
visual emphasis. The refinement separates quiet structural edges from identifiable controls,
combines workspace controls in one header, and gives collection entries and saved records a
consistent identity treatment.

[Apple's typography guidance](https://developer.apple.com/design/human-interface-guidelines/typography?changes=_5)
supports a small, legible hierarchy of sizes and weights. Its
[materials guidance](https://developer.apple.com/design/human-interface-guidelines/materials?changes=l_8_1)
explains depth as a way to distinguish interface layers. Workbench applies that principle with
opaque surfaces; the accepted direction explicitly excludes glass blur. Disney's
[layout practice](https://www.disneyanimation.com/process/layout/) emphasizes staging and clear
composition, and its [animation practice](https://www.disneyanimation.com/process/animation/)
connects timing with readable action. Applying these film principles to task focus is a Workbench
design interpretation, not a Disney web-UI standard. Research was accessed on 2026-09-07.

## Visual specification

- Preserve the supplied gold stag, serif Workbench wordmark, bronze actions, local system body
  fonts, warm light canvas, and charcoal dark palette. Add no font, component, or animation dependency.
- Use a compact desktop header, a 14rem navigation rail, and a fluid content region capped at 76rem
  including its gutters. On phones, place appearance beside the brand and keep all navigation visible.
- Establish one 14px-radius collection surface, 56px neutral missing-photo placeholders, 17px item
  names, readable location lines, inset dividers, and consistent outline icons. Never imply uploaded
  photos or category classification where H1 has neither.
- Give the short form and saved record matching surfaces and spacing. Group descriptive fields
  ahead of record metadata; preserve complete identifiers and notes. Show a compact release label
  with the full build in its title metadata.
- Keep controls at least 44px, inputs 16px, clear blue focus outlines, and distinct disabled/error
  states. Change palette colors immediately; use 120ms press feedback only when reduced motion is off.

## References and fidelity review

[Collection concept](collection-concept.png) · [Form/detail/mobile concepts](states-concept.png)

![Implemented desktop collection](collection-1440-light.png)

[Implemented phone, dark](collection-390-dark.png)

[Loaded account at 320px](account-320-dark.png) · [Loaded administration at 320px](administration-320-dark.png)

The generated images are composition references constrained by the existing written design system.
They are not new domain specifications. The implementation was inspected in the Codex in-app browser
at `http://127.0.0.1:4179`, including a real save on desktop and phone. Reproducible Playwright captures
were compared directly with the concept images using `view_image`. The primary concept generated
at 1504x1046 rather than its requested 1440x1000; the app was inspected at 1440x1000 and multiple
responsive widths instead of treating raster scaling as a product requirement.

| Comparison | Result and deliberate refinement |
| --- | --- |
| Composition | One header, rail, title/action pair, and collection surface match the concept's hierarchy. Existing 14rem rail and 30px title interpretation avoid its oversized chrome. |
| Typography | Local system body/controls and serif brand retained. 24px phone headings and 16px inputs follow written guidance. The generated font is not shipped or substituted as a remote asset. |
| Palette | Accepted opaque semantic values retained. Generated saturated gold selection and metallic-looking fill are replaced by pale-bronze selection and flat accessible bronze actions. |
| Identity and icons | Original stag retained rather than the generated substitute. One small original SVG icon family replaces text diamonds/arrows; a shield identifies administration. |
| Form/detail structure | Single form surface, persistent labels, grouped actions, and a record identity header follow the state reference. Notes, identifier and added time retain the API's exact values. |
| Copy | Collection description, actions and field names remain unchanged. Redundant Inventory and Secure tenant access eyebrows are removed. The concept's invented phone subtitle and Added date label are not adopted. Workspace name comes from the authenticated tenant. |
| Responsive layout | The concept's phone navigation stack is replaced with the already accepted wrapping H1 links. Appearance keeps its accessible name while its visible label is omitted in the compact signed-in header. |
| Motion | A browser check caught transient low contrast during palette interpolation. Color transitions were removed; focus and press feedback remain without delaying input. |
| Shared panels | Final loaded phone specimens exposed a split Sessions heading beside its action. The narrow panel header now stacks, with a regression asserting the short heading fits on one line. |

The implementation follows the normalized design specification above; it deliberately does not
claim literal pixel equivalence to the generated concepts. Required controls, copy, spacing,
palette, icons, hierarchy, and responsive behavior were compared, with the differences recorded here.

## Verification

The header regression failed before appearance was moved into the banner, then passed with exactly
one appearance control and a compact release label. Independent review also caught a storage-denied
preference reset across authentication. A focused test reproduced the reset; preference state now
lives above the public/authenticated views, preserving the user's choice within the page. The
independent follow-up review found no remaining actionable issues. All 31 client tests, lint, and production build
passed. The added browser scenario passed at 390/768/1024/1440px in both appearances, with 200% text,
keyboard focus, reduced motion, and forced colors. Existing H1 tests continue to cover 320px and
1280px, long content, safe retry and navigation. Contrast assertions now include supporting text,
labels and navigation as well as item names.

One targeted manual mutation restored the full build hash to the release label; its regression
failed for the expected missing compact label. The original source was restored byte-for-byte.
This is bounded mutation evidence, not a comprehensive mutation-tool run or coverage score.

The production JS bundle is 225.81 kB (69.67 kB gzip); CSS is approximately 12.2 kB (3.3 kB gzip).
These are build-output measurements, not field Core Web Vitals. Automated and keyboard evidence does not
establish screen-reader conformance; NVDA, VoiceOver and physical mobile keyboard testing remain
outside this pass.

The complete `scripts/verify.ps1 -SkipDependencyInstall` gate passed: 305 server tests, 31 client
tests, 15 browser tests, formatting, generated-contract drift, builds, all four migration drills,
and the published-release probe at `http://127.0.0.1:56978`. The final hardened-container check
also passed at `http://127.0.0.1:56876`. These disposable instances were cleaned up. The application
state fix was included in the client build, browser checks, published probe, and final container.

The narrated demo was refreshed with the refined H1 screens and passed its real-database scenario.
Its MP4 decodes successfully and creation/mobile frames were visually inspected. After the final
narrow-panel correction, the affected browser and release checks were rerun separately; the H1
footage is unaffected by that account-panel adjustment.
All 15 browser tests passed again, the published probe passed at `http://127.0.0.1:64334`, and
the final hardened-container run passed at `http://127.0.0.1:51414`. Loaded account/admin screenshots
confirmed the heading correction and complete, readable content at 320px. No material visual
mismatches remain relative to the normalized specification and intentional deviations above.
