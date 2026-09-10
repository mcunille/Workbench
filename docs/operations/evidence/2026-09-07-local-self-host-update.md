# Local self-host update verification

Verified on 2026-09-07 using Windows, PowerShell 7.4+, and Docker Desktop Linux containers.
Both installations were newly created isolated QA fixtures with unique roots, loopback
ports, generated credentials, and trusted local Caddy certificates. No retained user
installation was updated. Scripts were exercised from the implementation source;
application images were freshly built from the selected committed revisions.

| Scenario | Evidence |
| --- | --- |
| Same-release update | Application revision `9b62e0b51690dbfae5bbf3795a26cc2c3fbe31f2` updated to itself; trusted HTTPS readiness at `https://localhost:18487` returned 200. |
| Actual schema upgrade | Application baseline `f497fc4` upgraded to `9b62e0b51690dbfae5bbf3795a26cc2c3fbe31f2`; SQL advanced from `20260907054000_AddProviderRetryDelay` to `20260907060000_AddCollectionNotebook` using the restricted migrator credential. HTTPS readiness at `https://localhost:18488` returned 200. |
| Retained data and sessions | Both fixtures preserved an authenticated session across update and passed fresh sign-in, item read, and sign-out. Same-release verification retained a previously created inventory item; prior-schema verification created an item after migration. |
| Nonempty checkpoints | Each fixture contained one 66-byte retained blob with SQL metadata. SQL COPY_ONLY/CHECKSUM and VERIFYONLY passed; exported snapshot bytes matched the manifest SHA-256. The prior-schema checkpoint retained its original schema identity. |
| Failure containment | Failed checkpoint attempts left proxy/app/worker stopped and did not migrate. Recovery in these disposable fixtures followed inspection of the recorded checkpoint phase and unchanged schema before restarting the old workloads. |

The nonempty drill exposed `renameat2(RENAME_NOREPLACE)` returning EINVAL on a Windows
bind mount. The final updater snapshots onto a Linux named volume, exports to Windows,
and verifies each exported blob before accepting its checkpoint. Both live scenarios
passed with that implementation.

Automated checks cover setup configuration/orchestration, update phase ordering and
injected failures, 25 installation validation scenarios plus operation-lock/confirmation
and running-configuration hash checks, and 15 checkpoint cases. Checkpoint cases include
missing, truncated, and same-length corrupted exports. Six targeted scratch mutations
were detected: removal of checkpoint invocation, offline validation, empty-backup
rejection, manifest identity validation, blob-directory creation, and exported-blob
integrity verification. This is targeted mutation evidence, not a whole-program score.
Azure parameter, Compose topology, and verification-script command-boundary checks passed.

The live checks used trusted HTTPS requests, not browser UI automation. This record
does not claim a paired restore drill, off-host disaster recovery, public production
acceptance, certificate rotation, dependency-image upgrades, or the full application
test suite. The SQL/blob/key recovery runbook still governs restoration. QA URLs are
temporary test endpoints, not a deployment of the user's installation.
