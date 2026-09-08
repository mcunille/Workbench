# Workbench — Tanzanite Style Reference

> Quiet glass. A whisper of color.

**Appearance:** dark and light. **Reference surfaces:** sign-in and the collection workspace.

Tanzanite gives Workbench depth through translucent neutral surfaces, a fine highlighted
edge, and soft shadows. In dark appearance, faint blue and violet light enter from the
top of an almost black canvas. In light appearance, the same composition becomes white
glass with charcoal typography. Color supplies atmosphere; the form, brand, and primary
action stay neutral. A prominent geometric stag anchors the page above a clean Workbench
bench-pin wordmark and the quiet attribution, “by The White Stag Collection.”

This guide covers Tanzanite across sign-in, collection, editors, account and administration,
recovery, invitations, and shared dialogs. The existing appearance choices remain System, Light, and Dark;
Tanzanite is the design language, not a separate persisted theme setting.

## Tokens — Colors

The table below describes the sign-in glass variant. Shared text, canvas, action, border,
and focus colors match the application tokens. Eight-digit hex values
include alpha; they describe a layer, not its final composited screen color.

| Name | Dark | Light | Token | Role |
|------|------|-------|-------|------|
| Canvas | `#08090c` | `#ffffff` | `--canvas` | Full-page background |
| Translucent surface | `#15161aa6` | `#ffffffb8` | `--surface` | Neutral translucent control surface; light card base |
| Primary ink | `#f4f4f5` | `#222428` | `--text` | Wordmark, heading, labels, entered text |
| Secondary ink | `#b3b4bd` | `#60636b` | `--muted` | Supporting copy and recovery link |
| Maker attribution | `#93959f` | `#686b74` | `--sign-in-byline` | Small byline below Workbench |
| Control boundary | `#71737d` | `#898c95` | `--border` | Input and appearance-control borders |
| Glass edge | `#41434d` | `#d9dbe3` | `--glass-edge` | Decorative panel perimeter |
| Primary action | `#fafafa` | `#222428` | `--accent` | Neutral primary button fill |
| On primary | `#15161a` | `#ffffff` | `--on-accent` | Primary button label |
| Hover surface | `#24252c` | `#f0f1f5` | `--hover` | Secondary control hover |
| Focus | `#a5adeb` | `#555eb4` | `--focus` | Field border or action/link focus outline |

The dark card uses its own `#101114b8` base beneath its highlight gradient. It does not
use `--surface` directly. Error text inherits `--danger` from the application:
`#ffb3bb` in dark appearance and `#a12835` in light appearance.

### Atmospheric colors

| Layer | Dark | Light | Placement |
|-------|------|-------|-----------|
| Blue | `#263a7280` | `#526aba20` | Ellipse `65% 32rem`, centered at `10% -12rem` |
| Violet | `#43265360` | `#795aa71a` | Ellipse `55% 28rem`, centered at `95% -12rem` |

Both layers fade to transparent over the neutral canvas. Keeping their centers above
the viewport confines the visible color to the upper atmosphere. The lower page should
read as black or white. The accepted revision has no pink glow and no bronze accent.
Do not increase saturation to make Tanzanite more recognizable.

## Tokens — Typography

### System sans-serif — brand, headings, and UI

```css
font-family:
  ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
```

- **Weights:** 600 for the wordmark; 400 for the byline and body; 500 for the heading, labels,
  and primary button.
- **Character:** clean, restrained, readable. The bench-pin W identifies Workbench;
  the stag identifies The White Stag Collection.
- **Font delivery:** native system stack; no downloaded font or named custom font is required.
- **Rendering:** `font-synthesis: none` and `text-rendering: optimizeLegibility` are inherited.
- **Variation:** glyph shapes and perceived weight vary slightly by platform.

### Type scale

Pixel equivalents below assume a 16px root size. The implementation uses rem units.

