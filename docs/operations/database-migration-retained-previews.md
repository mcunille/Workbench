# Retained preview migrations

[Return to the main entry](database-migrations.md).

### Retained PO-04 development preview reconciliation

PO-04's three development migrations (`20260918030000`, `20260918040000`, and
`20260918050000`) were consolidated before release with explicit owner approval.
They are not part of the release lineage. An already-applied retained preview must
not run the new additive migration over its existing tables or silently rewrite
its history. Back up with checksums, restore to a separate database, verify every
retained table, install and verify the final procedure definitions on that clone,
and reconcile only the clone's three development history entries to the final
PO-04 migration. Stop writers and recheck retained data before switching to the
verified clone. Keep the original database and backup for guarded recovery.

This exception applies only to the approved isolated development preview. It is
not an automatic upgrade path for shared or production databases. The final
schema retains all constraints, RLS predicates, restricted grants and rollback
guards. Backup manifest validation continues to recognize the retired development
markers so their retained backups remain usable for guarded recovery.
