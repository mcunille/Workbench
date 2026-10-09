# BK-07 verification checkpoint — 2026-10-09

**Release verification FAILED.** Implementation, latest-main integration and scoped independent
internal review are complete. The completed local gate has 17 unresolved server failures; the
same-revision remote gate has one unresolved browser failure. Passing stages and the diagnostic
pair below do not replace either failed gate. This is a checkpoint, not delivery acceptance or
authorization to merge, activate bookkeeping or deploy to production.

## Revision and fresh-source provenance

All final gate, smoke and preview claims below concern
`f3a718f30f13f4aaa6dfaf1a4c45005cd285ca67`. Its merge parents are Task6 checkpoint
`b838402dd165291c5528204bd5c5df838bee97c1` and exact main
`494ebf6b14d162727d0fe37d8f6b0022bd96a322`. The one content conflict was purchasing
documentation; no source/schema conflict occurred. Main's retained PO-07 parent spec was preserved.
The sole BK-07 migration remains `20261008010000_AddFinancialEvidenceRetention`, after merged
`20261003214043_AddTenantGemReference`; no merged migration was rewritten.

Local verification used task-local Node 26.7.0/npm 11.19.0, locked .NET restore and both locked
`npm ci` installations, including the changed client lockfile. The official Node archive checksum
was verified; global installations and repository pins were unchanged. Format, fresh Release build
(zero warnings/errors), generated OpenAPI drift, client typecheck and client build passed.
The published verification manifest records:

| Input/output | SHA-256 |
| --- | --- |
| Source | `2E3D2428842CDD8FBC5975DDDF2A7C65676CC38D8A40B13AE194EB560D1FAA91` |
| Published server | `CB7F0DF4EF55A620C1AB12CBA8ABAF324AA22A3160CAB2BE78351A5682DE22E5` |
| Published database tool | `724358E79E82252DB6393D1272167F491DBD5D102CCC5FD29D888B258FBCCBB7` |

## Completed local gate

`scripts/verify.ps1` run `cdda7988d9224affb2772fc8c7ea1eaa` completed normally with exit 1,
using two server partitions and two concurrent SQL containers. Total wall time was 6,351.570s;
prerequisites took 312.990s. No case or coordination-observation deadline was increased.

| Stage | Outcome | Measured stage/process seconds |
| --- | --- | ---: |
| Client | 593/593 passed in 88 files | 262.373 |
| Published release unit | Passed | 5.582 |
| Browser | 132/132 passed | 246.008 |
| Server, complete inventory | 2,074 passed / 17 failed / 0 not executed of 2,091 | 6,075.249 |
| Server partition 1 | 1,035 passed / 10 failed of 1,045 | 6,067.967 |
| Server partition 2 | 1,039 passed / 7 failed of 1,046 | 5,640.562 |

Raw receipts are retained outside the temporary execution workspace under
`artifacts/verification/cdda7988d9224affb2772fc8c7ea1eaa/`: `gate.json`, `stages.json`,
`verification-manifest.json`, `full-gate-console.log`, `final-results-summary.json`, and complete
TRX/timing/inventory/discovery files under
`server-tests/4c60ab7c1581416690327dd9b84bfa75/partition-{1,2}/`.
The summary preserves every failed name, start/end time, message and stack.

The first receipt-fault case and coordination/application=true case started 0.431s apart in
independent partitions. Failure completion ordering was coordination=true at 01:18:21.578 PDT,
coordination=false at 01:18:32.113, then receipt rollback at 01:18:33.430. Both coordination
stacks point to the seven-second DMV observation loop at `SupplierPaymentCorrectionTests.cs:226`:
one cancellation and one aborted/busy SQL batch. They precede the later document-lock probes;
the raw failures do not record the exact waiter/blocker state. The other 15 failures report the
existing 30,000ms test timeout. Their TRX wall spans are about 30s despite reported durations of
0.001s. Later correction timeouts cluster through 01:27:44; they are not assumed to be independent
domain defects or a proven cleanup cascade.

Read-only runtime snapshots at 01:29 and 02:15 show live process/fixture activity and no blocked
chain at those instants. Both are after the failure window. They do not identify its cause,
exclude transient blocking, or establish CPU/compile/IO pressure. No production cause, uniform
host slowdown, stale-binary problem or permanent suite hang was established. The failures are
not classified as unrelated, environmental or flaky.

## Remote gate and bounded follow-up