| Role | Desktop | At 600px and below | Weight | Line height | Tracking |
|------|---------|--------------------|--------|-------------|----------|
| Sign-in wordmark | 44px / `2.75rem` | 38px / `2.375rem` | 600 | 1.15 | `-0.035em` |
| Maker byline | 13px / `0.8125rem` | Same | 400 | 1.5 | Normal |
| Sign-in heading | 36px / `2.25rem` | 30px / `1.875rem` | 500 | 1.25 | `-0.035em` |
| Supporting copy | 16px / `1rem` | Same | 400 | 1.6 | Normal |
| Field label | 16px / `1rem` | Same | 500 | 1.5 | Normal |
| Input text | 16px / `1rem` | Same | 500, inherited from label | 1.5 | Normal |
| Primary action | 18px / `1.125rem` | Same | 500 | 1.4 | Normal |
| Recovery link | 16px / `1rem` | Same | 500, inherited | 1.5 | Normal |
| Error message | 14px / `0.875rem` | Same | 400 | 1.6 | Normal |

These type roles are selector values, not additional custom properties. Do not infer a
marketing display scale, monospace family, or typography tokens that the application
does not implement.

## Tokens — Spacing & Shapes

**Rhythm:** mostly 8px increments, with small optical adjustments. **Density:** spacious
around the brand; practical and readable within the form.

| Space | Use |
|-------|-----|
| 6px | Wordmark-to-byline margin |
| 8px | Mark-to-wordmark margin, label-to-input gap, form-to-link margin |
| 16px | Mobile outer horizontal padding |
| 24px | Form row gap, desktop outer horizontal padding, mobile card horizontal padding |
| 32px | Form top margin, mobile card vertical padding, mobile brand bottom margin |
| 40px | Desktop card vertical padding and brand bottom margin |
| 48px | Desktop card horizontal padding and shell bottom padding |
| 88px | Mobile shell top clearance for the appearance control |
| 96px | Desktop shell top clearance for the appearance control |

### Border radius and dimensions

The radius scale is consistent across desktop and mobile:

| Role | Radius |
|------|--------|
| Small details | 2px |
| Badges | 4px |
| Inputs, including selects | 6px |
| Buttons | 6px |
| Cards | 12px |

Small-detail and badge values guide future components; the sign-in page has neither.

| Element | Desktop | At 600px and below |
|---------|---------|--------------------|
| Glass panel radius | 12px | 12px |
| Input, appearance selector, and primary button radius | 6px | 6px |
| Glass panel maximum width | 580px | Available width within page padding |
| Stag image box | 112 × 150px | 96 × 128px |
| Input minimum height | 62px | 62px |
| Primary button minimum height | 60px | 60px |
| Recovery link minimum hit height | 44px | 44px |

Use 1px panel and control borders. Control boundaries are deliberately stronger than
decorative glass edges. The panel height is determined by content, including errors.

## Surfaces

| Surface | Dark treatment | Light treatment | Purpose |
|---------|----------------|-----------------|---------|
| Canvas | Near-black with upper blue/violet atmosphere | White with faint upper cool light | Quiet page field |
| Glass panel | `#101114b8` plus white highlight gradient | `--surface` plus white highlight gradient | One clear container for the task |
| Floating-label input | `#121317` | `#ffffff` | Neutral editable region and label notch |
| Primary action | Near-white fill | Charcoal fill | Strongest actionable contrast |

Do not turn this into a stack of nested glass cards. The composition uses one primary
panel, with depth supplied by its material and soft shadows.

## Elevation

### Panel material

The panel uses a 24px backdrop blur, a fine perimeter, a top highlight, and two soft
outer shadows. It must still read as a complete panel when backdrop blur is unavailable.

| Layer | Dark | Light |
|-------|------|-------|
| Highlight gradient | `linear-gradient(155deg, #ffffff0b, #ffffff00 35%)` | `linear-gradient(155deg, #ffffff70, #ffffff10 35%)` |
| Inset top edge | `inset 0 1px 0 #ffffff75` | `inset 0 1px 0 #fff` |
| Near shadow | `0 8px 24px #00000030` | `0 8px 24px #20263c08` |
| Ambient shadow | `0 32px 80px #00000070` | `0 32px 80px #20263c12` |

### Control depth

- Floating-label inputs: no inset shadow or outer focus ring.
- Primary button: `0 4px 12px #00000015`; hover uses `0 6px 18px #00000025`.
- Keep shadows neutral. A colored glow around the full panel is outside this direction.

## Components

### Workbench lettering and favicon

The approved product mark is the solid bench-pin **W** replacing the first letter of
**Workbench**, followed by live text `orkbench`. Use the shared
[Wordmark component](src/Workbench.Client/src/Wordmark.tsx) for sign-in, authenticated
headers, and public/recovery headers. Do not put a second W or a separate bench-pin icon
beside the wordmark. The collection stag remains above the sign-in wordmark; the maker
attribution appears only on sign-in, separate and understated. Other application and
public account pages display only Workbench branding.

