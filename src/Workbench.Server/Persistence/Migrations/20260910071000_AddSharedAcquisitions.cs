// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence.Migrations;

[DbContext(typeof(WorkbenchDbContext))]
[Migration("20260910071000_AddSharedAcquisitions")]
public sealed partial class AddSharedAcquisitions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex("IX_Acquisitions_TenantId_CreatedAtUtc_Id", "Acquisitions", new[] { "TenantId", "CreatedAtUtc", "Id" }, "Inventory");
        migrationBuilder.Sql("""
            CREATE PROCEDURE [Inventory].[ChangeAcquisitionLink]
                @ItemId uniqueidentifier, @ExpectedItemVersion varbinary(max),
                @ExpectedAcquisitionId uniqueidentifier=NULL, @ExpectedAcquisitionVersion varbinary(max)=NULL,
                @TargetAcquisitionId uniqueidentifier=NULL, @TargetAcquisitionVersion varbinary(max)=NULL
            AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                IF @@TRANCOUNT=0 OR @ExpectedItemVersion IS NULL OR DATALENGTH(@ExpectedItemVersion)<>8
                    OR (@ExpectedAcquisitionId IS NULL AND @ExpectedAcquisitionVersion IS NOT NULL)
                    OR (@ExpectedAcquisitionId IS NOT NULL AND (@ExpectedAcquisitionVersion IS NULL OR DATALENGTH(@ExpectedAcquisitionVersion)<>8))
                    OR (@TargetAcquisitionId IS NULL AND @TargetAcquisitionVersion IS NOT NULL)
                    OR (@TargetAcquisitionId IS NOT NULL AND (@TargetAcquisitionVersion IS NULL OR DATALENGTH(@TargetAcquisitionVersion)<>8))
                    OR @ExpectedAcquisitionId='00000000-0000-0000-0000-000000000000'
                    OR @TargetAcquisitionId='00000000-0000-0000-0000-000000000000'
                    THROW 50045, 'Link commands require a transaction and consistent eight-byte versions.', 1;
                DECLARE @TenantId uniqueidentifier, @Version binary(8), @Archived datetimeoffset, @Current uniqueidentifier;
                SELECT @TenantId=TenantId,@Version=RowVersion,@Archived=ArchivedAtUtc
                    FROM Inventory.Items WITH (UPDLOCK,HOLDLOCK) WHERE Id=@ItemId;
                IF @TenantId IS NULL BEGIN SELECT 0; RETURN; END;
                SELECT @Current=AcquisitionId FROM Inventory.AcquisitionItems WHERE TenantId=@TenantId AND ItemId=@ItemId;
                -- Check identifier visibility before exposing active or stale state. RLS also protects direct SQL callers.
                IF (@ExpectedAcquisitionId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM Inventory.Acquisitions WHERE TenantId=@TenantId AND Id=@ExpectedAcquisitionId))
                    OR (@TargetAcquisitionId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM Inventory.Acquisitions WHERE TenantId=@TenantId AND Id=@TargetAcquisitionId))
                    BEGIN SELECT 0; RETURN; END;
                -- Lock one key at a time in SQL Server UUID order, independent of replacement direction.
                DECLARE @First uniqueidentifier, @Second uniqueidentifier, @FirstVersion binary(8), @SecondVersion binary(8);
                SELECT TOP (1) @First=Id FROM Inventory.Acquisitions WHERE TenantId=@TenantId AND Id IN (@ExpectedAcquisitionId,@TargetAcquisitionId) ORDER BY Id;
                SELECT @Second=Id FROM Inventory.Acquisitions WHERE TenantId=@TenantId AND Id IN (@ExpectedAcquisitionId,@TargetAcquisitionId) AND Id<>@First;
                SELECT @FirstVersion=RowVersion FROM Inventory.Acquisitions WITH (UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@First;
                IF @Second IS NOT NULL
                    SELECT @SecondVersion=RowVersion FROM Inventory.Acquisitions WITH (UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@Second;
                IF @Archived IS NOT NULL BEGIN SELECT 3; RETURN; END;
                IF @Version<>@ExpectedItemVersion OR (@Current IS NULL AND @ExpectedAcquisitionId IS NOT NULL)
                    OR (@Current IS NOT NULL AND @ExpectedAcquisitionId IS NULL) OR @Current<>@ExpectedAcquisitionId
                    OR (@ExpectedAcquisitionId IS NOT NULL AND @ExpectedAcquisitionVersion<>CASE WHEN @ExpectedAcquisitionId=@First THEN @FirstVersion ELSE @SecondVersion END)
                    OR (@TargetAcquisitionId IS NOT NULL AND @TargetAcquisitionVersion<>CASE WHEN @TargetAcquisitionId=@First THEN @FirstVersion ELSE @SecondVersion END)
                    BEGIN SELECT 2; RETURN; END;
                IF @Current=@TargetAcquisitionId OR (@Current IS NULL AND @TargetAcquisitionId IS NULL)
                    BEGIN SELECT 1; RETURN; END;
                IF @Current IS NOT NULL DELETE Inventory.AcquisitionItems WHERE TenantId=@TenantId AND ItemId=@ItemId;
                IF @TargetAcquisitionId IS NOT NULL
                    INSERT Inventory.AcquisitionItems (TenantId,ItemId,AcquisitionId) VALUES (@TenantId,@ItemId,@TargetAcquisitionId);
                UPDATE Inventory.Acquisitions SET Method=Method WHERE TenantId=@TenantId AND Id IN (@Current,@TargetAcquisitionId);
                UPDATE Inventory.Items SET Name=Name WHERE TenantId=@TenantId AND Id=@ItemId;
                SELECT 1;
            END;
            """);
        migrationBuilder.Sql("""
            GRANT EXECUTE ON [Inventory].[ChangeAcquisitionLink] TO [workbench_web];
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Readiness=REPLACE(@Readiness,N'20260909034719_AddAcquisitionContext',N'20260910071000_AddSharedAcquisitions');
            EXEC sys.sp_executesql @Readiness;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("THROW 50020, 'Shared acquisition relationships require forward correction or guarded recovery; destructive rollback is disabled.', 1;");
}
