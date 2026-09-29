# Test reduction evidence — 2026-09-29

Base revision: `ddb2101a887bd33f43f91c00ef07a3fbc0698de3`.

The audit removed 60 runnable cases: 49 server cases and 11 client cases. The requested 20% reduction was not justified by the candidates reviewed while preserving distinct contracts. This is the approved smaller-reduction fallback, not a claim that the 20% target was met. Parameterized rows count individually; no tests were skipped or bundled into loops to lower discovery counts. No production implementation changed.

## Removed cases and surviving owners

| Suite / removed cases | Count | Surviving proof |
| --- | ---: | --- |
| `DatabaseMigrationTests.MigratorUpgradesASeededPriorSchemaWithoutLosingTenantData`: intermediate prefix rows | 22 | Retained `InitialSchema` row places the same tenant before every migration; `IntegrateBetaDraftFinancialAdjustments` retains a later-prefix upgrade. Dedicated feature migrations still seed actual historical drafts, receipts, attachments, and accounting records. Fresh schema, concurrent migrators, cancellation, destructive-down refusal and BK-05-to-BK-06 upgrade remain. |
| `DatabaseSchemaReadinessTests.PriorReleaseSchemaIsUnreadyUntilDeploymentMigrationIsApplied`: intermediate prefix rows | 10 | Oldest `AddBlobAndOperationalProviders` and immediate predecessor `20260928034802_AddSupplierBills` retain 503-ready/200-live then 200-ready after deployment. `ReadinessAuthorityTests` probes individual authorities from a healthy baseline. |
| `PurchasingIdentityInputTests.WebsiteValidationRejectsUnsafeOrNonAbsoluteValues` | 4 | `SupplierWebsiteNormalizationDoesNotBypassValidation` covers the same rejected forms through the actual normalize-then-validate path; `SupplierWebsiteTests` retains real endpoint persistence. |
| `PublicEndpointTests`: second Azure peer and two Azure host-filter mode rows | 3 | One arbitrary Azure peer retains the one-hop/forwarded-host contract. KnownProxies mode retains allowed/denied Host values; Azure configuration and metadata tests retain its distinct policy. |
| `BrowserSecurityHeadersTests`: HTTP localhost row | 1 | Remote HTTP and HTTPS localhost/IPv4/IPv6 retain the independent scheme and loopback conditions. Root, direct static file, SPA, endpoint and photo authentication paths remain. |
| `ProductionSecurityConfigurationTests.AzureMetadataTrustDoesNotWaiveOtherProductionRequirements`: proof-key and origin rows | 2 | Dedicated missing-proof-key and invalid-origin tests retain the underlying requirements. Other Azure rows retain validation before and after proxy setup. |
| `ItemInputTests` blank-field and `PurchaseOrderInputTests` calendar-date empty-string rows | 2 | Null and nonempty Unicode whitespace remain for item normalization; null, impossible date, noncanonical date and timestamp remain for commitment dates. |
| `BlobManifestValidationTests.CurrentReleaseAcceptsAnExactManifest` | 1 | `KnownReleasesFromBackupSupportBoundaryAcceptAnExactManifest` includes current release; `CurrentSchemaTests` binds current marker to the full inventory. Independent literal first-boundary, retired markers, both count directions, casing and unknown/future rejection remain. |
| `PurchaseRecognitionPostingTests.EligibleInvoiceCreatesPrepayment` | 2 | Both classifications remain in `InvoiceBreakdownPostsNetCostAndSeparateRecoverableTax`; single-journal and zero inventory/expense balance assertions moved there. Bill adapter and matching tests retain production source and later recognition transitions. |
| `PurchaseRecognitionComponentTests.InvoiceRoundingHasOneMinorUnitBound`: positive 0.01 row | 1 | Exact scale-two 100.01/0.01 row remains in `InvoiceRoundingUsesConfiguredMinorUnitAtEverySupportedScale`; negative rounding and out-of-bound rejection remain. |
| `SupplierBillPostingTests.MatchedVarianceUsesOriginalClassification`: Inventory/no-variance row | 1 | `ReceiptFirstInvoiceClearsAccrualWithoutDoubleRecognition` uses the same bill path and also checks payable and match count. Expense/variance and unsupported-inventory-variance rejection remain. |
| Acquisition and purchase document API: repeated 422/401 rows and capacity-title echo tests | 6 | Each implementation retains 422 actionable title, all explicit 400/413/415 classifications, 409 typed reason, generic 503 status, multipart/CSRF, and failed/private download checks. Server tests own actual capacity limits. |
| Export API: four generic error status rows | 4 | One 503 case retains generic status propagation; export-memory tests continue to exercise 401/403/422/429/503 failure-state and authentication handling. |
| System API mock-response echo | 1 | `App.test.tsx` uses real `getSystem` to display compact/full build identity and handles failed requests; generated contract and endpoint tests remain. |

## Deliberate retentions

All browser and tooling cases remain. Unique SQL principal, tenant-isolation, transaction, concurrency, migration-data, payment, allocation, and recovery cases remain even where .NET coverage is redundant. The shared transport tests retain both raw Request/URL semantics and the exact retired-contract 409 global-lock regression; the App's 400 recovery test is not a substitute. Twelve-character malformed Base64 remains because a shorter malformed input exits through the length guard. Distinct document status-list members and draft whitespace/control variants remain.

