# CI verification

Hosted CI runs the source/release checks and two server jobs in parallel. Each server job
builds from the event checkout and runs two of four whole-class cohorts, with at most two
SQL workers per host. The named required gate joins the source/release receipt and both
server artifacts. It checks checkout and source identity, the duration dataset, the exact
inventory, and passing results for every cohort.

Run `./scripts/verify.ps1` for complete local verification. The hosted
`-StageGroup SourceRelease` invocation covers only client, browser, and published-output
stages and relies on the required gate to join the remaining evidence.

The gate discovers the current built inventory and assigns whole classes, including every theory
row, to independent processes with sequential collections and disposable SQL fixtures. Missing,
extra, duplicate, skipped or failed results and missing completion receipts fail the gate.
Discovery preserves exact UTF-8 test identities; trimming or filtering unsupported names must
not silently reduce coverage. Shared build-output writes are serialized; concurrent consumers
use read-only release artifacts.

Run-specific build receipts and manifests bind repository root, run ID, Release configuration,
current tracked/nonignored source hashes and published hashes. Generated API files are excluded
only from the pre-build receipt, checked for drift and included in the final manifest. Existence
alone cannot authorize reuse; standalone browser/publish runs create fresh artifacts. Consumers
must not delete shared outputs. Native child stdout/stderr use separate temporary logs so they
cannot corrupt PowerShell background-job transport; cleanup follows process shutdown.

## Refresh server timing data

The reviewed `scripts/server-test-durations.json` dataset predicts class cost by summing historical
case durations. Assignment takes longest classes first into the lowest predicted total, with
ordinal class-name and partition-ID ties. All methods sharing a class fixture stay in one process;
fixture preparation remains outside case deadlines. New rows receive one second each; obsolete rows do not
affect assignment. This fallback permits new tests but cannot predict their actual cost. Invalid
datasets fail closed. Inventory artifacts retain predictions/fallback counts and dataset provenance.

Refresh after substantial test changes or observed imbalance, outside a running gate. Use only a
completed successful CI **push to main** in this repository. Verify event, branch, conclusion and
full SHA with `gh run view` before downloading `server-timing-evidence`; use a successful main run.
For older single-runner main runs, use the original `verification-evidence` artifact.
The importer validates gate success, inventory, exit receipts, exact passing TRX coverage and finite
nonnegative durations, prohibits XML DTDs, and never executes artifacts. It cannot authenticate
local downloaded files: provenance parameters describe evidence the operator verified.

```powershell
./scripts/update-server-test-durations.ps1 -EvidenceDirectory <gate-directory> `
    -SourceRunUrl https://github.com/mcunille/Workbench/actions/runs/<run-id> `
    -SourceRevision <full-commit-sha> -OutputPath scripts/server-test-durations.json
./tests/Workbench.BuildTests/ServerDurations.Tests.ps1
./tests/Workbench.BuildTests/ServerPartitions.Tests.ps1
```

Review the data and provenance diff together. A controlled `-TimingDataPath` comparison must retain
partition count, concurrency, inventory and runner class. Predicted balance excludes fixture startup
and runner pressure and does not guarantee critical-path savings. Retain resource costs alongside
wall time; more concurrent SQL fixtures are not free capacity.
