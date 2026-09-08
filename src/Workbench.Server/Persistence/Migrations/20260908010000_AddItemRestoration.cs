// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence.Migrations;

public partial class AddItemRestoration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE PROCEDURE [Inventory].[RestoreItem]
                @Id uniqueidentifier, @ExpectedVersion varbinary(max)
            AS
            BEGIN
                SET NOCOUNT ON;
                IF @@TRANCOUNT = 0 OR @ExpectedVersion IS NULL OR DATALENGTH(@ExpectedVersion) <> 8
                    THROW 50044, 'Restoration requires a transaction and an eight-byte version.', 1;
                -- Hold the visible record stable through outcome classification and the caller's detail read.
                DECLARE @CurrentVersion binary(8), @ArchivedAtUtc datetimeoffset;
                SELECT @CurrentVersion=[RowVersion], @ArchivedAtUtc=[ArchivedAtUtc]
                    FROM [Inventory].[Items] WITH (UPDLOCK, HOLDLOCK) WHERE [Id]=@Id;
                IF @CurrentVersion IS NULL SELECT 0;
                ELSE IF @ArchivedAtUtc IS NULL SELECT 3;
                ELSE IF @CurrentVersion <> @ExpectedVersion SELECT 2;
                ELSE
                BEGIN
                    UPDATE [Inventory].[Items] SET [ArchivedAtUtc]=NULL
                        WHERE [Id]=@Id AND [RowVersion]=@ExpectedVersion AND [ArchivedAtUtc] IS NOT NULL;
                    SELECT 1;
                END;
            END;
            """);
        migrationBuilder.Sql("""
            GRANT EXECUTE ON [Inventory].[RestoreItem] TO [workbench_web];
            DECLARE @Readiness nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness = REPLACE(@Readiness, N'CREATE PROCEDURE', N'ALTER PROCEDURE');
            SET @Readiness = REPLACE(@Readiness, N'20260907224158_AddItemArchiving', N'20260908010000_AddItemRestoration');
            EXEC sys.sp_executesql @Readiness;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP PROCEDURE [Inventory].[RestoreItem];
            DECLARE @Readiness nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness = REPLACE(@Readiness, N'CREATE PROCEDURE', N'ALTER PROCEDURE');
            SET @Readiness = REPLACE(@Readiness, N'20260908010000_AddItemRestoration', N'20260907224158_AddItemArchiving');
            EXEC sys.sp_executesql @Readiness;
            """);
    }
}
