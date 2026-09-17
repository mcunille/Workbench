// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;
namespace Workbench.Server.Persistence.Migrations;

public partial class PrepareRetainedBetaFinancialUpgrade : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // A retained beta database has already removed the procedures required by the newly
        // pending, immutable PO-05 migrations. Fresh/main-line databases need no preparation.
        // These internal prerequisites exist only during the drained, offline migration window.
        migrationBuilder.Sql("""
            IF OBJECT_ID(N'[Purchasing].[SaveDraftOrderV4]',N'P') IS NULL
                AND EXISTS(SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId=N'20260917080000_ConsolidateBetaDraftCommands')
            BEGIN
                DECLARE @Command nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Purchasing].[SaveDraftOrder]'));
                IF @Command IS NULL OR CHARINDEX(N'CREATE PROCEDURE [Purchasing].[SaveDraftOrder]',@Command)=0
                    THROW 50020,'Unsupported retained beta financial predecessor.',1;
                SET @Command=REPLACE(@Command,N'CREATE PROCEDURE [Purchasing].[SaveDraftOrder]',N'CREATE PROCEDURE [Purchasing].[SaveDraftOrderV4]');
                EXEC sys.sp_executesql @Command;
                -- The immutable financial migration alters this retired procedure; no caller receives EXECUTE.
                EXEC sys.sp_executesql N'CREATE PROCEDURE [Purchasing].[SaveDraftOrderV3] AS BEGIN THROW 50427,''Retired API contract.'',1; END;';
                EXEC sys.sp_executesql N'CREATE PROCEDURE [Purchasing].[CreateDraftOrderV4]
                    @RequestId uniqueidentifier,@CanonicalInputJson nvarchar(max),@ActorUserId uniqueidentifier AS
                    BEGIN EXEC [Purchasing].[SaveDraftOrderV4] @RequestId,@CanonicalInputJson,@ActorUserId,''Create''; END;';
                EXEC sys.sp_executesql N'CREATE PROCEDURE [Purchasing].[UpdateDraftOrderV4]
                    @RequestId uniqueidentifier,@CanonicalInputJson nvarchar(max),@ActorUserId uniqueidentifier AS
                    BEGIN EXEC [Purchasing].[SaveDraftOrderV4] @RequestId,@CanonicalInputJson,@ActorUserId,''Update''; END;';
                DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
                SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
                SET @Readiness=REPLACE(@Readiness,N'20260918010000_RemoveHistoricalDraftReplay',N'20260917010000_AddSupplierBasedDraftPricing');
                SET @Readiness=REPLACE(@Readiness,N'20260917080000_ConsolidateBetaDraftCommands',N'20260917010000_AddSupplierBasedDraftPricing');
                EXEC sys.sp_executesql @Readiness;
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("THROW 50020, 'Retained beta financial preparation requires completing the forward transition.', 1;");
}
