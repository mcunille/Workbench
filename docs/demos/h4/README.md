# H4 narrated walkthrough

[Transcript](transcript.md) · [Captions](captions.srt)

The recorded video is an external artifact, shared separately from Git. The walkthrough uses real API calls and disposable SQL,
synthetic collection text, and Microsoft Zira synthetic narration. Authentication happens off camera.

The scenes show canceling an edit, a failed save and safe retry on mobile, appearance changes,
a conflict between independently signed-in sessions, explicit reconciliation, and updated search
results after reload. The injected 503 models a server failure; the conflict uses actual saved data.
Separate browser regressions exercise loss of a committed save response, narrow conflict comparison,
keyboard focus, text contrast, and 320-pixel layouts.

## Verification

The narrated Playwright scenario passed against disposable SQL. The roughly 76-second MP4
was rendered with captions and narration, then decoded completely without media errors.
Desktop, mobile retry, conflict comparison, and reconciliation frames were visually inspected.

H4 delivery also passed `scripts/verify.ps1 -SkipDependencyInstall` (423 server tests, 73 client
tests, 25 browser tests, migration drills, generated contracts, builds, and published-output
probes) and `scripts/smoke-container.ps1`. A final focused browser run passed all three H4 cases.
Four targeted manual mutants were detected: SQL version predicate, client checked token,
stale reconciliation initialization, and late cache invalidation. No automated mutation score
is claimed. These are local verification results; production acceptance is separate.
