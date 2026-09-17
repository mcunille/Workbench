// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;
namespace Workbench.Server.Persistence.Migrations;

public partial class RemoveHistoricalDraftReplay : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Dropping the object also removes its EXECUTE grants; durable receipts remain intact.
        migrationBuilder.Sql("DROP PROCEDURE [Purchasing].[ReplayDraftOrderReceipt];");
        migrationBuilder.Sql("""
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Readiness=REPLACE(@Readiness,N'20260917080000_ConsolidateBetaDraftCommands',N'20260918010000_RemoveHistoricalDraftReplay');
            EXEC sys.sp_executesql @Readiness;
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("THROW 50020, 'Historical API removal requires forward correction or guarded recovery.', 1;");
}
