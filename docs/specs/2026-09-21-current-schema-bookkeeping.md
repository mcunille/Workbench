# Current schema bookkeeping

**Status:** Implemented

## Problem and scope

Issue #149 follows supplier metadata work that required unrelated acquisition, pricing,
recovery, and migration tests to change their current migration literal or total count.
The application also repeated the current marker in readiness and backup code.

This is an internal maintenance contract. Public APIs, schema SQL, persisted data, backup
formats, supported historical markers, and guarded restore/downgrade behavior are unchanged.

## Contract

`CurrentSchema.Migrations` is an immutable, explicit, ordered release manifest. Its final
entry supplies `CurrentSchema.MigrationId`. Readiness supplies that marker to the existing
SQL procedure, and offline backup emits it in new manifests. A database-free test compares
the full manifest with EF's compiled inventory and checks ordering and uniqueness.

An explicit manifest is preferable to deriving the expected history from EF: an accidental
migration omission, rename, or addition must cause a discrepancy requiring deliberate review.
A separate current constant would introduce a second value to synchronize within the contract.

`MigrationHistoryAssertions` compares the complete expected ordered history against the
applied history. Retained preview cases explicitly supply their additional historical IDs.
It does not infer exceptions from production compatibility logic or reduce history to a count.

Backup acceptance includes the current boundary plus its existing explicit historical
allowlist. Historical compatibility cases retain literal markers as independent oracles;
known EF migrations predating backup support and unknown future markers are rejected.

## Inventory and preservation

| Location | Classification and treatment |
| --- | --- |
| `DatabaseReadinessCheck`, `StorageMaintenanceCommand` emitted marker | Current release; use shared boundary. |
| Acquisition, shared acquisition, beta financial upgrade tests | Remove redundant SQL-definition marker checks; retain domain preservation and behavioral readiness checks. |
| Migration compatibility and blob recovery tests | Own the current readiness and emitted backup marker checks using the shared boundary. |
| Fresh migration, concurrent migrator, cancelled-migrator retry tests | Current history; compare full ordered manifest. |
| Retained supplier-pricing preview test | Current history plus explicitly retained structured-line migration. |
| Supplier profile consolidation test | Fixed historical upgrade; target supplier-profile migration explicitly and retain one-step assertion. |
| Migration SQL, historical upgrade/downgrade fixtures, retained branch inspection | Historical compatibility oracles; retain IDs unchanged. |
| Cancelled migration's initial-schema count of one | Fixed historical state; retain assertion. |
| Backup compatibility matrix and runtime historical allowlist | Independent compatibility decision; preserve every existing marker. |

## Verification

Acceptance requires exact-history rejection of missing, reordered, renamed, duplicate, unknown,
and undeclared retained entries; the actual EF inventory must agree with the explicit manifest.
Existing SQL readiness mismatch, retained-branch upgrade, restore, and downgrade tests remain.
A temporary appended migration and manifest entry exercise unchanged acquisition, recovery,
and current-history tests; remove the experiment before delivery. Run affected tests and the
repository verification and container smoke gates against the final source.

Future releases append the migration to the manifest, explicitly advance the SQL readiness
marker, and preserve the outgoing marker's backup compatibility where supported. Feature
migration SQL and independent historical fixture expectations remain reviewed release work.