Use the system sans-serif stack above at **600 (semibold)**. Keep `-0.035em` tracking,
`1.15` line height, and a `0.025em` gap between the mark and `orkbench`. The mark uses the
approved path, tightly framed by `viewBox="2 5 28 22"`, at `0.715em` high and `0.91em`
wide. Align its bottom to the text baseline. Do not stretch, redraw, outline, or add
internal facets to the W. Native font metrics vary slightly across platforms.

| Placement | Font size | Treatment |
| --- | --- | --- |
| App and public/recovery header | 20px / `1.25rem` | Same size on mobile |
| Sign-in | 44px / `2.75rem` | 38px / `2.375rem` at 600px and below |
| Standalone branding preview | 52px / `3.25rem` | Display reference, not an app heading token |

The sign-in wordmark is larger than its 36px desktop / 30px mobile form heading,
so the product name remains the primary brand anchor. Render the complete wordmark as
one image role named `Workbench`; hide the decorative
SVG and partial visible lettering from assistive technology. Its color inherits the
surface's foreground via `currentColor`. Use one flat color without a gradient.

The [favicon](src/Workbench.Client/public/favicon.svg) uses the identical W path in a
`0 0 32 32` viewBox, preserving breathing room around the silhouette. It uses dark ink
`#292720` on light browser chrome and ivory `#f6f5f2` for `prefers-color-scheme: dark`.
Browser chrome follows the browser/system preference independently of the app's theme
selector. Check the icon at 16, 24, and 32px on both backgrounds.

### Stag and wordmark

**Role:** recognizable brand anchor inside the panel.

Use [stag-mark.svg](src/Workbench.Client/public/stag-mark.svg), preserving its angular
antlers, geometric face, and eight-point star. The asset contains the original supplied
vector paths with the source wordmark and Illustrator metadata removed. It is black
by default and displayed with `filter: invert(1)` in dark appearance.

Center the mark above `Workbench`. Directly below the name, render
`by The White Stag Collection` at 13px in the byline color. Keep the attribution quiet
but readable. Use an empty image alt attribute when the adjacent text already identifies
the brand, as on sign-in. Do not replace the mark with a generic deer, a filled silhouette,
or a generated approximation.

### Glass sign-in panel

**Role:** a single focused entry point.

Center the brand block; left-align the heading, supporting copy, labels, and recovery
link. The order is brand, byline, heading, explanation, email, password, primary action,
and recovery. An error appears before the primary action when needed.

### Primary action

**Role:** the clearest action in the composition.

Use neutral fill and inverse text, a 6px radius, and a 60px minimum height. The button
spans the form width. During submission, keep the existing disabled state and the label
`Signing in…`. Preserve its submit semantics.

### Text input

**Role:** a readable neutral surface with a floating label.

Use `FloatingField` around one native input or textarea, with `htmlFor` matching the
control's `id`. A sibling label keeps textarea content out of its accessible name. The label rests inside an empty
field and rises into the top border on focus, autofill, or a nonempty value. A focused
field has a single 2px `--focus` border with compensated padding, no outer outline or
shadow ring, and 6px corners. Invalid fields keep `--danger`. Preserve email/password
types, validation, and autocomplete. Provide `placeholder=" "` when there is no hint;
example hints appear on focus. A placeholder never replaces the accessible label.

Entered text remains 16px. Sign-in keeps its 62px minimum input height; workspace
controls retain their existing target sizes. The input and label notch share an opaque
white or `#121317` fill. Fields at most 16rem wide use external, wrapping labels;
the threshold scales with root text size so enlarged text cannot overlap values.
Label motion lasts 140ms and respects reduced motion. Text color changes immediately
with appearance to preserve contrast throughout theme changes. See the
[field specification](docs/specs/2026-09-08-floating-label-fields.md).

### Recovery link

**Role:** a quiet secondary route.

Use muted underlined text with `text-underline-offset: 0.2em` and a minimum 44px hit
height. Keep the label `Forgot your password?` and the destination `/recover`.

### Appearance control

**Role:** retain user control of light, dark, and system appearance.

