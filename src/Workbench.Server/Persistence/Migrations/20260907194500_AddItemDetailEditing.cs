// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence.Migrations;

public partial class AddItemDetailEditing : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ItemCreationSnapshots", schema: "Inventory",
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                StorageLocation = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ItemCreationSnapshots", row => new { row.TenantId, row.ItemId });
                table.ForeignKey(name: "FK_ItemCreationSnapshots_Items_TenantId_ItemId",
                    columns: row => new { row.TenantId, row.ItemId }, principalSchema: "Inventory",
                    principalTable: "Items", principalColumns: ["TenantId", "Id"],
                    onDelete: ReferentialAction.Restrict);
            });
        migrationBuilder.Sql("""
            ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Inventory].[ItemCreationSnapshots],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Inventory].[ItemCreationSnapshots] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Inventory].[ItemCreationSnapshots] AFTER UPDATE;
            GRANT SELECT ON [Inventory].[ItemCreationSnapshots] TO [workbench_web];
            DENY INSERT, UPDATE, DELETE ON [Inventory].[ItemCreationSnapshots] TO [workbench_web];
            """);
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
                DECLARE @Original TABLE (TenantId uniqueidentifier, ItemId uniqueidentifier,
                    Name nvarchar(200), Notes nvarchar(4000), StorageLocation nvarchar(200));
                UPDATE [Inventory].[Items] SET [Name]=@Name, [Notes]=@Notes, [StorageLocation]=@Location
                    OUTPUT deleted.TenantId, deleted.Id, deleted.Name, deleted.Notes, deleted.StorageLocation INTO @Original
                    WHERE [Id]=@Id AND [RowVersion]=@ExpectedVersion;
                IF @@ROWCOUNT = 1
                BEGIN
                    -- The item update lock serializes first capture with every later edit.
                    -- Replay evidence and the text change commit or roll back together.
                    INSERT [Inventory].[ItemCreationSnapshots] (TenantId,ItemId,Name,Notes,StorageLocation)
                        SELECT TenantId,ItemId,Name,Notes,StorageLocation FROM @Original o
                        WHERE NOT EXISTS (SELECT 1 FROM [Inventory].[ItemCreationSnapshots] s
                            WHERE s.TenantId=o.TenantId AND s.ItemId=o.ItemId);
                    SELECT 1;
                END
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
        // A migrator is still subject to RLS: never infer absence from its filtered view.
        migrationBuilder.Sql("THROW 50020, 'Creation replay evidence requires a forward correction or paired offline recovery.', 1;");
    }
}