## Measurement and verification

Runnable inventory (parameterized rows counted separately):

| Suite | Before | After | Removed |
| --- | ---: | ---: | ---: |
| Server | 1,742 | 1,693 | 49 |
| Client | 519 | 508 | 11 |
| Browser | 122 | 122 | 0 |
| Node tooling | 11 | 11 | 0 |
| **Total** | **2,394** | **2,334** | **60 (2.51%)** |

Server declarations changed from 869 to 866; client declarations from 401 to 398. Fourteen standalone PowerShell check scripts remain unchanged and are excluded from the discoverable-case denominator; their existence is not a claim that every script was executed.

| Measured suite | Metric | Before (covered/total) | After (covered/total) | Change (percentage points) |
| --- | --- | --- | --- | ---: |
| Server (`Workbench.Server`) | Lines | 81,823/83,294 (98.2340%) | 81,823/83,294 (98.2340%) | 0.0000 |
| Server (`Workbench.Server`) | Branches | 4,999/6,419 (77.8782%) | 4,999/6,419 (77.8782%) | 0.0000 |
| Client | Lines | 2,749/3,049 (90.1607%) | 2,749/3,049 (90.1607%) | 0.0000 |
| Client | Branches | 3,900/4,720 (82.6271%) | 3,901/4,720 (82.6483%) | +0.0212 |

Percentages above are calculated from raw counts and rounded to four decimal places. All four metrics satisfy the two-percentage-point limit. The single extra client branch hit is treated as run variation, not an improvement attributable to deleting tests. Both server coverage runs passed every discovered case without skips (1,742 before; 1,693 after). The server collector actually reported only `Workbench.Server`; the separate database CLI process and SQL engine branches are outside this percentage.

The original server baseline passed all 1,742 cases. The baseline assembly was built from original tests before pruning. Reduced server coverage uses a fresh Release build of canonical source in an isolated SDK artifacts directory, followed by a complete SHA256-verified output copy. Both runs use SDK 10.0.401, Coverlet 6.0.4, semantically identical settings, and sequential test collections. Duplicate VSTest coverage attachments were checked for identical hashes before comparison. Cobertura used different relative source roots for the two build layouts; comparison resolves ordinary files to absolute paths. The two generated OpenAPI/regex documents are matched by generator-relative identity and identical PDB source checksums, without exclusions. All 271 covered server source documents have matching identities and PDB checksums, and both coverage denominators are unchanged.

Client measurements use Vitest/V8 4.1.11 with one worker and identical source scopes. The unchanged production-file lists and line/branch denominators are checked separately for each measured suite. The acceptance limit is at most two percentage points of loss for each metric in each suite.

The matching Vitest V8 provider and `tests/coverage.runsettings` establish fixed source scopes before and after pruning; see [coverage commands](../README.md#coverage-comparisons). No exclusions were introduced between measurements. Generated SQL strings count as C# coverage, not SQL engine branch coverage. Browser and standalone tooling contracts are separate execution evidence. Whole-suite mutation scores and runtime improvements are not claimed.

Verification completed from current source:

- Full `./scripts/verify.ps1` passed, including locked restores, documentation checks, formatting, Release build, generated API drift, client type/build checks, 1,693 server cases, 508 client cases, 122 browser cases, and published-output verification. Server partitions passed 827 and 866 cases, with exact inventory coverage and no skips. Run ID: `2cde55b9dfd74bb0a2479c7a98088d9c`.
- The full gate used a separate managed checkout at the same base with every changed source/configuration/test file and coverage settings verified byte-for-byte against this patch. Later edits only updated this evidence record and the tests README.
- `./scripts/smoke-container.ps1` passed: internal TLS, SQL readiness, Secure-cookie login, durable session after app replacement, forwarding headers, private app listener, and worker queue telemetry. Public certificate issuance and SMTP delivery remain outside the local smoke scope.
- Focused retained-owner checks passed: 171 server and 66 client cases. `./scripts/verify-migrations.ps1 -Scenario Upgrade` passed its 10 selected cases; full server verification also exercised the retained feature-specific migration cases.
- All 11 Node tooling cases passed. All browser and tooling test files remain unchanged.
- Seven targeted manual mutants were detected by assertion failures: acquisition-document conflict reason, purchase-document conflict reason, export error status, system version display, credential-bearing website rejection, HSTS locality, and SQL invoice prepayment classification (both classifications). This is targeted evidence, not a whole-suite mutation score.

Coverage JSON/XML, TRX inventories, mutation logs, smoke output, and full-gate evidence are retained locally under ignored `artifacts/test-audit/`. Their counts and coverage denominators were checked before writing this record.

Independent discovery lanes and a fresh-context preservation review checked the complete diff. The reviewer required the invoice-first journal-count and zero-classification assertions to survive in the richer keeper; both were transferred. Server mutation probes ran in an isolated copy of current source. Mutated files were restored byte-for-byte against canonical source, then rebuilt for the 171-case focused check. Client mutation files were likewise restored byte-for-byte. Canonical server production source was never mutated.