Keep the existing labeled select in the top-right appearance bar. Reserve space above
the panel for it. Appearance changes preserve form input and use the existing preference
mechanism; this visual treatment introduces no additional preference setting.

## Layout

The shell uses CSS Grid, `place-items: center`, and `min-height: 100svh`. Desktop padding
is `6rem 1.5rem 3rem`; at 600px and below it becomes `5.5rem 1rem 2rem`. The asymmetric
vertical padding leaves room for the appearance control above the centered composition.

At narrow widths, retain the single-column form and scale the brand modestly. At short
heights, allow document scrolling so the button, error, and recovery link remain reachable.
Do not fix the card height or clip overflowing content to preserve a screenshot silhouette.

## Motion & Interaction

- Primary button hover: lift by 1px and soften the shadow over `160ms ease`.
- Primary button press: move down by 1px.
- Disabled buttons do not receive the hover or press transform.
- With `prefers-reduced-motion: reduce`, remove the transition and both transforms.
- Links and buttons use a 3px keyboard-focus outline in `--focus`, offset by 3px.
  Text fields use a single 2px focus border; the appearance selector changes border color.
- The implemented background is static. There is no ambient drift, parallax, or glow animation.
- Preserve native validation, pending feedback, and the existing generic sign-in error.

## Imagery

The sign-in page uses the original vector stag and the inline bench-pin wordmark.
Its atmosphere is CSS, so there is no raster background to load, crop, or scale. The implementation must remain live
HTML controls rather than a flattened image of the approved concept.

If this language is later extended to the collection, preserve accurate item photography
and existing domain interactions. Atmospheric tints should never alter how a gemstone
or jewelry photograph represents the item. That extension requires its own scoped design.

## Do's and Don'ts

### Do

- Keep the page overwhelmingly neutral, with atmospheric color confined to the top.
- Use the original stag at a clearly visible size and keep the byline subordinate to Workbench.
- Create depth through material, highlights, and soft shadows.
- Maintain crisp text and visible control boundaries over translucent surfaces.
- Preserve both appearances, keyboard operation, validation, and reduced-motion behavior.
- Reuse the actual selectors and semantic tokens when working on the existing sign-in surface.

### Don't

- Fill the bottom of the page with blue, violet, pink, or bronze.
- Use a saturated primary button, a colored card outline, or decorative gradient lettering.
- Restore the serif sign-in wordmark or shrink the stag into a barely visible header icon.
- Add ornaments, floating orbs, badges, social-login options, or marketing copy to this form.
- Make every element glass or remove borders simply to make the form look softer.
- Apply these scoped tokens globally or silently replace the rest of the application's styling.

## Agent Prompt Guide

### Reusable design brief

> Design the requested Workbench surface using the Tanzanite reference. Start with a
> neutral black or white canvas and restrained translucent glass. Keep blue/violet light
> faint and near the top edge; leave the lower background neutral. Use clean system
> sans-serif typography, soft neutral shadows, and clear control boundaries. Preserve
> the original geometric stag and star. Where the sign-in brand lockup is used, place
> “by The White Stag Collection” directly beneath Workbench in smaller muted gray text.
> Keep primary actions neutral with inverse text. Preserve the requested workflow and
> existing accessible interactions. Do not invent extra UI or expand the restyle beyond
> the authorized surface. Use the actual source tokens and verify both appearances and
> narrow layouts in the browser.

### Example component briefs

1. **Sign-in panel:** Center a 580px maximum-width glass panel with a 12px radius,
   40px vertical and 48px horizontal padding. Place a 112 × 150px stag image box above
   the 44px semibold bench-pin Workbench wordmark and 13px gray byline.
   Left-align the form beneath it.
2. **Dark atmosphere:** Use `#08090c` as the canvas. Add the documented blue and violet
   radial layers above the viewport. Keep the lower page virtually black and the card neutral.
3. **Primary action:** Create a full-width, minimum 60px-high button with a 6px radius,
   18px/500 text, neutral inverse colors, and subtle 1px hover lift. Respect reduced motion.
4. **Mobile adaptation:** At 600px and below, use 16px outer horizontal padding and
   32px/24px panel padding. Reduce the mark to 96 × 128px and reduce the wordmark to 38px. Keep
   the original labels, control heights, and recovery route; allow vertical scrolling.

## Quick Start

### Existing Workbench implementation

The application already imports these styles in this order:

```tsx
import './styles.css';
import './sign-in.css';
import './floating-field.css';
```

