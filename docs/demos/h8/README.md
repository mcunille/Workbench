# H8 narrated collection package walkthrough

[Transcript](transcript.md) · [Captions](captions.srt)

The walkthrough uses synthetic records and real uploaded photographs against the API and disposable
SQL. It demonstrates the CSV/ZIP choice, explicit record scopes, stored photograph mapping, an
explicit no-photo record, archived inclusion, navigation and theme retention, an injected preparation
failure and safe retry. The final view displays the actual downloaded manifest and image bytes.
Authentication occurs off camera. Automated checks do not establish collector usability.

For capture commands, rendering prerequisites and media policy, use the
[walkthrough index](../README.md#capture-and-rendering-prerequisites).

The separate `package.spec.ts` regressions independently parse ZIP entries and validate CSV/manifest
mapping, photo length/digest and exact stored detail bytes. They exercise both scopes, photo absence,
keyboard operation, light/dark desktop and 320px layouts, reduced-motion/transparency preferences,
navigation/reload behavior, partial responses, cancellation and retry.

See the [verification record](verification.md) for completed gates and coverage limits.
