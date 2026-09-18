// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations;

public partial class HardenPurchaseOrderDocumentAuthority : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DECLARE @Prepare nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.PreparePurchaseOrderDocument'));
            DECLARE @Start nvarchar(max)=N'SET NOCOUNT ON; SET XACT_ABORT ON;';
            DECLARE @Replay nvarchar(max)=N'AND OrderId=@OrderId AND Kind=@Kind';
            IF @Prepare IS NULL OR CHARINDEX(@Start,@Prepare)=0 OR CHARINDEX(@Replay,@Prepare)=0
                THROW 50020,'Unsupported purchase document authority predecessor.',1;
            SET @Prepare=REPLACE(@Prepare,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Prepare=REPLACE(@Prepare,@Start,@Start+N'
              DECLARE @CurrentTenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N''TenantId''));
              IF @CurrentTenant IS NULL OR NOT EXISTS(SELECT 1 FROM Security.fn_tenant_access(@CurrentTenant))
                OR NOT EXISTS(SELECT 1 FROM [Identity].Users WHERE TenantId=@CurrentTenant AND Id=@ActorUserId AND State=1)
                OR NOT EXISTS(SELECT 1 FROM Tenancy.Tenants WHERE Id=@CurrentTenant AND IsEnabled=1)
                THROW 50403,''Current tenant authority required.'',1;');
            SET @Prepare=REPLACE(@Prepare,@Replay,N'AND ActorUserId=@ActorUserId '+@Replay);
            EXEC sys.sp_executesql @Prepare;

            DECLARE @Finish nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.FinishPurchaseOrderDocument'));
            DECLARE @Finalize nvarchar(max)=N'IF @State<>0 RETURN;';
            IF @Finish IS NULL OR CHARINDEX(@Finalize,@Finish)=0
                THROW 50020,'Unsupported purchase document finalization predecessor.',1;
            SET @Finish=REPLACE(@Finish,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            -- Hold the authority reads through the existing finalization transaction. A
            -- suspension during provider I/O must retire published bytes, not expose them.
            SET @Finish=REPLACE(@Finish,@Finalize,@Finalize+N'
              IF NOT EXISTS(SELECT 1 FROM Tenancy.Tenants WITH(HOLDLOCK) WHERE Id=@TenantId AND IsEnabled=1)
                SET @Conflict=1;
              IF NOT EXISTS(SELECT 1 FROM [Identity].Users WITH(HOLDLOCK) WHERE TenantId=@TenantId AND Id=@ActorUserId AND State=1)
                SET @Conflict=1;');
            EXEC sys.sp_executesql @Finish;

            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness'));
            IF @Readiness IS NULL OR CHARINDEX(N'20260918061646_AddPurchaseOrderDocuments',@Readiness)=0
                THROW 50020,'Unsupported document readiness predecessor.',1;
            SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Readiness=REPLACE(@Readiness,N'20260918061646_AddPurchaseOrderDocuments',N'20260918063409_HardenPurchaseOrderDocumentAuthority');
            EXEC sys.sp_executesql @Readiness;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("THROW 50020, 'Purchase document authority requires forward correction or paired recovery.', 1;");
}
