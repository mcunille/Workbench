# H11 verification record

Verified on 2026-09-11 against the H11 source on `codex/acquisition-documents`, based on
`07f2714` (the merged H10 implementation). Media uses synthetic collector records and a small
generated PNG document. This record describes local evidence, not production deployment.

## Automated evidence

- `./scripts/verify.ps1 -SkipDependencyInstall` passed: 753 server tests in two isolated SQL
  partitions, 218 client tests across 41 files, 75 browser tests, formatting, locked restore,
  Release build, generated API drift, client lint/typecheck/build and published release checks.
  Local gate ID: `b826d83c5efb4e0cb14ba2d57c4e5296`; elapsed 728.94 seconds.
- `./scripts/smoke-container.ps1` passed with a freshly built Linux runtime: non-root process,
  migrations, restricted principals, SQL readiness, TLS/secure-cookie login, session persistence
  across replacement, private listener, forwarding checks and worker telemetry.
- `dotnet ef migrations has-pending-model-changes --project src/Workbench.Server --configuration Release --no-build`
  confirmed the current built model matches the single H11 migration. Server tests cover clean
  creation, upgrades retaining earlier data, restrictive grants, guarded down migration, paired
  recovery and explicit unavailable-document disposition.
- Independent SQL sessions exercise duplicate request serialization, pending-capacity competition,
  archive/unlink races after publication, and rename/removal races. HTTP tests cover safe original
  downloads, foreign-tenant identifiers, retained archived access and immutable replay.
- Focused manual mutations were detected for changed client retry UUID, resubmission of completed
  uploads, archived upload controls and PDF duplicate-key validation (four rejecting fixtures).
  These are bounded checks, not an exhaustive mutation score. The temporary SQL-capacity mutation
  output was not retained reliably and is not counted as evidence; the original guard was restored
  before the full passing server run.

## Visual evidence

The narrated walkthrough passed separately at `http://127.0.0.1:5397` using disposable SQL. It
demonstrates response loss after commit, status-first retry with exactly one stored document,
download and a checked label correction at 320px in dark appearance. The capture is 50 seconds;
Windows System.Speech narration and captions were rendered with local `imageio-ffmpeg==0.6.0`.
The complete MP4 decoded successfully; representative desktop/mobile frames were inspected for
legible controls, wrapping and captions. The shared receipt-fixture extraction was followed by
a focused browser rerun. Only documentation, shared browser fixtures and trailing whitespace changed after the full gate.

Media files are external: `h11-acquisition-documents.mp4`, `documents-desktop.png` and
`documents-mobile.png`. The PR carries attachments; no generated media is stored in Git.

## Limits

`./scripts/dev-up.ps1 -Json` could not secure the checkout's Windows directory ACL, including
an escalated attempt. It stopped before generating credentials. There is no retained interactive
preview URL; disposable browser and container verification above completed successfully and their
temporary environments were removed. No permissions were weakened to bypass the launcher.

PDF acceptance is the documented restricted subset, not universal PDF compatibility or malware
certification. Validation is bounded and cooperative, not a hard parser isolation boundary. Broader
mutation testing, an independent parser audit, public CA issuance, production SMTP delivery and
uncoached collector usability are not established by these checks.
