// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class SupplierBillSchema
{
    internal static void Up(MigrationBuilder migrationBuilder, string migrationId)
    {
        migrationBuilder.Sql(SupplierBillValidation.NormalizeSql);
        migrationBuilder.Sql(SupplierBillValidation.Sql);
        migrationBuilder.Sql(SupplierBillDraftCommands.Sql);
        foreach (var table in new[] { "SupplierBills", "SupplierBillRevisions", "SupplierBillReceipts" })
            migrationBuilder.Sql($"""
                ALTER SECURITY POLICY Security.TenantIsolationPolicy
                  ADD FILTER PREDICATE Security.fn_tenant_access(TenantId) ON Purchasing.{table},
                  ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Purchasing.{table} AFTER INSERT,
                  ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Purchasing.{table} AFTER UPDATE;
                DENY INSERT,UPDATE,DELETE ON Purchasing.{table} TO workbench_web;
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
