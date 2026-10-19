# BK-07 feedback verification — 2026-10-11

The approved PR #215 feedback changes passed independent internal review and local verification.
Remote verification of this delivery remains pending. This record does not authorize merging,
production rollout or bookkeeping activation.

## Changes and first-principles evidence

- Evidence lookup now binds owner kind, owner ID and revision, including reversal and recognition reads.
- Disposal conflict review restores the editable reason and requires fresh versions, a new request ID and explicit confirmation.
- Runtime SQL text patching and its 20 calls were deleted. The release installs 18 frozen final procedure definitions.
  Two independent comparisons found all 18 equivalent to the original migrated definitions after header/newline normalization.
  Merged migrations and required generated artifacts remain unchanged. The direct upgrade predecessor is GEM-06.
- Deleting listing coordination produced an actual SQL deadlock during genuine concurrent listing and evidence append.
  The lock was restored; a real concurrency regression replaces the old artificial lock-wait assertion.

## Completed local verification

`scripts/verify.ps1 -SkipDependencyInstall -ServerConcurrency 1` run
`d5fa47ed5c944133ab61fd39c32e33ee` completed with exit 0 in 11,125.974 seconds.
Both partitions completed with exact inventory coverage: **2,095/2,095 server cases**, no failures or skips.
This includes fresh schema creation and authentic seeded GEM-06 upgrades, correction lineage and financial preservation.
Client lint, **596/596 client tests**, formatting, current-source Release build, generated API drift,
typechecking, client build, **132/132 browser tests** and published release-unit checks passed.
The sequential partition setting is not a performance acceptance claim. No case deadline was increased.

Verified working-tree source SHA-256:
`B80CB73B2D0A1A28DBE1909BF9DA70492EED1CF0E82B781452B718F947E6CBCD`.
It matched after the gate. Subsequent changes are this record, spec/index status links, and whitespace-only cleanup on three blank SQL lines.
The tested runtime changes are based on `80d841defb7a03fcd4afbbafae3d9f7206e25220`.

Focused SQL regressions passed 5/5. Removing only the owner-kind predicate reproduced HTTP 500 and killed that manual mutation.
Four focused client mutations were also killed; production source was restored before the full gate.
These are manual mutation probes, not an automated mutation score.
Earlier focused bill-read timeouts are superseded by its full-gate pass in 17.333 seconds.

`scripts/smoke-container.ps1 -ComposeIngressPrefix 172.30` passed with current-source builds,
fresh SQL migration/provisioning, hardened runtime, internal TLS, secure login, durable sessions,
forwarding headers and worker telemetry. Public CA issuance, SMTP delivery and hosted recovery remain unverified.

## Preview inspection

The preserved checkout preview was refreshed at `http://localhost:32775` with source changed=False.
At 1280px and 390px, actual native dialogs preserved an editable reason, focused the textarea,
required explicit submission with fresh versions/new request ID, and had no horizontal overflow.
Purchase and document responses were synthetic intercepted data; no real purchase or disposal changed.
This proves the scoped UI behavior, not SQL persistence. The full SQL/browser suites own persistence evidence.
Screenshots remain outside Git history in the task's visualization directory.
