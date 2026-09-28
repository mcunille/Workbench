// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class SupplierBillSchema
{
    internal static void Up(MigrationBuilder migrationBuilder, string migrationId)
    {
        migrationBuilder.Sql(SupplierBillReviewCommands.TablesSql);
        migrationBuilder.Sql(SupplierBillPosting.TablesSql);
        migrationBuilder.Sql(SupplierBillPosting.KernelOutputSql);
        migrationBuilder.Sql(SupplierBillValidation.NormalizeSql);
        migrationBuilder.Sql(SupplierBillValidation.Sql);
        migrationBuilder.Sql(SupplierBillReviewCommands.EvidenceSql);
        migrationBuilder.Sql(SupplierBillReviewCommands.ValidateSql);
        migrationBuilder.Sql(SupplierBillReviewCommands.Sql);
        migrationBuilder.Sql(SupplierBillDraftCommands.Sql);
        migrationBuilder.Sql(SupplierBillPosting.Sql);
        foreach (var table in new[] { "SupplierBills", "SupplierBillRevisions", "SupplierBillReceipts", "SupplierBillReviews", "SupplierBillEvidence", "SupplierBillPostings", "SupplierBillPostingEvents" })
            migrationBuilder.Sql($"""
                ALTER SECURITY POLICY Security.TenantIsolationPolicy
                  ADD FILTER PREDICATE Security.fn_tenant_access(TenantId) ON Purchasing.{table},
                  ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Purchasing.{table} AFTER INSERT,
                  ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Purchasing.{table} AFTER UPDATE;
                DENY INSERT,UPDATE,DELETE ON Purchasing.{table} TO workbench_web;
                """);
        migrationBuilder.Sql("""
            DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.SavePurchaseOrder')),
              @Anchor nvarchar(max)=N'EXEC Purchasing.ValidatePurchaseOrderContent @Draft,@TenantId,@TargetId,@Calculation OUTPUT;';
            IF @Definition IS NULL OR CHARINDEX(@Anchor,@Definition)=0 THROW 50020,'Unsupported bill purchase guard predecessor.',1;
            SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Definition=REPLACE(@Definition,@Anchor,N'
              IF @Operation=''Amend'' AND EXISTS(SELECT 1 FROM Purchasing.SupplierBills WHERE TenantId=@TenantId AND PurchaseOrderId=@TargetId AND State<>''Abandoned''
                AND (TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Draft,''$.supplierId'')) IS NULL OR SupplierId<>TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Draft,''$.supplierId''))))
                THROW 50415,''Supplier is fixed while bill evidence exists.'',1;
              '+@Anchor);
            EXEC sys.sp_executesql @Definition;
            """);
        migrationBuilder.Sql($"""
            DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness'));
            IF @Definition IS NULL OR CHARINDEX(N'20260926210900_AddPurchaseRecognition',@Definition)=0
              THROW 50020,'Unsupported bill readiness predecessor.',1;
            SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Definition=REPLACE(@Definition,N'20260926210900_AddPurchaseRecognition',N'{migrationId}');
            EXEC sys.sp_executesql @Definition;
            """);
    }
}
