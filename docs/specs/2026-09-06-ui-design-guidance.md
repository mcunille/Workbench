# Workbench UI design guidance

**Status:** Proposed

**Research date:** 2026-09-06

**Scope:** Product UI design direction; no application behavior or dependency changes.

**Decision:** Recommend a calm studio workspace with progressive capability, responsive task
layouts, and equally complete light and dark themes. Implementation requires design acceptance.

## Purpose and product fit

Workbench should feel welcoming when someone records their first gemstone and dependable when a
business works through hundreds of records. Modern means clear hierarchy, fast response, and good
platform behavior. Lightweight means little visual clutter and little unnecessary code. Flexible
means the same records and concepts can support different tasks and levels of experience.

This proposal extends the [product vision](../VISION.md) and
[design principles](../DESIGN-PRINCIPLES.md). It proposes presentation conventions for the four
product areas; it does not approve inventory fields, accounting commands, saved-view persistence,
commerce workflows, public sharing, or a component-library migration. Those need their own scoped
requirements. Examples below illustrate future screens, not capabilities already implemented.

### Current evidence

At repository revision `92976ca`, the client uses React, TypeScript, Vite, plain CSS, and a typed API
client. Its package manifest contains no component, icon, animation, or styling framework.
[App.tsx](../../src/Workbench.Client/src/App.tsx) presents sign-in, recovery/invitation routes,
session management, and authorized user administration. The signed-in landing content describes
session infrastructure; it is not yet a collection workspace.

[styles.css](../../src/Workbench.Client/src/styles.css) supplies a green light palette, large
headings, rounded panels, and one responsive media query. It has no dark-theme token mapping or
theme preference control. The existing green is implementation history, not established brand
authority. Subsequent owner feedback rejects that accent and supplies The White Stag Collection
logo as the brand reference. Reduce oversized operational headings and decorative panel treatment. This is source inspection, not a rendered UI audit or an
accessibility conformance assessment.

## Research synthesis

