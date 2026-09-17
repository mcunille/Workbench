// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations;

public partial class ProtectConfirmedSupplierChargeCorrections : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Preserve the installed financial migration and patch its locked update guard forward.
        migrationBuilder.Sql("""
            DECLARE @Command nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Purchasing].[SaveDraftOrderV4]'));
            DECLARE @Before nvarchar(max)=N'DECLARE @SavedContent nvarchar(max);';
            IF @Command IS NULL OR CHARINDEX(@Before,@Command)=0
                THROW 50020,'Unsupported supplier correction predecessor.',1;
            SET @Command=REPLACE(@Command,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Command=REPLACE(@Command,@Before,N'DECLARE @SavedContent nvarchar(max),@SavedSupplierId uniqueidentifier,@SavedSupplierName nvarchar(200);
                SELECT @SavedSupplierId=SupplierId,@SavedSupplierName=SupplierName FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id=@TargetId;');
            SET @Before=N'OR currentRow.AmountStatus<>N''confirmed'')';
            IF CHARINDEX(@Before,@Command)=0 THROW 50020,'Unsupported supplier correction guard.',1;
            SET @Command=REPLACE(@Command,@Before,N'OR currentRow.AmountStatus<>N''confirmed''
                OR (JSON_VALUE(old.[value],''$.payeeKind'')=N''supplier'' AND
                    (COALESCE(CONVERT(nvarchar(36),@SavedSupplierId),N'''')<>COALESCE(CONVERT(nvarchar(36),@SupplierId),N'''')
                     OR CONVERT(varbinary(max),COALESCE(@SavedSupplierName,N''''))<>CONVERT(varbinary(max),COALESCE(@Supplier,N'''')))))');
            EXEC sys.sp_executesql @Command;
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            IF @Readiness IS NULL OR CHARINDEX(N'20260917020000_AddDraftFinancialAdjustments',@Readiness)=0
                THROW 50020,'Unsupported supplier correction readiness predecessor.',1;
            SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Readiness=REPLACE(@Readiness,N'20260917020000_AddDraftFinancialAdjustments',N'20260917030000_ProtectConfirmedSupplierChargeCorrections');
            EXEC sys.sp_executesql @Readiness;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("THROW 50020, 'Confirmed supplier charge protection requires forward correction or guarded recovery.', 1;");
}
