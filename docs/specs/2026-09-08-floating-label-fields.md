# Floating-label fields

Status: approved by the request to adopt the label-input interaction shown at
https://spell.sh/docs/label-input and in the supplied screenshots.

Text, email, password, search, and multiline fields start with their label inside the
empty control. Focus moves the label into a notch in the top border; a nonempty or
autofilled value keeps it raised after blur. Clearing and blurring restores the resting
label. Focus uses one 2px Tanzanite-colored border, with padding compensation to avoid
layout movement, rather than an additional outside ring. Corners remain 6px.

Use native controls and explicitly associated sibling labels in a shared wrapper. Preserve
names, IDs, validation descriptions, autocomplete, controlled/uncontrolled values,
disabled states, and submission behavior. Placeholder examples appear on focus.
Keep file upload and appearance labels external; appearance focus also uses a single
colored boundary. Links and buttons retain their keyboard focus outlines.

At field widths of 16rem or less, labels stay above controls in normal flow and may
wrap. This text-relative fallback also applies when root text is enlarged, preserving
the full label without overlap in empty, focused, and populated states.

Motion is CSS-only and disabled for reduced-motion users. Label color changes immediately
with the theme so text and background never animate out of contrast. Forced colors uses a system
highlight border. Invalid fields retain their danger color. A neutral opaque field fill
provides a clean notch against the surrounding glass.

Verify empty/focus/filled/cleared states and label click behavior in both appearances,
plus existing workflow, validation, responsive, and accessibility browser coverage.
