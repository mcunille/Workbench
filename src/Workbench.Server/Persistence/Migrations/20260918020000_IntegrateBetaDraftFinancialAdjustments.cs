// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
namespace Workbench.Server.Persistence.Migrations;

public partial class IntegrateBetaDraftFinancialAdjustments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Reuse the immutable PO-05 validation on the byte-equivalent beta predecessor without
        // rewriting either branch's applied migrations or duplicating the financial SQL rules.
        var financial = new MigrationBuilder(migrationBuilder.ActiveProvider);
        FinancialDraftOrderSchema.Protect(financial);
        var financialCommand = financial.Operations.OfType<SqlOperation>()
            .Single(operation => operation.Sql.StartsWith("DECLARE @Command ", StringComparison.Ordinal));
        migrationBuilder.Sql(financialCommand.Sql.Replace("[Purchasing].[SaveDraftOrderV4]", "[Purchasing].[SaveDraftOrder]", StringComparison.Ordinal));

        // Both branch histories arrive with the PO-05 correction readiness marker: the original
        // beta migration's older marker replacement is a no-op after the parallel PO-05 upgrade.
        var correction = new ProtectConfirmedSupplierChargeCorrections().UpOperations.OfType<SqlOperation>().Single();
        migrationBuilder.Sql(correction.Sql
            .Replace("[Purchasing].[SaveDraftOrderV4]", "[Purchasing].[SaveDraftOrder]", StringComparison.Ordinal)
            .Replace("20260917030000_ProtectConfirmedSupplierChargeCorrections", "20260918020000_IntegrateBetaDraftFinancialAdjustments", StringComparison.Ordinal)
            .Replace("20260917020000_AddDraftFinancialAdjustments", "20260917030000_ProtectConfirmedSupplierChargeCorrections", StringComparison.Ordinal));

        // Bind compatibility to the requesting application's schema, not only this installed
        // procedure's self-report. The default preserves operator probes without parameters.
        migrationBuilder.Sql("""
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            IF @Readiness IS NULL OR CHARINDEX(N'WITH EXECUTE AS OWNER',@Readiness)=0
                OR CHARINDEX(N'N''20260918020000_IntegrateBetaDraftFinancialAdjustments''',@Readiness)=0
                THROW 50020,'Unsupported application readiness predecessor.',1;
            SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Readiness=REPLACE(@Readiness,N'N''20260918020000_IntegrateBetaDraftFinancialAdjustments''',N'@ExpectedMigration');
            SET @Readiness=REPLACE(@Readiness,N'WITH EXECUTE AS OWNER',
                N'@ExpectedMigration nvarchar(150)=N''20260918020000_IntegrateBetaDraftFinancialAdjustments'' WITH EXECUTE AS OWNER');
            EXEC sys.sp_executesql @Readiness;
            """);

        // Dropping temporary/retired procedures also drops every grant installed by PO-05.
        foreach (var suffix in new[] { "V2", "V3", "V4" })
            foreach (var operation in new[] { "Create", "Update", "Save" })
                migrationBuilder.Sql($"DROP PROCEDURE IF EXISTS [Purchasing].[{operation}DraftOrder{suffix}];");
        migrationBuilder.Sql("DROP PROCEDURE IF EXISTS [Purchasing].[ReplayDraftOrderReceipt];");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("THROW 50020, 'Beta financial integration requires forward correction or guarded recovery.', 1;");
}
