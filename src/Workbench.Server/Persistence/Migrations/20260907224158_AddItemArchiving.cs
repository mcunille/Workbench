// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence.Migrations;

public partial class AddItemArchiving : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(name: "ArchivedAtUtc", schema: "Inventory", table: "Items", type: "datetimeoffset", nullable: true);
        migrationBuilder.CreateIndex(name: "IX_Items_ActiveBrowsing", schema: "Inventory", table: "Items",
            columns: ["TenantId", "CreatedAtUtc", "Id"], filter: "[ArchivedAtUtc] IS NULL");
        migrationBuilder.Sql("""
            CREATE TRIGGER [Inventory].[RequireActiveItemCreation] ON [Inventory].[Items] AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM inserted WHERE [ArchivedAtUtc] IS NOT NULL)
                    THROW 50043, 'New records must be active. Use the checked archive command.', 1;
            END;
            """);
        migrationBuilder.Sql("""
            CREATE PROCEDURE [Inventory].[ArchiveItem]
                @Id uniqueidentifier, @ExpectedVersion varbinary(max)
            AS
            BEGIN
                SET NOCOUNT ON;
                IF @@TRANCOUNT = 0 OR @ExpectedVersion IS NULL OR DATALENGTH(@ExpectedVersion) <> 8
                    THROW 50043, 'Archiving requires a transaction and an eight-byte version.', 1;
                UPDATE [Inventory].[Items] SET [ArchivedAtUtc]=SYSUTCDATETIME()
                    WHERE [Id]=@Id AND [RowVersion]=@ExpectedVersion AND [ArchivedAtUtc] IS NULL;
                IF @@ROWCOUNT = 1 SELECT 1;
                ELSE IF EXISTS (SELECT 1 FROM [Inventory].[Items] WHERE [Id]=@Id AND [ArchivedAtUtc] IS NOT NULL) SELECT 3;
                ELSE IF EXISTS (SELECT 1 FROM [Inventory].[Items] WHERE [Id]=@Id) SELECT 2;
                ELSE SELECT 0;
            END;
            """);
        migrationBuilder.Sql("""
            ALTER PROCEDURE [Inventory].[UpdateItemDetails]
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
                    WHERE [Id]=@Id AND [RowVersion]=@ExpectedVersion AND [ArchivedAtUtc] IS NULL;
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
                ELSE IF EXISTS (SELECT 1 FROM [Inventory].[Items] WHERE [Id]=@Id AND [ArchivedAtUtc] IS NOT NULL) SELECT 3;
                ELSE IF EXISTS (SELECT 1 FROM [Inventory].[Items] WHERE [Id]=@Id) SELECT 2;
                ELSE SELECT 0;
            END;
            """);
        migrationBuilder.Sql("""
            ALTER PROCEDURE [Inventory].[SetItemPhoto]
                @Id uniqueidentifier, @ExpectedVersion binary(8), @PhotoId uniqueidentifier
            AS
            BEGIN
                SET NOCOUNT ON;
                IF @@TRANCOUNT = 0 THROW 50040, 'Photo publication requires a transaction.', 1;
                -- Ownership chaining grants only this pointer update, under caller RLS. The
                -- runtime remains unable to UPDATE/DELETE item identity or descriptive fields.
                IF @PhotoId IS NOT NULL AND NOT EXISTS
                (
                    SELECT 1 FROM [Inventory].[ItemPhotos] p
                    JOIN [Storage].[Attachments] d ON d.[TenantId]=p.[TenantId] AND d.[Id]=p.[DetailAttachmentId]
                    JOIN [Storage].[Revisions] dr ON dr.[TenantId]=d.[TenantId] AND dr.[Id]=d.[CurrentRevisionId]
                    JOIN [Storage].[Attachments] t ON t.[TenantId]=p.[TenantId] AND t.[Id]=p.[ThumbnailAttachmentId]
                    JOIN [Storage].[Revisions] tr ON tr.[TenantId]=t.[TenantId] AND tr.[Id]=t.[CurrentRevisionId]
                    WHERE p.[Id]=@PhotoId AND p.[ItemId]=@Id AND dr.[State]=1 AND tr.[State]=1
                        AND d.[DeletedAtUtc] IS NULL AND t.[DeletedAtUtc] IS NULL
                ) THROW 50040, 'The complete photo is unavailable.', 1;
                UPDATE [Inventory].[Items] SET [CurrentPhotoId]=@PhotoId
                    WHERE [Id]=@Id AND [RowVersion]=@ExpectedVersion AND [ArchivedAtUtc] IS NULL;
                IF @@ROWCOUNT <> 1 THROW 50040, 'The item changed.', 1;
                SELECT [RowVersion] FROM [Inventory].[Items] WHERE [Id]=@Id;
            END;
            """);
        migrationBuilder.Sql("""
            GRANT EXECUTE ON [Inventory].[ArchiveItem] TO [workbench_web];
            DECLARE @Readiness nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness = REPLACE(@Readiness, N'CREATE PROCEDURE', N'ALTER PROCEDURE');
            SET @Readiness = REPLACE(@Readiness, N'20260907194500_AddItemDetailEditing', N'20260907224158_AddItemArchiving');
            EXEC sys.sp_executesql @Readiness;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("THROW 50020, 'Archive state requires a forward correction or paired offline recovery.', 1;");
}
