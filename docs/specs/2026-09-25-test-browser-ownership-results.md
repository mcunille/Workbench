# Browser workflow and matrix ownership measurements

Implementation evidence for [issue #161](https://github.com/mcunille/Workbench/issues/161).
The baseline is `4b4d5ef`, after the assertion, duplicate and SQL setup cleanups (#165, #167, #169).
This applies the existing [test ownership rules](../../tests/README.md) and
[efficiency design](2026-09-16-test-suite-efficiency.md). Product behavior, API contracts,
worker limits, timeouts, rate limits, diagnostics and session ownership are unchanged.

## Retained regression detectors

| Before | After | Assertion ownership and reason |
| --- | --- | --- |
| Four live `purchase-order-layout.spec.ts` cases, each creating a draft | Four `purchase-order-layout.ui.spec.ts` cases at 320, 390, 600 and 1440px | Real browser loading/clearing panel position, enlarged text, wide font action bounds, clipping and document overflow remain. Clear-search focus is explicit. Routes precede initial navigation, return owned data and never continue/fetch live requests. `purchase-orders.spec.ts` retains real save/reload/separate-session persistence and title/supplier/item identity. |
| Two live `supplier-profiles.spec.ts` create/validate/save/reload/edit/remove/save/reload journeys | One complete live journey plus two `supplier-profiles.ui.spec.ts` cases at 320 and 1440px | Live coverage still proves three arbitrary labels/handles, reference text without links, separate website, edit/removal and reload. Both browser widths keep invalid-platform focus, row alignment/stacking, remove-button position and overflow. `SupplierEditor.test.tsx` owns local editing/validation permutations. |
| Two live `supplier-header.spec.ts` cases | Two `supplier-header.ui.spec.ts` cases at 320 and 1440px | Owned create/read responses preserve real toolbar submission, saved heading versus unsaved rename, status and disabled save, short-form width, long multilingual name, sticky actions at 200% text, overflow, and desktop contact stacking. Supplier profile live journey retains actual toolbar-to-server save. |
| `AddItem` uncertain-save retry subset | Existing selectable-recovery-text case | It already freezes input, preserves all fields, excludes request metadata, selects all recovery text, reuses the exact submitted object and invokes completion. `RecoveryText` selection only focuses/selects the textarea; it neither changes nor branches the retry command. The retained live creation retry also exercises retry without selecting recovery text. |
| `inventory.spec.ts` unchanged-record lost-response retry | Existing `editing.spec.ts` creation retry after another session's edit | Both commit the first request then lose its response, freeze inputs, compare exact retry payload/identity, require successful replay and one record. The retained journey additionally verifies the same detail URL and current name/notes/location after replay/reload. `InventoryEndpointTests.DatabaseFailureNeverReportsSuccessAndSameDraftCanBeRetried` owns unchanged-record replay; `ItemEditingTests.CreationReplayKeepsOriginalIdentityAcrossEdits` owns changed-record replay and conflicting payloads. |
| ZIP cancellation subsection in `package.spec.ts` | Existing CSV cancellation journey in `export.spec.ts` | One browser holds a complete real response, cancels, releases it, exposes no download and prepares/downloads again. `exportMemory.test.ts` retains late-response cancel/dispose/scope/format matrices; `api/export.test.ts` owns abort/body-read transport failures. ZIP 503 explanation, malformed Content-Length/interrupted body, no partial link, explicit retry and independently parsed native ZIP download remain in the mixed-purpose test. Both formats retain native downloads. |
| Eight persisted CSV encoding rows | Two persisted rows with distinct name, notes and location values | Every exported ID maps to independently expected fields, so swapping/duplicating columns fails. Exact headers, 19-field schema, version, scope, ID/tracking kind, archive/creation metadata, Unicode, punctuation, newline/tab/control text, safety prefixes and one shared multi-row timestamp remain. `ItemExportEncodingTests` keeps all eight exhaustive pure classes: equals, plus, minus, at-sign, apostrophe, leading whitespace/tab, control and Unicode/quotes/CRLF. All-pages/scope/tenant API owners remain untouched. |
| Five label hierarchy examples in `DraftList.test.tsx` | Representative accessible rows and explicit href identity | Two identically labelled same-supplier purchases retain different references/targets; a custom title remains wired into its row. Pure `purchaseLabel.test.ts` owns all six precedence/fallback examples, including items-only, entries, empty draft and ordered fallback. |

Candidates intentionally retained include the stronger live creation retry, the complete supplier
persistence journey, every geometry width, the CSV cancellation journey, ZIP's unique interrupted
body, both native downloads, and exhaustive pure label/encoding tests. Similarity alone did not
justify removing any of those distinct boundaries.

## Exact discovery changes

Browser discovery remains **120 cases**, changing from **111 live / 9 intercepted** to
**103 live / 17 intercepted**. Comparison uses project, file and full expanded test title;
the original and final JSON inventories also retain source locations. Every identity is unique.

| Discovery delta | Cases removed / added | Explanation |
| --- | --- | --- |
| `purchase-order-layout.spec.ts` → `purchase-order-layout.ui.spec.ts` | 4 / 4 | Same titles and widths; project changes from live to intercepted. |
| `supplier-header.spec.ts` → `supplier-header.ui.spec.ts` | 2 / 2 | Same widths; titles change from “saves and keeps record context” to “keeps saved record context”; project changes. |
| `supplier-profiles.spec.ts` → one live plus `supplier-profiles.ui.spec.ts` | 2 / 3 | Two complete width-specific journeys become one named persistence journey and two named validation/geometry cases. |
| `inventory.spec.ts` simpler committed-save/lost-response journey | 1 / 0 | Stronger existing two-session journey remains unchanged. |
| `package.spec.ts` mixed failure case | 1 / 1 | Title ends in “retry recovers” rather than “retry and cancellation recover”; its distinct failure/download behavior remains live. |

These ten removed and ten added identities account for the entire browser delta. The affected
browser cohort remains 37 cases. The client cohort changes from 69 to 68: one `AddItem` subset
is removed and one `DraftList` case is renamed/reduced; all other selected cases remain.
All **1,268 server identities** are identical before/after; reducing rows inside the CSV fact does
not change discovery. Raw inventories and the exact comparison remain in ignored evidence files.

## Measurement method

Measurements run sequentially on TARDISCORE with .NET SDK 10.0.401, Node 26.7.0, npm 11.19.0,
and Docker Desktop's Linux engine. Each state has three client/server/browser focused runs and
two complete browser runs. Original and replacement browser files use the same filename stems.
The ordinary focused script includes current-source builds; full browser runs include normal
publication, disposable SQL startup, browser execution and cleanup. Initial client/server builds
are recorded separately. Unrelated checkout services remain untouched; shared-host/cache variation
is not treated as a causal effect. One live and one intercepted worker remain configured.

The client cohort is AddItem, exportMemory, API export, DraftList and purchaseLabel. The server
cohort is ItemExportCsvTests, ItemExportEncodingTests, InventoryEndpointTests, ItemEditingTests,
ItemExportEndpointTests (including all-page, archive-scope and tenant cases). The browser cohort is purchase-order-layout,
supplier-profiles, supplier-header, purchase-orders, inventory, editing, export and package.
Raw logs, discovery identities, source receipts, commands and process times remain under ignored
`artifacts/issue-161/`; no raw media, TRX or timing dataset is committed.

## Focused measurement results

Seconds are process wall time. Each range includes all three successful samples on that state.

| Cohort | Cases before / after | Before median (range) | After median (range) |
| --- | --- | --- | --- |
| Client | 69 / 68 | 15.70 (3.80–21.53) | 3.81 (3.67–5.15) |
| Server | 27 / 27 | 82.58 (77.33–82.69) | 72.58 (72.33–81.39) |
| Affected browsers | 37 / 37 | 98.34 (93.62–104.06) | 82.93 (82.35–85.62) |

The client range is dominated by process/environment variation: its fastest baseline sample is
already near the after median. Do not attribute that median difference to removing one case.
Server ranges overlap, so the magnitude of its observed reduction is uncertain. The affected
browser samples show a consistent reduction on this host. These cohorts overlap in runtime
dependencies; do not add their differences or infer a full-gate speedup from them.

Both complete browser runs pass all 120 cases in each state:

| Complete browser metric | Before | After |
| --- | --- | --- |
| Process wall time, samples | 198.53s, 200.66s | 187.02s, 185.12s |
| Process midpoint (range) | 199.59s (198.53–200.66) | 186.07s (185.12–187.02) |
| Summed case durations, samples | 173.13s, 175.50s | 166.87s, 164.30s |

The full-browser comparison shows a 13.52-second lower midpoint on this host. Two samples per
state do not establish statistical significance or predict CI performance. Case durations overlap
between projects and are not wall time. For the affected browser cohort, summed case durations
range from 55.88–59.91s before and 50.96–54.44s after. The initial standalone server builds take
6.93s before and 1.73s after; initial client builds take 8.84s and 7.29s. Those incremental build
differences are not credited as test savings. No comparable before/after full verification gate
was measured; only the required final gate is run for delivery.

## Scoped fault probes

These are targeted manual mutations, not an automated mutation-tool run or a whole-suite score.
Each changed production file was restored byte-for-byte in a `finally` block and checked against
its original SHA256. Restoration was checked again before starting the clean after cohorts.

| Temporary fault | Observed regression detector |
| --- | --- |
| Generate a new ID when retrying the frozen creation command | The retained `AddItem` recovery case fails its original-object assertion. |
| Replace copied notes with the creation request ID | The same case fails the exact recovery-text assertion, detecting lost business text and leaked command metadata. |
| Remove the export completion identity/abort guards | Four `exportMemory` cases fail: cancel, dispose, scope change and ZIP format change expose an obsolete object URL. |
| Give supplier name precedence over a custom purchase title | The pure precedence case and representative accessible-row case both fail. |
| Point every purchase row at a wrong record URL | The representative component case fails its href assertion. |
| Return original creation fields on replay instead of current edited fields | Both current-schema and upgraded-schema `CreationReplayKeepsOriginalIdentityAcrossEdits` cases fail on stale name/notes/location. |
| Project storage location into CSV notes | The two-row persisted CSV case fails its independently expected notes assertion. |
| Offset the results panel by 24px only while the loading message is visible | All four purchase-order widths fail their anchored-position assertion, with a measured 24px difference. |
| Force the page actions to a 2200px minimum width | All four purchase-order widths fail the document-overflow assertion. |
| Disconnect the real export Cancel button | The retained CSV browser journey fails: after release it announces a ready file instead of cancellation. The cancellation-status assertion moved from the removed ZIP subsection into this journey. |

An initial probe removing only `.po-draft-progress`'s minimum height survived all four widths.
It did not establish a geometry regression: the surrounding toolbar and controls also reserve
height. It is recorded as a limited, non-detected probe, not credited as detection or claimed
equivalent under all possible layouts. The explicit loading-offset probe above establishes that
the retained browser assertions detect actual panel movement. No production fix was needed.

## Final delivery verification

The current-source `scripts/verify.ps1 -SkipDependencyInstall` gate passed (run
`7fdfb2829b8d4685a6e1fe52d9ddb9fe`): 1,268 server tests across two partitions with exact
inventory coverage, 517 client tests, and all 120 browser cases. Formatting, lint, API
contract generation/drift checks, type checking, Release build and published release-unit
checks passed. The gate reports 1,573.44 seconds; its caller records 1,575.16 seconds.
This final gate overlaps its stages and is separate from the sequential timing comparisons.

All four browser harness suites passed (10 tests). `scripts/smoke-container.ps1` passed in
96.61 seconds against the current-source image, including internal TLS, SQL readiness,
Secure-cookie login, durable session after app replacement, forwarding headers, private
app listener and worker queue telemetry. Public CA issuance and SMTP delivery remain untested.
Browser workflows were exercised through the normal isolated runner; no retained preview
was requested or created. Independent final review found no actionable coverage gaps.

Only tests and documentation changed. No production behavior, CI architecture, concurrency,
timeouts or rate limits changed; no automated mutation framework or whole-suite mutation
score is claimed. Raw gate results remain in ignored local evidence directories.
