# Floating-label fields

Status: approved by the request to adopt the label-input interaction shown at
https://spell.sh/docs/label-input and in the supplied screenshots.

Text, email, password, search, and multiline fields start with their label inside the
empty control. Focus moves the label into a notch in the top border; a nonempty or
autofilled value keeps it raised after blur. Clearing and blurring restores the resting
label. Focus uses one 2px Tanzanite-colored border, with padding compensation to avoid
layout movement, rather than an additional outside ring. Corners remain 6px.

Use the existing native controls inside a shared accessible label wrapper. Preserve
names, IDs, validation descriptions, autocomplete, controlled/uncontrolled values,
disabled states, and submission behavior. Placeholder examples appear on focus.
Keep file upload and appearance labels external; appearance focus also uses a single
colored boundary. Links and buttons retain their keyboard focus outlines.

Motion is CSS-only and disabled for reduced-motion users. Forced colors uses a system
highlight border. Invalid fields retain their danger color. A neutral opaque field fill
provides a clean notch against the surrounding glass.

Verify empty/focus/filled/cleared states and label click behavior in both appearances,
plus existing workflow, validation, responsive, and accessibility browser coverage.
