# H7 narrated walkthrough

[Transcript](transcript.md) · [Captions](captions.srt)

The walkthrough demonstrates explicit export scope, search independence, complete preparation,
a real CSV download, reversible spreadsheet protection, navigation and appearance retention,
a deliberately interrupted request, and a new snapshot including archived records on retry.
It uses synthetic text records against the real API and disposable SQL. Authentication occurs off camera.
The network interruption is injected by Playwright; successful downloads use the real server.

For capture commands, rendering prerequisites and media policy, use the
[walkthrough index](../README.md#capture-and-rendering-prerequisites).

The separate `export.spec.ts` regressions cover both scopes, more than 50 seeded active records,
archived exclusion/inclusion, independently parsed CSV text, search independence, keyboard selection,
320px and desktop layouts, both appearances, reduced motion/transparency, real mobile downloads,
navigation retention, reload clearing, and injected empty/failure/disconnection outcomes with retry, plus cancellation of a held complete response.
Server and client unit/integration tests cover limits, tenant isolation, consistency, cancellation,
session loss, and in-memory expiry separately. Automated evidence does not establish collector usability.

See the [verification record](verification.md) for complete gate results and coverage limits.
