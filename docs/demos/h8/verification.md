# H8 verification record

Implementation follows the owner-approved package design for issue #56. The package endpoint retains
H7 CSV behavior and introduces no database migration, durable export job or server download identifier.

## Focused evidence

- Tests were written first: missing endpoint/ZIP behavior and unsupported identifier requests failed
  before implementation. Corrupt content exposed an incorrect 500 mapping, corrected to the promised
  503 recovery outcome. An injected Azure credential failure reproduced the same mapping gap and was
  corrected with a focused failure-and-retry test. A resource test exposed MemoryStream capacity growth
  above its ceiling before the capacity clamp was added.
- Archive tests verify exact stored bytes and SHA-256, CSV compatibility, literal manifest mapping,
  explicit absence, Unicode/path-like text, row/per-photo/CSV/manifest/aggregate/final-directory limits,
  exact buffer capacity, cancellation and stream disposal. All 18 pass on current source.
- HTTP and real-SQL tests cover both scopes beyond one page, archived and foreign records/photos,
  authentication/antiforgery, strict scope-only requests, incomplete/corrupt/missing/recovery-unavailable
  photographs, retries, shared capacity, SQL failures and session revocation.
- Independent SQL connections prove export snapshots during insert/edit/archive/restore. Photo
  replacement/removal demonstrably blocks while capture holds SQL locks, then commits after release.
  Independent HTTP sessions also replace/remove a photo after capture while blob copying is held; export retains exact
  captured bytes, and an actual worker refuses premature purge under seven-day retention. A real
  120-second deadline and client cancellation release capacity while SQL remains blocked.
- 48 focused client tests, typecheck and lint passed. Seven focused CSV/ZIP browser scenarios passed
  at `http://127.0.0.1:5286` against current client assets and disposable SQL. Downloaded ZIPs are parsed
  independently and photo bytes compared to authenticated stored detail responses. Desktop and 320px
  light/dark screenshots were inspected; keyboard, reduced motion/transparency, navigation, reload,
  incomplete response, cancellation and retry checks passed.
- Final container smoke passed at `http://127.0.0.1:61077`: non-root read-only SQL-backed runtime,
  private listener, internal TLS, cookie login, durable sessions across app replacement, forwarded
  client isolation and worker telemetry. The disposable environment was removed afterward.

## Mutation evidence and limits

Stryker.NET 4.16.0 ran against an isolated source copy, with SHA-256 equality established for the two
production files and test file. Its initial archive/buffer report was 75.00%: 55 killed, nine survived,
two timed out and ten uncovered mutants. Compiler limitations excluded 61 archive mutants, including
`EncodeAsync`; the score does not establish mutation coverage for that method.

Three supplemental manual mutations were killed: bypassed byte integrity verification, removed
per-photo size enforcement, and omitted photograph entries. The Stryker survivors revealed two
assertion gaps: equal photo/absence counts hid an inverted count predicate, and the buffer test did
not write exactly at the byte ceiling. Both assertions were strengthened, both mutations then killed,
and all 18 archive tests passed after restoration. The original Stryker score was not recalculated.

Other survivors concern redundant guards, a cancellation poll, per-item serializer flush timing and
allocation growth choices that remain within the ceiling. Uncovered stream overloads and the SQL,
authorization and retention layers were not covered by this focused Stryker run. Real-SQL/HTTP tests
supply regression evidence for those boundaries. Three focused client manual mutations were killed:
an incorrect ZIP size ceiling, skipped body-completeness checking, and ignored format selection.

Deliberately corrupt ownership metadata is not a separate H8 fixture; existing relational ownership
constraints and restricted-SQL isolation tests cover the related boundaries. H8 package scenarios use
filesystem/test providers; provider portability tests do not establish live Azure managed-identity/RBAC configuration. No collector usability study was performed.

## Full gate and media

`scripts/verify.ps1 -SkipDependencyInstall` passed on the integrated revision with SDK 10.0.401:
locked restores, formatting, generated OpenAPI drift, Release build with no warnings/errors,
599 server tests (including all 38 package-specific tests and migration/recovery drills), 149 client
tests, lint/typecheck/build, all 53 browser tests and published release-unit probes at
`http://127.0.0.1:64193`. Dependencies had already been installed from their lockfiles.
Independent implementation review of the complete feature range and subsequent upstream integration
reported no actionable findings.

The narrated Playwright capture used an isolated copy of the integrated checkout (implementation revision `a14b536`) with SDK
10.0.401; all 525 tracked files were verified equal to the delivery checkout by SHA-256 before building. It passed at `http://127.0.0.1:5287/inventory/export`, including actual
ZIP downloads and an offline display of extracted manifest/README text and decoded WebP bytes.
The 117.56-second H.264/AAC MP4 rendered and decoded successfully. Desktop, mobile, offline manifest/photo
and README frames were inspected. Audio is non-silent (mean -21.0 dB, maximum -2.2 dB); narration fits
each presentation hold. Transcript and captions are committed here; the 3.8 MB MP4, extracted ZIPs,
frames and logs remain ignored under `artifacts/h8/walkthrough/`. Disposable applications were stopped
and their SQL containers and temporary data removed after verification.
