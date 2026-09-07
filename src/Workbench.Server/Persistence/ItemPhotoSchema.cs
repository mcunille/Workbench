// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class ItemPhotoSchema
{
    internal static void Protect(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Inventory].[ItemPhotos],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Inventory].[ItemPhotos] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Inventory].[ItemPhotos] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Inventory].[ItemPhotoOperations],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Inventory].[ItemPhotoOperations] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Inventory].[ItemPhotoOperations] AFTER UPDATE;
            GRANT SELECT, INSERT ON [Inventory].[ItemPhotos] TO [workbench_web];
            DENY UPDATE, DELETE ON [Inventory].[ItemPhotos] TO [workbench_web];
            GRANT SELECT, INSERT, UPDATE ON [Inventory].[ItemPhotoOperations] TO [workbench_web];
            DENY DELETE ON [Inventory].[ItemPhotoOperations] TO [workbench_web];
            DECLARE @Readiness nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness = REPLACE(@Readiness, N'CREATE PROCEDURE', N'ALTER PROCEDURE');
            SET @Readiness = REPLACE(@Readiness, N'20260907060000_AddCollectionNotebook', N'20260907082353_AddItemPhotographs');
            EXEC sys.sp_executesql @Readiness;
            """);
        migrationBuilder.Sql("""
            CREATE PROCEDURE [Inventory].[SetItemPhoto]
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
                    WHERE [Id]=@Id AND [RowVersion]=@ExpectedVersion;
                IF @@ROWCOUNT <> 1 THROW 50040, 'The item changed.', 1;
                SELECT [RowVersion] FROM [Inventory].[Items] WHERE [Id]=@Id;
            END;
            """);
        migrationBuilder.Sql("GRANT EXECUTE ON [Inventory].[SetItemPhoto] TO [workbench_web];");
        migrationBuilder.Sql("""
            CREATE TRIGGER [Inventory].[ProtectPhotoOperation] ON [Inventory].[ItemPhotoOperations] AFTER UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                IF UPDATE([Id]) OR UPDATE([TenantId]) OR UPDATE([ItemId]) OR UPDATE([RequestId])
                    OR UPDATE([ExpectedVersion]) OR UPDATE([PayloadSha256]) OR UPDATE([Kind])
                    OR UPDATE([ActorUserId]) OR UPDATE([CreatedAtUtc]) OR UPDATE([DetailAttachmentId])
                    OR UPDATE([ThumbnailAttachmentId])
                    THROW 50041, 'Photo operation provenance is immutable.', 1;
                IF EXISTS (SELECT 1 FROM deleted WHERE [State] <> 0)
                    THROW 50041, 'Completed photo operations are immutable.', 1;
            END;
            """);
    }
}
