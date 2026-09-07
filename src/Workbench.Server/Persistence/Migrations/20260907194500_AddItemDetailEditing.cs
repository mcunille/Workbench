// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence.Migrations;

public partial class AddItemDetailEditing : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE PROCEDURE [Inventory].[UpdateItemDetails]
                @Id uniqueidentifier, @ExpectedVersion binary(8),
                @Name nvarchar(max), @Notes nvarchar(max), @Location nvarchar(max)
            AS
            BEGIN
                SET NOCOUNT ON;
                IF @@TRANCOUNT = 0 THROW 50042, 'Item editing requires a transaction.', 1;
                -- MAX parameters prevent SQL from truncating input before validation.
                IF @ExpectedVersion IS NULL OR @Name IS NULL OR DATALENGTH(@Name) > 400
                    OR LEN(TRIM(NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+NCHAR(8239)+NCHAR(8287)+NCHAR(12288) FROM @Name)) = 0
                    OR DATALENGTH(@Notes) > 8000 OR DATALENGTH(@Location) > 400
                    THROW 50042, 'Invalid item details.', 1;
                -- Caller RLS and ownership chaining permit only the checked descriptive update.
                UPDATE [Inventory].[Items] SET [Name]=@Name, [Notes]=@Notes, [StorageLocation]=@Location
                    WHERE [Id]=@Id AND [RowVersion]=@ExpectedVersion;
                IF @@ROWCOUNT = 1 SELECT 1;
                ELSE IF EXISTS (SELECT 1 FROM [Inventory].[Items] WHERE [Id]=@Id) SELECT 2;
                ELSE SELECT 0;
            END;
            """);
        migrationBuilder.Sql("""
            GRANT EXECUTE ON [Inventory].[UpdateItemDetails] TO [workbench_web];
            DECLARE @Readiness nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness = REPLACE(@Readiness, N'CREATE PROCEDURE', N'ALTER PROCEDURE');
            SET @Readiness = REPLACE(@Readiness, N'20260907082353_AddItemPhotographs', N'20260907194500_AddItemDetailEditing');
            EXEC sys.sp_executesql @Readiness;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP PROCEDURE [Inventory].[UpdateItemDetails];
            DECLARE @Readiness nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness = REPLACE(@Readiness, N'CREATE PROCEDURE', N'ALTER PROCEDURE');
            SET @Readiness = REPLACE(@Readiness, N'20260907194500_AddItemDetailEditing', N'20260907082353_AddItemPhotographs');
            EXEC sys.sp_executesql @Readiness;
            """);
    }
}
