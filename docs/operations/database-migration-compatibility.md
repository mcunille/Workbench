# Database migration compatibility

[Return to the main entry](database-migrations.md).

## Migration compatibility matrix

Rows are ordered by application. The predecessor column names the preceding row's full migration
ID by its unique suffix; the first migration starts from an empty database. Each release must have
its required schema and permissions before readiness succeeds; liveness remains independent.
This inventory describes checked-in migration behavior, not permission to execute Down in production.

- [Early release and financial migrations](database-migration-financial-history.md)
- [Reference and financial evidence migrations](database-migration-reference-history.md)


Product behavior, user-visible concurrency/retry rules and the shipped feature inventory belong in
[collection documentation](../collection.md). Provider retry/backoff behavior belongs in
[identity delivery and worker operations](blob-and-service-providers.md#identity-delivery-and-worker).
The [migration source](../../src/Workbench.Server/Persistence/Migrations) is authoritative for SQL.


The current required migration is `20261019010000_AddSupplierCreditsAndRefunds`.

GEM-06 introduces one coherent migration after merged GEM-05. It creates tenant-owned entries and
sparse JSON choices, installs RLS and restricted commands, and advances readiness. Shared catalog,
curation and existing tenant data remain intact. Native SQL backups include tenant reference data;
restore sanitation remains responsible for invalidating restored authentication. Full reset retains
an empty versioned override row. Down rejects destructive loss with 50020; use forward correction
or protected restore and matching binaries. Fresh creation and upgrade from `AddGemReferenceCuration`
must verify model/history, tenant scope, actual-principal command admission and the rollback guard.

GEM-05 adds one coherent migration after the merged GEM-04 pilot baseline. It creates private
command-owned drafts, immutable publication outcomes, and append-oriented publication audit;
installs six restricted web commands; and advances readiness. Existing shared content,
rowversions, provenance, tenant records, and service-admin identities are retained. Native
SQL backups include curation storage, while existing restore sanitation continues to invalidate
all restored admin sessions. Down rejects destructive loss with error 50020. Use forward
correction or protected restore; an older application's readiness marker rejects this schema.
Verify fresh creation, upgrade from `InstallGemReferencePilot`, direct-principal allow/deny
checks, exact outcome replay, and transactional catalog/audit rollback against current source.

`MakeSupplierProfilesCustom` directly follows `HardenPurchaseOrderDocumentAuthority`.
It adds custom supplier reference pairs in one migration, preserving existing supplier data,
versions and receipts. The unmerged intermediate supplier migration was consolidated at the
owner's request; the matching local preview was explicitly reconciled after verifying its final
schema and retained data. Other development histories require a verified transition before
refresh. Shipped migrations remain unchanged. Down is blocked.
Stop all application writers before migration and deploy the matching beta API/client together
only after the entire pending migration set completes. The final schema has one purchasing writer
with PO-05 financial and confirmed supplier correction validation, no historical replay procedure,
and no V2/V3/V4 write procedures. Stored content schemas 1/2/3/4 and immutable receipts remain intact.

The preparatory migration deliberately sorts before the immutable PO-05 migrations. A retained
beta database already applied consolidation/removal and therefore lacks their V4 SQL prerequisite;
EF applies the newly pending preparatory migration without changing any existing history rows.
It reconstructs only the internal procedure prerequisites needed during this offline transition.
Fresh and main-line databases already have those prerequisites and require no preparation. The
final forward migration restores financial validation after the earlier beta consolidation and
removes the temporary procedures. Do not run an intermediate application or stop the deployment
at a preparatory schema. Readiness advances only to the completed integration boundary.
Development preview inspection recognizes these exact retained histories and their ordered
forward migration stages; it continues to reject unknown migrations or gaps in earlier history.

All four earlier PO-05 and beta migrations remain unchanged because they are base history or were
applied to retained previews. The two transition migrations serve different required ordering
boundaries and cannot be consolidated without either rewriting history or breaking retained-beta
upgrades. Verify fresh creation, main-line PO-05 upgrade, and an actual beta-only applied history,
including preserved drafts/receipts, current financial saves, retired procedure absence, and
blocked downgrade. See [API lifecycle](../api-lifecycle.md).

The PO-03 changes were consolidated into `AddSupplierBasedDraftPricing`, retaining the final migration ID and final model. It installs V3 compatibility commands before V4 commands and keeps the destructive-rollback guard. Retained previews that already applied both earlier migrations keep their existing history and data unchanged; the final ID is already applied, so no schema work is repeated. A preview that applied only the removed structured-line migration is not a supported upgrade baseline and needs a separately planned transition; never reset its history automatically.