The imports above are relative to
[main.tsx](src/Workbench.Client/src/main.tsx). Use the existing
[SignIn component](src/Workbench.Client/src/features/auth/SignIn.tsx) within its
`.sign-in-page` and `.sign-in-shell` wrappers. The base stylesheet supplies shared
typography, validation, focus, and control rules; `sign-in.css` supplies scoped overrides.
Import `floating-field.css` last for the shared text-field interaction.
Changing the root `data-theme` attribute remains the responsibility of the existing
appearance control.

### CSS atmosphere recipe

This excerpt uses the exact current dark-mode values. It illustrates the layering;
it is not a standalone replacement for the complete component stylesheet.

```css
:root[data-theme="dark"] .sign-in-page {
  --canvas: #08090c;
  background:
    radial-gradient(ellipse 65% 32rem at 10% -12rem, #263a7280, transparent),
    radial-gradient(ellipse 55% 28rem at 95% -12rem, #43265360, transparent),
    var(--canvas);
}

:root[data-theme="dark"] .sign-in-card {
  background: linear-gradient(155deg, #ffffff0b, #ffffff00 35%), #101114b8;
  box-shadow:
    inset 0 1px 0 #ffffff75,
    0 8px 24px #00000030,
    0 32px 80px #00000070;
}
```

Workbench currently uses plain CSS and React. No Tailwind preset, additional component
library, or separate font installation is needed to reproduce this reference.

## Source of Truth

- [Wordmark component](src/Workbench.Client/src/Wordmark.tsx) and
  [favicon](src/Workbench.Client/public/favicon.svg): shared bench-pin geometry and
  accessible product lettering.
- [Sign-in styles](src/Workbench.Client/src/sign-in.css): scoped colors, materials,
  dimensions, responsive rules, and motion.
- [Base styles](src/Workbench.Client/src/styles.css): inherited typography, controls,
  focus, and error treatment.
- [Sign-in component](src/Workbench.Client/src/features/auth/SignIn.tsx): brand, copy,
  form semantics, pending/error feedback, and recovery link.
- [Application shell](src/Workbench.Client/src/App.tsx): signed-out composition.
- [Accepted sign-in specification](docs/specs/2026-09-08-tanzanite-sign-in.md): approval
  scope and implementation requirements.
- [Design principles](docs/DESIGN-PRINCIPLES.md): broader product and engineering guidance.

Keep this guide synchronized with approved implementation changes. It documents visual
language; it does not replace product requirements, authorization rules, or the repository's
implementation and review workflow.

## Application surfaces

The shared stylesheet implements Tanzanite throughout the existing workspace. Reading,
editing, and photographic surfaces stay opaque; the sticky header and public account
cards use restrained glass. Layout, route structure, permissions, and workflows are unchanged.

| Shared token | Dark | Light |
|---|---|---|
| `--surface` | `#121317` | `#ffffff` |
| `--surface-raised` | `#1b1c22` | `#ffffff` |
| `--border-subtle` | `#34363f` | `#d9dbe3` |
| `--selected` | `#252730` | `#edf0f6` |
| `--hover` | `#202229` | `#f3f4f7` |
| `--placeholder` | `#1b1c22` | `#f4f5f7` |
| `--header-material` | `rgb(18 19 23 / 88%)` | `rgb(255 255 255 / 88%)` |

Shared radius tokens are `--radius-small: 2px`, `--radius-badge: 4px`,
`--radius-input: 6px`, `--radius-button: 6px`, and `--radius-card: 12px`.
Small and badge tokens are reserved for future components. Existing card, list, panel,
editor, and dialog corners use 12px; controls and navigation links use 6px. Grid photo
corners are inset by their 1px container border.

The compact shared `Brand` uses the bench-pin Workbench wordmark at 20px in both
desktop and mobile layouts. The stag and maker attribution appear only in the larger
sign-in lockup described above. Standard workspace controls retain their
44px minimum target; the 60px/62px sign-in dimensions above are specific to that form.

Card elevation uses subtle neutral shadows in both appearances. Never tint photographs
or use colored fills to imply inventory state. Reduced transparency falls back to opaque
chrome; reduced motion disables active translations. Forced colors retains native control
boundaries and an explicit active navigation outline.

See the [application extension specification](docs/specs/2026-09-08-tanzanite-app.md).
