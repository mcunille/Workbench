# Workbench UI visual reference

Open [workbench-ui-preview.html](workbench-ui-preview.html) in a browser after checking out or
downloading the repository. GitHub's file viewer displays its source rather than running it.
The file embeds the mockup styles, application script, sample photographs and brand emblem. Pinned
Lucide and Floating UI scripts load from a public CDN for icons and tooltip positioning. It needs
no build, server, account, development credentials or application API connection.

This is the reviewed visual companion to the accepted
[UI design guidance](../specs/2026-09-06-ui-design-guidance.md). It is an illustrative mockup, not
application source or a reusable production component library. Its browser wrapper is confined
to this documentation file and is not a proposed Workbench runtime dependency.

## What to explore

- System, Light and Dark appearance, including switching while an item is selected.
- List and gallery views, category and status filters, search, and compact rows.
- Item details and the short add-item flow using in-memory sample records.
- Narrow-screen navigation and item details, plus illustrative linked product-area screens.

Bronze is the accepted accent. The stag geometry and original `#947C4A` gold derive from the
owner-supplied company artwork; the serif Workbench wordmark is a product treatment. The six
gemstone/jewelry photographs were generated for the mockup. Names, provenance, measurements,
financial entries and work orders are fictional examples. Refreshing discards all changes.
Appearance is also local to the preview session; this does not implement production preference
persistence. Theme behavior and accessibility requirements in the specification remain authoritative.

## Verification evidence and limits

The in-conversation source was exercised in the Codex in-app browser at desktop and narrow widths,
including a 320 CSS-pixel content width with no horizontal overflow. Checks covered light/dark
appearance, list/gallery switching, search/no matches/reset, category/status filtering, density,
item details, sample creation, navigation and linked sample records.

After a reported light-theme contrast defect, list and gallery title text was bound explicitly to
the product text token, with the detail heading treated consistently and descendant color schemes
inheriting the selected product theme. Browser inspection confirmed dark title text in light mode
and light title text in dark mode. Selected pale-bronze rows retain the normal text color.

The standalone export was checked separately in the in-app browser: light list/gallery title
colors, dark detail-title color, sample item creation, and 320px content width without horizontal
overflow all passed. These mockup checks do not prove
production behavior, WCAG conformance, screen-reader support, financial correctness, performance
targets, mobile-device coverage or backend resilience. Application, container and mutation tests
are not applicable evidence for a documentation-only visual reference; run the required application
gates when implementing the design.