The [same-revision CI run](https://github.com/mcunille/Workbench/actions/runs/37902228596)
also **failed**: server 2,091/2,091 passed, client and published stages passed, browser 131/132
passed. Gate `b997c55ad6194e3aa6e45636e751a1c3` took 3,541.986s overall and 3,322.690s
for its server stage. The sole browser failure was `acquisition-documents.spec.ts:43`: after
Save label, `Download Fair supporting record` was absent at the five-second assertion deadline.
The retained geometry showed the label form, but did not establish the cause. All BK-07 browser
owners passed there. Remote receipts remain in `artifacts/bk07-remote-final-37902228596/`.
All 17 locally failing cases passed remotely; neither result erases the other's failed gate.

A single two-owner diagnostic ran only the earliest receipt-fault and coordination=true owners,
with authentic prerequisites and unchanged deadlines. Their bodies started 95ms apart after a
temporary readiness barrier. Both passed: reported bodies 17.654s and 6.878s; pair process
231.188s. The unchanged coordination poll observed the required held lock 4.077s after preview
started; preview, receipt rollback/retry, assertions and cleanup completed. No timeout or
overlapping unfinished body was reproduced.

Both numeric samplers failed: the in-process reader had an integer/smallint mismatch, and external
`sqlcmd` rejected incompatible options before executing. There is no numeric CPU/wait series or
compile attribution from this diagnostic. Synchronization perturbed scheduling; the temporary
post-timeout drain path was not exercised. The pair is not a replacement gate or source fix.
All four instrumented originals were restored byte-for-byte, and the clean baseline was rebuilt
with zero warnings/errors. Receipts and restoration hashes remain in
`artifacts/bk07-two-owner-diagnostic/`. The 17 full-gate failures remain unresolved.

## Separate container smoke and retained preview

Current-source `scripts/smoke-container.ps1` passed in 126.910s. Docker CLI/engine and Compose
checks passed. The hardened UID 1654 runtime, SQL migration/principals, tenant/service-admin
authentication and revocation, photo lifecycle, and Compose TLS/session/app-replacement/telemetry
checks passed. `container-smoke.json` and `container-smoke.log` are beside the local gate receipts.
Its temporary endpoint was cleaned by the script; public CA issuance and SMTP delivery were not tested.

The owned persistent preview remains at **http://localhost:32775**, environment
`dev-e7bddb44f635445a914ea00b237326f5`. `dev-up.ps1` reported ready, clean image revision
`f3a718f30f13f4aaa6dfaf1a4c45005cd285ca67`, unchanged source inputs, and image
`sha256:a08e9a4e30a06bfbd1ed5e6df9a7f5d72239e713cb4b40761ef63b0cc038e69b`.
No later preview rebuild, reset or resource deletion occurred.

Private sign-in, creation of a non-sensitive sample item and purchase acquisition, and their
persisted state on fresh navigation were verified. Purchase-order list/new-draft presentation
and acquisition-document navigation rendered normally. The fresh document list explicitly showed
no documents. Accounting-role presentation showed both roles unassigned; no access was granted.

Persistent upload/download/rename/removal and the accounting policy form were **not verified**.
The documented Edge file-chooser call hung until interrupted; no direct input-file upload method
was documented. A later attempt to discard only the unsaved draft reached a browser confirmation,
then dialog control timed out; the original draft/modal was left intact. A fresh read-only tab
confirmed no server upload persisted. No upload or modal action was repeated, and no policy save,
financial seed, SQL grant or guard bypass occurred. The remote rename failure was not reproduced
or disproved by this partial preview inspection.

The independent passing 132-case automated browser run owns retention, blocked ordinary removal,
disposal confirmation, lost acknowledgement, permission loss, narrow keyboard interaction and both
themes. Its actual PDF upload uses guarded synthetic financial links in a disposable fixture;
it does not prove authentic public financial posting. That fixture and the published-check URL
were temporary and are no longer live. They are distinct from the retained preview above.

Current non-sensitive images remain outside Git under
`C:/Users/mcuni/.codex/visualizations/2026/09/29/01a0ebb1-0899-77f3-97bb-835bdd237970/bk07-2026-10-09/`:
`bk-07/retained-light.png`, `bk-07/retained-dark.png`, `bk-07/disposal-narrow-light.png`,
`bk-07/disposal-narrow-dark.png`, and `persistent-preview-acquisition.png`.
The first four show the passing disposable financial-evidence journey at the reviewed revision;
the last shows only the retained ordinary sample. Images are supplemental evidence.

## Final ownership, mutation and cost decisions

- Two authentic recognition upgrade regressions replace ambiguity with an exact same-side parent
  contract. Removing the correlated side predicate or recorded replacement identity was killed;
  both mutants were restored. Historical BK-06 rows target their durable BK-06 boundary; actual
  merged-GEM06-to-BK-07 tests and fresh installation own the current chain. No populated cumulative
  BK-05-to-latest claim is made by those historical rows.
- Two duplicate tests were removed: the weaker positive coordination fact and combined mixed-history
  upgrade/restore case. Separate lock/effects owners and the enriched existing guarded restore retain
  distinct assertions. Net server inventory change was zero before main added one case. There is no
  combined old-to-latest-to-restore proof.
- Existing authentic prerequisite histories/previews are restored independently with fresh current
  authorization. Actual correction/racing commands, blocking, replay and financial assertions remain
  timed. This saves setup cost but makes prerequisite/preview coupling depend on separate retained
  owners. Only measured paid-participant/three legacy-upgrade cases use awaited per-test cleanup;
  lifecycle time and cleanup failures remain distinct from case-body timing. No deadline was raised
  and no new cleanup framework remains.
- Earlier focused cohorts are not summed into a full pass. The final minimal cohort was 14 passed /
  1 timeout of 15; the repaired explicit replacement rows subsequently passed 2/2. Those repaired
  owners, legacy upgrades and enriched restore also passed the final local gate. Other owners failed
  as recorded above. Cold-compilation observations did not justify a speculative production rewrite.
- Task6's remaining manual probes used existing owners: removing predecessor set ownership was killed
  (domain error became FK rejection); removing one independent-hold check survived because another SQL
  guard still rejected disposal. The latter is redundant-defense evidence, not a killed mutant.
  The mutation receipt has nine passes/one expected mutant failure of ten. No automatic broad mutation
  run, combined-guard mutation or blanket score is claimed.

Maintained accounting/purchasing/provider/recovery contracts and the existing browser journey were
updated; this final checkpoint adds no application behavior or test case. Hosted SQL identity rollout,
provider immutability/WORM, live off-host recovery and production bookkeeping remain separate and
unverified. The execution ledger and raw receipts are preserved while verification is unresolved.