The [UI Skills catalog](https://www.ui-skills.com/) collects independently authored guidance.
The following entrypoints were read as research, not installed or executed. Their workflow commands,
automation instructions, and arbitrary library choices do not become Workbench policy. This is a
targeted review of relevant skills, not an exhaustive audit of the catalog or its software.

| Resource | Useful contribution | Workbench disposition |
| --- | --- | --- |
| [interface-design](https://www.ui-skills.com/skills/dammyjay93/interface-design) | Task-led product design, coherent surfaces, reusable controls and semantic tokens. | Primary visual-system reference; choose domain-specific composition without demanding novelty in every control. |
| [better-layout](https://www.ui-skills.com/skills/jakubkrehel/better-layout) | Grouping, visible disclosure, content-driven adaptation and long-content checks. | Guide responsive layout; tune spacing to the task. |
| [better-typography](https://www.ui-skills.com/skills/jakubkrehel/better-typography) | Role-based type, numeric alignment, wrapping and readable input text. | Use a small type scale and stable numerical columns. |
| [better-accessibility](https://www.ui-skills.com/skills/jakubkrehel/better-accessibility) | Native semantics, keyboard and screen-reader walkthroughs, focus and touch targets. | Use as a review aid; W3C requirements take precedence. |
| [better-ui](https://www.ui-skills.com/skills/jakubkrehel/better-ui) | Optical alignment, consistent nested corners and interruptible feedback. | Use for final polish; exact animation recipes are optional. |
| [interaction-design](https://www.ui-skills.com/skills/wshobson/interaction-design) | Motion explaining feedback, orientation and continuity. | Adopt the purpose; omit decorative ripples, scroll reveals and routine page choreography. |
| [frontend-ui-engineering](https://www.ui-skills.com/skills/addyosmani/frontend-ui-engineering) | Composable presentation, deliberate state ownership and complete UI states. | Follow the existing React/CSS architecture; introduce dependencies only for demonstrated needs. |
| [web-design-guidelines](https://www.ui-skills.com/skills/antfu/web-design-guidelines) | Routes reviews to the upstream Vercel checklist. | Review its [actual guidelines](https://raw.githubusercontent.com/vercel-labs/web-interface-guidelines/main/command.md), applying the exceptions below. |
| [impeccable](https://www.ui-skills.com/skills/pbakaus/impeccable) | Operational interfaces prioritize task completion and consistent expectations. | Retain this distinction from marketing design; do not import its launcher or workflow mandates. |

Resolve conflicts deliberately:

- Keep the system font stack. A rejection of familiar fonts is a stylistic preference; it does not
  outweigh readability, dependable local loading, or Workbench's small dependency footprint.
- Use subtle surface boundaries, but give inputs and focus indicators sufficient contrast. The
  interface-design entry's blanket hit-area wording is not the WCAG AA definition: 24 CSS pixels
  with specified exceptions is the AA minimum; 44 pixels is our more generous touch target.
- Use sentence case and natural labels. Do not adopt Vercel's title-case requirement or blanket
  disabling of autocomplete. Preserve useful browser completion, especially in authentication.
- Do not put all component state in URLs. Only intentionally shareable navigation/view state belongs
  there; passwords, recovery tokens, sensitive search text, and drafts do not.
- Profile before virtualizing. A fixed item-count rule does not establish that a library is needed.
- Use native controls where they satisfy the task; native select and date controls are not
  categorically unstyleable. A custom control must justify its accessibility and maintenance cost.
- Do not hide overflow to conceal broken layout, delay every route for animation, or optimistically
  report a consequential server operation as successful.

## Experience principles

1. **Make the next useful action obvious.** A new collection should explain how to add an item.
   A work queue should show what needs attention. Do not substitute decorative metrics for a task.
2. **Reveal depth in context.** Start with the essential fields and actions. Label optional sections
   by their purpose, such as Acquisition or Documents; avoid an unexplained Advanced drawer.
3. **Let expertise increase speed.** Offer visible filters, keyboard access, consistent actions,
   and appropriate density. Shortcuts accelerate an action that is also available on screen.
4. **Respect the object.** Item identity, photographs, measurements, location and history deserve
   clear treatment. Financial values need units and meaning, not oversized summary numbers.
5. **Keep work and context intact.** Returning from a record should restore the list position and
   view. Failures should explain what happened and preserve recoverable work.
6. **Make consequential actions explicit.** Name the affected record and effect. Show completion only
   after authoritative success. Never disguise a financial correction as an ordinary edit.
7. **Make delight practical.** Clear empty states, stable layouts, accurate copy and responsive
   controls should make the product pleasant throughout repeated daily use.

### Hobbyists and power users

| Situation | Approachable default | Additional depth when useful |
| --- | --- | --- |
| First collection | Clear item identity, optional photograph, minimum domain-required data. | Classification, provenance, acquisition and documents grouped by purpose. |
| Finding an item | Search and a few understandable filters; optional photo view. | Column selection, compound filters and named views when supported. |
| Repeated operations | Obvious individual actions with clear results. | Compact rows, multi-selection and batch actions with an explicit scope. |
| Understanding a record | Summary with measurements, location and current state. | Connected work, financial effects and explainable history. |
| Professional expansion | Discoverable relevant product areas as they ship. | Accounting, commerce and administration according to actual authority and need. |

Do not require a permanent Hobbyist/Professional mode or duplicate records between modes. Optional
capability is distinct from authorization: hiding a section cannot grant or revoke permission.
Avoid locked-feature advertisements and empty future modules in ordinary navigation.

## Visual direction: a calm studio workspace

Think of a well-organized jeweler's work surface: quiet surroundings, precise labels, and objects
that reward close inspection. Use neutral paper-like light surfaces and charcoal dark surfaces,
with a restrained bronze accent derived from The White Stag Collection identity. Avoid simulated wood, velvet,
metallic gradients, glass blur, and ornamental gem shapes in routine controls.

### Brand relationship and accent options

The owner-supplied **The White Stag Collection.svg** uses `#947C4A`, a muted antique gold, with
an angular, finely drawn stag and classic lettering. Preserve the original artwork and its gold;
do not replace it with a generic gem icon. A compact stag emblem beside a serif Workbench wordmark
is the proposed product signature. Keep the full company lockup for settings/about or larger brand
placements where its lettering remains legible. Body text, controls and numerical tables retain
the system sans-serif stack. An emblem-only presentation is a proposed adaptation of the supplied
artwork, not a newly approved company logo.

Use warm stone and charcoal neutrals without a green cast. **Bronze is recommended**, with Ink and
Slate blue retained as comparison options in the mockup. Accent choice remains proposed; the owner
has supported the overall direction but has not selected a final accent.

| Accent option | Light action fill / text | Dark action fill / text | Character |
| --- | --- | --- | --- |
| Bronze (recommended) | `#775D2F` / `#FFFFFF` | `#D1B77C` / `#292316` | Closest relationship to the logo, warm and understated. |
| Ink | `#343944` / `#FFFFFF` | `#CFD0D2` / `#202329` | Quiet, largely neutral controls; the gold emblem supplies the brand color. |
| Slate blue | `#405D78` / `#FFFFFF` | `#A5C3E0` / `#1A2938` | A cooler complement to the gold identity. |

The original gold has approximately 4.01:1 contrast against white, so it is not the default fill
for buttons with normal-sized white text. Bronze uses darker/lighter functional variants while
leaving the artwork unchanged. The opaque action pairs above calculate to 6.19:1 / 8.00:1 for
Bronze, 11.57:1 / 10.20:1 for Ink, and 6.87:1 / 8.10:1 for Slate blue (light / dark). These are
static pair checks, not proof of complete component-state accessibility.

The recognizable pattern is the **item identity block**: photograph or honest missing-photo
placeholder, readable name, stable identifier, and a short line of relevant measurements. Reuse
that hierarchy in a collection entry, record header, related-item selector, and work input/output
reference. Exact fields remain subject to the corresponding domain specification.

### Typography, spacing and density

These are initial Workbench design values, to confirm in rendered specimens rather than universal
standards. Express text and spacing in `rem`; pixel equivalents below assume a 16px root.

| Role | Starting value | Application |
| --- | --- | --- |
| Font family | `ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif` | No remote font dependency; test Windows, macOS, Android and iOS fallbacks. |
| Page title | 28/36px, weight 600 | 24/32px on narrow layouts; operational pages do not need hero-sized headings. |
| Section title | 20/28px, weight 600 | Distinguish sections without excessive boxes. |
| Body and inputs | 16/24px, weight 400 | Keep inputs at least 16px on mobile; allow user text enlargement. |
| Navigation and table text | 14/20px, weight 400 or 500 | Compact presentation reduces padding before reducing readability. |
| Supporting metadata | 13/20px, weight 400 | Never use tiny text for required warnings, amounts or actionable information. |
| Spacing scale | 4, 8, 12, 16, 24, 32, 48px | Related controls have less separation than distinct sections. |
| Corners | 6px small, 10px controls, 14px panels | Pills reserved for tags/status; visually reconcile nested corners. |
| Comfortable controls / rows | 44px / at least 52px | Default; rows grow when content wraps. |
| Compact controls / rows | 32px / at least 36px | Opt-in for fine-pointer work; retain 44px targets on touch-capable layouts. |

Use tabular numerals in aligned monetary and measurement columns. Right-align comparable numbers;
keep currency and units explicit. Never align unrelated meanings merely because both are numeric.
Cap reading paragraphs around 65 characters; allow work tables to use available width. Full names,
identifiers and precision must be accessible by opening or expanding a record, not hover alone.
This adapts [better-typography](https://www.ui-skills.com/skills/jakubkrehel/better-typography).

### Semantic color tokens

Components consume semantic roles, not raw colors. The following opaque sRGB values are a proposed
starting palette, not a complete state palette or a conformance claim. Separate selected, hover,
pressed, disabled, focus, chart and status backgrounds during implementation. Do not lower an
entire component's opacity to create disabled styling.

| Token role | Light | Dark | Use |
| --- | --- | --- | --- |
| `canvas` | `#F6F5F2` | `#191919` | Application background. |
| `surface` | `#FFFFFF` | `#222221` | Content and controls. |
| `surface-raised` | `#FFFFFF` | `#2C2C2A` | Menus, dialogs and floating surfaces. |
| `text` | `#292824` | `#F1EFEB` | Primary content. |
| `text-muted` | `#625F58` | `#BAB7AF` | Readable supporting content, never decorative opacity. |
| `border-subtle` | `#DFDDD6` | `#494741` | Decorative separation only. |
| `border-control` | `#878176` | `#969083` | Identifiable input/control boundary. |
| `accent` | `#775D2F` | `#D1B77C` | Primary action fill and selected emphasis. |
| `on-accent` | `#FFFFFF` | `#292316` | Text/icons on the accent fill. |
| `focus` | `#286A9A` | `#8CCBFA` | Visible focus ring, with surface-colored offset. |
| `danger-text` | `#A12835` | `#FFB3BB` | Error/destructive text paired with an explicit label. |
| `warning-text` | `#775000` | `#F1CF83` | Warning text paired with an explicit label. |
| `success-text` | `#246441` | `#A3D9B4` | Success text paired with an explicit label. |

Static sRGB luminance calculations for these opaque values give the following minimum ratios
against `canvas`, `surface` and `surface-raised` (accent text is measured against its accent fill).
These checks validate the starting pairs only; compositing, component states and focus placement
still require rendered verification.

| Pair | Light minimum | Dark minimum |
| --- | --- | --- |
| Primary text / base surfaces | 13.53:1 | 12.18:1 |
| Muted text / base surfaces | 5.84:1 | 6.98:1 |
| Control border / base surfaces | 3.55:1 | 4.41:1 |
| Focus / base surfaces | 5.32:1 | 8.02:1 |
| Danger, warning and success text / base surfaces | 6.48:1 | 8.27:1 |
| On-accent / accent | 6.19:1 | 8.00:1 |

Keep brand accent and semantic status as separate roles. Neutral availability labels do not need
success coloring; reserve semantic color for states that benefit from it. Use spacing and surface levels
for hierarchy, thin separators for structure, and restrained shadows on actual overlays. In dark
mode, distinguish overlays through surface and border contrast rather than stronger black shadows.
The semantic-role approach is informed by
[interface-design](https://www.ui-skills.com/skills/dammyjay93/interface-design).

### Theme behavior

Offer **System, Light, Dark** in Appearance, reachable from both the public authentication surface
and account settings. Default to System; follow OS changes only while System is selected. Initially
store only this non-sensitive browser preference locally. Storage denial or a corrupt value must
fall back safely to System. Account synchronization is a separate persistence decision.

Resolve the preference before first paint using a mechanism compatible with the application's
content security policy; avoid a bright flash during dark startup. Match CSS `color-scheme` and
browser theme color to the resolved theme so native UI follows the page. See
[MDN color-scheme](https://developer.mozilla.org/en-US/docs/Web/CSS/Reference/Properties/color-scheme).
Theme changes must preserve focus, scroll, validation messages and unsaved input.

Theme every surface, including dialogs, tooltips, autofill, selection, native fields, skeletons,
charts, errors, disabled states and empty states. Do not invert or tint item photographs. Use a
consistent neutral image backing; show uncropped originals in detail inspection. Check print output
separately for legible light paper surfaces. Honor forced-colors mode with system colors and visible
boundaries; dark mode is not a replacement for high-contrast support.

## Navigation and responsive composition

Use a compact header for Workbench identity, the authenticated workspace name, and account/help
access. Keep the workspace name visible as context, not as a tenant switcher: the
[architecture](../ARCHITECTURE.md) binds the current user to one tenant. Product-area navigation
should use the vision's names: Inventory, Work orders, Bookkeeping, Commerce. Show areas only as
implemented and relevant; administration remains permission-based. Never create dead-end menu items
to fill out the intended future shell.

A page starts with its title, useful context and one visually dominant action for the current task.
Then show filters or tools, then the work itself. Record detail uses a summary followed by clearly
named sections. Prefer a full page for sustained editing and linked history; reserve dialogs for
short bounded decisions. Avoid nested modal stacks.

The ranges below are specimen starting points. Change layout where real content stops fitting,
including when users zoom; do not identify capability from a device name or width alone. This
follows [better-layout](https://www.ui-skills.com/skills/jakubkrehel/better-layout).

| Available width | Navigation | Content and editing |
| --- | --- | --- |
| Narrow, roughly below 48rem | Compact header and labeled Menu opening a modal drawer. | One column; primary action in flow or a safe sticky action region; short filters open a labeled sheet. |
| Medium, roughly 48–72rem | Drawer when labeled navigation would crowd the task. | Two columns only where field groups remain readable; full-page record editing. |
| Wide, roughly 72rem and above | Approximately 15rem labeled sidebar, optionally collapsed. | Fluid list/table area; optional detail pane only when both panes remain useful. |

Use 16px narrow page margins and 24–32px wider gutters as starting values. Reading and ordinary
editing panels should not expand indefinitely on ultrawide screens. Keep DOM order aligned with
visual reading order; use logical CSS properties to support future translation and RTL layout.

On mobile, a collection table can become labeled item rows showing identity, photograph, essential
measurement and status. Open a record for remaining fields. Preserve filters, sort and selection
semantics across layouts. Do not convert financial comparison tables into ambiguous unlabeled
cards: provide an accessible horizontal table region and a useful summary/detail view where needed.
Horizontal scrolling belongs to genuine two-dimensional content, not the whole page.

Preserve ordinary links, browser Back/Forward, and opening records in another tab. Put safe route,
sort, page and view choices in URLs when supported. Never expose sensitive searches or drafts merely
to make a view bookmarkable. On narrow screens, returning from detail restores the prior list.

Support touch, keyboard and pointer simultaneously. No action depends solely on hover, swipe, drag,
right-click or a shortcut. Keep primary controls reachable with the software keyboard open, respect
safe-area insets, and ensure sticky chrome cannot cover content or focus. Do not lock orientation,
disable pinch zoom, or open the mobile keyboard automatically on ordinary page entry.

## Component and interaction contracts

| Pattern | Required behavior |
| --- | --- |
| Buttons and links | Action verbs such as Save item or Revoke session; links navigate. Primary, secondary, quiet and destructive treatments have distinct purposes. Icon-only controls need an accessible name and a discoverable explanation. |
| Forms | Persistent labels, units, required/optional meaning and useful examples. Validate on submit, then help correct errors; avoid scolding untouched fields. Associate errors with fields and focus an error summary or first invalid field. |
| Saving | Keep the action identifiable while pending and prevent duplicate submission. Distinguish Saving, Saved and Could not save. Preserve input on recoverable failure; do not silently retry consequential commands. |
| Selection and bulk work | Separate row navigation from selection. Show selected count, whether selection spans pages, the action's scope and a clear way to cancel. Never imply Select all means an entire result set unless it does. |
| Search and filters | Distinguish zero matches from an empty collection. Show active criteria and a clear reset. Apply and Cancel on a mobile filter sheet should have predictable effects. |
| Tables | Real headers, meaningful sorting state, explicit units and currency. Explain totals and their scope. Separate unknown, zero and not applicable. A read-only table is not automatically an ARIA grid. |
| Menus and dialogs | Predictable keyboard operation, visible close/cancel, safe focus placement and restoration. Modal background is inert. A long workflow belongs on a page. |
| Notifications | Inline feedback near the work; polite status announcements for routine results. Errors and actionable notices persist until resolved or dismissed. A toast is never the sole record of a consequential result. |
| Images and attachments | Reserve dimensions before loading; provide missing/failed-image states. Preserve natural color and aspect ratio in inspection. Show upload progress and failure only to the precision the backend actually supplies. |
| Charts | Use only to answer a real comparison or trend question. Label series, provide a text/table equivalent and distinguish data without color alone; validate both themes. |

Prefer existing controls, semantic HTML and ordinary CSS. Evaluate an accessible headless primitive
only when composite behavior needs it; style it through Workbench tokens. Keep component selection
separate from product design acceptance. No proposed guidance requires Tailwind, shadcn, a motion
library, or a global state framework. The composition reference is
[frontend-ui-engineering](https://www.ui-skills.com/skills/addyosmani/frontend-ui-engineering), and
the behavior reference for composite widgets is the [W3C APG](https://www.w3.org/WAI/ARIA/apg/patterns/).

### State, failure and trust

Design each data surface for initial loading, populated content, empty content, no matches, partial
failure, retry, forbidden access, expired authentication and stale/conflicting edits where the API
supports them. Preserve still-valid content on a partial refresh failure and label its staleness.
Distinguish an unavailable service from a signed-out user; a temporary outage is not a reason to
discard a form or report that credentials are wrong.

Do not claim offline saving without an approved durable queue and conflict model. Unsaved work may
remain in memory while the user stays in the same authorized session; warn before navigating away.
Do not persist sensitive drafts, tokens, customer data or business records in local storage by
default. Clear protected views and caches on sign-out or identity loss. Recovery/invitation handling
must preserve existing security behavior; generic outward-facing identity messages take precedence
over more revealing explanatory copy.

Permissions and domain state determine available commands; the server remains authoritative. Explain
an unavailable action when doing so reveals no protected information. A discoverable disabled action
needs a readable nearby reason, not a tooltip attached to an unfocusable control. Destructive actions
need a clear consequence and confirmation or a genuine supported undo. Financial commands show their
effect and await server confirmation; correction labels must respect open/closed-period rules in
[DESIGN-PRINCIPLES.md](../DESIGN-PRINCIPLES.md), without inventing new ledger semantics.

### Motion and tone

Use motion to acknowledge input or explain a surface change. Start with 100–150ms control feedback
and 150–220ms overlay transitions, interruptible and implemented with CSS where practical. Repeated
typing, table selection and keyboard navigation should not wait for animation. Prefer opacity and
transform; name transitioned properties explicitly. Reduced motion removes travel, scale and
decorative loops without removing meaningful feedback. These are Workbench timing choices informed
by [interaction-design](https://www.ui-skills.com/skills/wshobson/interaction-design).

Polish includes aligned icons, stable button widths during saving, clear image edges and consistent
corners; use [better-ui](https://www.ui-skills.com/skills/jakubkrehel/better-ui) as a focused finishing
reference. Avoid confetti for routine saves, animated monetary totals, sound by default, and
entrance animation on every row. A welcoming first-item message can be delightful without movement.

Use direct, friendly sentence case: “Add your first item”, “No items match these filters”,
“We couldn't save your changes. Try again.” Only state that changes are preserved when true.
Explain specialist terms near first use without renaming them incorrectly. Keep backend vocabulary
such as tenant derivation, durable sessions, providers and migration versions out of ordinary product
copy; technical support details belong in an appropriately scoped help surface.

## Accessibility and performance acceptance

Target **WCAG 2.2 AA** for complete workflows, not merely individual components. W3C is the authority;
the following highlights are not the entire standard. Use the
[WCAG quick reference](https://www.w3.org/WAI/WCAG22/quickref/) for the full review.

- Normal text has at least 4.5:1 contrast; qualifying large text at least 3:1. Measure actual rendered
  foreground/background pairs, including supporting text and states. See
  [Contrast Minimum](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html).
- Required control boundaries and state indicators meet 3:1 non-text contrast. Focus remains visible
  and unobscured. Use a clear outline with offset, and check its adjacent colors in every context.
- Use at least 44×44 CSS-pixel touch targets. Compact fine-pointer controls still meet WCAG's 24×24
  minimum or a documented exception; expanded targets never overlap. See
  [Target Size Minimum](https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum.html).
- Reflow at 320 CSS pixels without lost functionality or page-wide horizontal scrolling, except
  content genuinely requiring two-dimensional layout. Test 200% text enlargement and 400% browser
  zoom at a suitable desktop width. See [Reflow](https://www.w3.org/WAI/WCAG22/Understanding/reflow.html).
- Complete tasks by keyboard and screen reader. Provide names, landmarks, skip navigation, meaningful
  focus order, accessible validation and status announcements. Never rely on color alone. Allow
  password managers and paste; provide accessible authentication without an added memory puzzle.

For performance, use the published good Core Web Vitals thresholds as targets: LCP ≤2.5 seconds,
INP ≤200 milliseconds, CLS ≤0.1 at the 75th percentile, considered separately for mobile and desktop.
[Web Vitals](https://web.dev/articles/vitals) describes these metrics. No measurements are claimed
for Workbench by this proposal. Distinguish client interaction time, network delay and server cold
start; record realistic cold and warm visits rather than hiding server latency behind a skeleton.

Keep typography local, serve appropriately sized images, defer offscreen media, and reserve space
for loading content. Start large collections with bounded server queries and pagination. Add
virtualization only after measurement, preserving keyboard focus, selection and reading access.
Measure production route assets before and after a new UI dependency; agree a bundle budget from
that baseline rather than presenting an arbitrary number as proven. Do not block navigation or
input on nonessential animation or reports.

## Verification and implementation handoff

Before application work, accept or revise this direction. Then establish rendered specimens using
current sign-in, sessions and user administration as real workflows. Add representative collection
and financial examples only as clearly labeled fixtures until their domain specifications exist.
Promote this proposal into living UI guidance when the accepted design is implemented.

| Acceptance scenario | Evidence required during implementation |
| --- | --- |
| Begin simply and discover depth | A hobbyist can complete the agreed first-item task without accounting setup; a professional can find additional controls without changing products. Validate with representative users when domain screens exist. |
| Desktop and mobile | Inspect 320, 390, 768, 1024 and 1440 CSS-pixel widths plus intermediate resize; check portrait/landscape and the on-screen keyboard. No clipped actions or inaccessible record fields. |
| Theme parity | Every component/state in light and dark; System follows OS changes, explicit choice persists, unavailable storage falls back, and first paint is correct. |
| Accessibility | Automated audit plus manual keyboard, Windows NVDA and Safari VoiceOver checks; zoom, enlarged text, reduced motion and forced colors. Record browser/OS versions and actual limits. |
| Realistic content | Long names, missing images, large/negative/zero/unknown values, currency codes, precision, long translations and RTL specimen. No misleading totals or hidden essential content. |
| Resilience and authority | Delayed/error responses, duplicate clicks, access loss and stale edits. No false success, unsafe replay, leaked data or accidental input loss. |
| Professional throughput | Compare comfortable/compact rows, visible actions and optional shortcut paths using realistic data volume; measure task success and errors before declaring a speed improvement. |
| Polish and performance | Theme/viewport screenshots, production build measurements, stable loading layout, responsive typing, and no decorative delay on repeated work. |

Build the first implementation in bounded slices: tokens/theme and primitive states; responsive
shell and current account workflows; then domain screens as their specs are accepted. Use the
repository's TDD rules for behavior, appropriate mutation testing, and the application gates in
[CONTRIBUTING.md](../../CONTRIBUTING.md). A CSS screenshot alone is not proof of workflow correctness,
and an automated accessibility pass is not a conformance claim.

This documentation change has no API, schema, migration or runtime rollback. Future UI work must
preserve current route, authentication and server-authority contracts; changes to those boundaries
require a focused design. Theme preference should be version-tolerant and ignorable by older code.
Retain existing working screens until their replacements pass the agreed checks.

## Alternatives and remaining decisions

| Alternative | Tradeoff and decision |
| --- | --- |
| Photo-led gallery everywhere | Attractive for browsing; weak for comparing measurements and repeated operations. Offer it where useful alongside structured views. |
| Dense administration UI everywhere | Fast for practiced users but exposes too much complexity on first use. Choose progressive disclosure and optional density. |
| Separate beginner/professional products | Duplicates concepts and complicates growth. Keep one model with contextual depth. |
| Highly expressive animated dashboard | Adds visual and runtime cost without evidence of task value. Let real objects and careful details provide character. |
| Adopt a full framework immediately | Could accelerate complex widgets but adds conventions and migration work. Decide against actual component needs after specimens. |
| Dark theme by color inversion | Quick but distorts media and ignores surface/state contrast. Define both themes explicitly. |

The proposed palette, density and layout ranges need visual validation with actual item imagery and
long records. Product navigation still depends on which modules ship. Saved views, shortcuts,
preference synchronization and bulk commands need scoped requirements; none are silently added to
the delivery backlog by this document. Public collection sharing remains outside the accepted
product scope. Research links are mutable snapshots accessed on the date above; revisit a source
before adopting new code or dependencies from it.
