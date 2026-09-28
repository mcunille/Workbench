// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class SupplierOpenItemSchema
{
    internal static void Up(MigrationBuilder migrationBuilder, string migrationId)
    {
        migrationBuilder.Sql(SupplierAllocationAvailability.Sql);
        migrationBuilder.Sql(SupplierPaymentCommands.ControlSql);
        migrationBuilder.Sql(SupplierAllocationCommands.ControlSql);
        migrationBuilder.Sql(SupplierAllocationCommands.KernelSql);
        migrationBuilder.Sql(SupplierPaymentCommands.KernelSql);
        migrationBuilder.Sql(SupplierAllocationCorrections.ControlSql);
        migrationBuilder.Sql(SupplierAllocationCorrections.KernelSql);
        migrationBuilder.Sql(SupplierPaymentCorrections.CompositionSql);
        migrationBuilder.Sql(SupplierPaymentCorrections.CompositionKernelSql);
        migrationBuilder.Sql(SupplierOpenItemEvents.Sql);
        migrationBuilder.Sql(SupplierOpenItemValidation.Sql);
        migrationBuilder.Sql(SupplierAllocationCommands.Sql);
        migrationBuilder.Sql(SupplierAllocationCommands.WrapperSql);
        migrationBuilder.Sql(SupplierPaymentCommands.Sql);
        migrationBuilder.Sql(SupplierPaymentCommands.WrapperSql);
        migrationBuilder.Sql("""
            DENY EXECUTE ON Purchasing.RecordSupplierPaymentCore TO workbench_web;
            DENY EXECUTE ON Purchasing.RecordSupplierPaymentCore TO workbench_worker;
            DENY EXECUTE ON Purchasing.ApplySupplierFundsCore TO workbench_web;
            DENY EXECUTE ON Purchasing.ApplySupplierFundsCore TO workbench_worker;
            DENY EXECUTE ON Purchasing.RequireSupplierComposition TO workbench_web;
            DENY EXECUTE ON Purchasing.RequireSupplierComposition TO workbench_worker;
            """);
        migrationBuilder.Sql(SupplierPaymentCommands.SupplierGuardSql);
        migrationBuilder.Sql(SupplierPaymentCorrections.KernelSql);
        migrationBuilder.Sql(SupplierPaymentCorrections.GenericGuardSql);
        migrationBuilder.Sql(SupplierAllocationCorrections.Sql);
        migrationBuilder.Sql(SupplierAllocationCorrections.ParticipantSql);
        migrationBuilder.Sql(SupplierPaymentCorrections.PlanSql);
        migrationBuilder.Sql(SupplierPaymentCorrections.PreviewSql);
        migrationBuilder.Sql(SupplierPaymentCorrections.Sql);
        migrationBuilder.Sql(SupplierOpenItemSourceIntegration.Sql);
        migrationBuilder.Sql(SupplierOpenItemSourceIntegration.HooksSql);
        migrationBuilder.Sql(SupplierOpenItemBackfill.Sql);
        // Derivation reads legacy tables protected by this policy. Attaching new
        // predicates first can self-block its compilation on the policy's Sch-M
        // lock. New tables and evidence remain uncommitted until every predicate
        // and runtime denial below is installed in this same migration transaction.
        foreach (var table in new[] { "SupplierOpenItems", "SupplierItemMovements", "SupplierApplications",
            "SupplierApplicationReversals", "SupplierControlAttributions", "SupplierPayments",
            "SupplierPaymentCorrections", "SupplierFinancialGroups", "SupplierFinancialReceipts",
            "SupplierItemVersions", "SupplierApplicationVersions", "SupplierPaymentVersions" })
            migrationBuilder.Sql($"""
                ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                  ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Purchasing].[{table}],
                  ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Purchasing].[{table}] AFTER INSERT,
                  ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Purchasing].[{table}] AFTER UPDATE;
                GRANT SELECT ON [Purchasing].[{table}] TO [workbench_web];
                DENY INSERT,UPDATE,DELETE ON [Purchasing].[{table}] TO [workbench_web];
                DENY INSERT,UPDATE,DELETE ON [Purchasing].[{table}] TO [workbench_worker];
                """);
        migrationBuilder.Sql($"""
            DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness'));
            IF @Definition IS NULL OR CHARINDEX(N'20260928034802_AddSupplierBills',@Definition)=0
              THROW 50020,'Unsupported supplier open-item readiness predecessor.',1;
            SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Definition=REPLACE(@Definition,N'20260928034802_AddSupplierBills',N'{migrationId}');
            EXEC sys.sp_executesql @Definition;
            """);
    }
}
