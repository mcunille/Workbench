// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class FinancialEvidenceSchema
{
    internal static void Create(MigrationBuilder migration)
    {
        migration.Sql("UPDATE Storage.Attachments SET IndependentHeld=Held;");
        migration.Sql(FinancialEvidenceCapture.DeadlineSql);
        migration.Sql(FinancialEvidenceSourceAdapters.SourceSql);
        migration.Sql(FinancialEvidenceSourceAdapters.ValidateSql);
        migration.Sql(FinancialEvidenceInheritance.LinksSql);
        migration.Sql(FinancialEvidenceInheritance.ValidateSql);
        migration.Sql(FinancialEvidenceCapture.AcquireSql);
        migration.Sql(FinancialEvidenceCapture.CaptureSql);
        migration.Sql(FinancialEvidenceCapture.AppendSql);
        FinancialEvidenceSourceAdapters.InstallHooks(migration);
        migration.Sql("""
            DENY UPDATE ON OBJECT::Storage.Attachments(Held,IndependentHeld) TO workbench_web;
            DENY UPDATE ON OBJECT::Storage.Attachments(Held,IndependentHeld) TO workbench_worker;
            DENY EXECUTE ON Accounting.CaptureFinancialEvidence TO workbench_web;
            DENY EXECUTE ON Accounting.CaptureFinancialEvidence TO workbench_worker;
            DENY EXECUTE ON Accounting.AcquireFinancialEvidenceLink TO workbench_web;
            DENY EXECUTE ON Accounting.AcquireFinancialEvidenceLink TO workbench_worker;
            DENY EXECUTE ON Accounting.AppendFinancialEvidence TO workbench_web;
            DENY EXECUTE ON Accounting.AppendFinancialEvidence TO workbench_worker;
            """);
        migration.Sql("""
            CREATE TRIGGER Purchasing.PreserveRecognitionEvidenceAuthority ON Purchasing.RecognitionSideEvents AFTER UPDATE AS BEGIN
              SET NOCOUNT ON;
              IF UPDATE(EvidenceMutationPermission) THROW 51009,'Original recognition mutation authority is immutable.',1;
            END;
            """);
        migration.Sql("""
            CREATE TRIGGER Storage.ProtectInsertedFinancialHold ON Storage.Attachments AFTER INSERT AS BEGIN
              SET NOCOUNT ON;
              IF IS_ROLEMEMBER('workbench_web')=1 AND EXISTS(SELECT 1 FROM inserted WHERE Held=1 OR IndependentHeld=1)
                THROW 51003,'Attachment holds require a restricted command.',1;
            END;
            """);
        migration.Sql("""
            CREATE TRIGGER Accounting.PreserveFinancialEvidenceSet ON Accounting.FinancialEvidenceSets AFTER UPDATE,DELETE AS BEGIN
              SET NOCOUNT ON;
              IF UPDATE(OwnerKind) OR UPDATE(OwnerId) OR UPDATE(OwnerRevisionId) OR UPDATE(PurchaseOrderId) OR UPDATE(SupplierId)
                OR UPDATE(ActorId) OR UPDATE(PostingDate) OR UPDATE(RecordedAtUtc) OR UPDATE(SourceSnapshotSha256) OR UPDATE(MissingEvidenceReason)
                OR UPDATE(LegacyEvidence) OR UPDATE(MutationPermission) OR UPDATE(InheritedEvidenceSetId)
                OR EXISTS(SELECT TenantId,Id FROM deleted EXCEPT SELECT TenantId,Id FROM inserted)
                THROW 51009,'Original financial evidence is immutable.',1;
            END;
            """);
        foreach (var table in new[] { "FinancialEvidenceLinks", "FinancialEvidenceAdditions", "FinancialEvidenceReceipts" })
            migration.Sql($"CREATE TRIGGER Accounting.Preserve{table} ON Accounting.{table} AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; IF EXISTS(SELECT 1 FROM deleted) THROW 51009,'Financial evidence history is immutable.',1; END;");
        foreach (var (schema, table) in new[] { ("Accounting", "FinancialEvidenceSets"), ("Accounting", "FinancialEvidenceLinks"),
            ("Accounting", "FinancialEvidenceAdditions"), ("Accounting", "FinancialEvidenceReceipts"), ("Storage", "FinancialEvidenceAttachmentStates") })
            migration.Sql($"""
                ALTER SECURITY POLICY Security.TenantIsolationPolicy
                  ADD FILTER PREDICATE Security.fn_tenant_access(TenantId) ON [{schema}].[{table}],
                  ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON [{schema}].[{table}] AFTER INSERT,
                  ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON [{schema}].[{table}] AFTER UPDATE;
                GRANT SELECT ON [{schema}].[{table}] TO workbench_web;
                DENY INSERT,UPDATE,DELETE ON [{schema}].[{table}] TO workbench_web;
                DENY INSERT,UPDATE,DELETE ON [{schema}].[{table}] TO workbench_worker;
                """);
        Alter(migration, "Security.ReadDatabaseReadiness", ("20261003214043_AddTenantGemReference", "20261008010000_AddFinancialEvidenceRetention"));
    }

    // Read the actual durable predecessor, validate each anchor, and modify only this release's procedures.
    internal static void Alter(MigrationBuilder migration, string procedure, params (string Before, string After)[] replacements)
    {
        var sql = $"DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'{procedure}')); IF @Definition IS NULL THROW 50020,'Missing financial evidence predecessor.',1;\n";
        foreach (var (before, after) in replacements)
        {
            var escaped = before.Replace("'", "''", StringComparison.Ordinal);
            sql += $"IF CHARINDEX(N'{escaped}',@Definition)=0 THROW 50020,'Unsupported financial evidence predecessor: {procedure}.',1;\n";
            sql += $"SET @Definition=REPLACE(@Definition,N'{escaped}',N'{after.Replace("'", "''", StringComparison.Ordinal)}');\n";
        }
        sql += "SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE'); EXEC sys.sp_executesql @Definition;";
        migration.Sql(sql);
    }
}
