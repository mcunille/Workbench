// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class StorageMaintenanceSchema
{
    public static void Create(MigrationBuilder migration)
    {
        migration.Sql("CREATE ROLE [workbench_storage_maintenance];");
        migration.Sql("""
            CREATE PROCEDURE [Storage].[AssertMigrationReady]
            WITH EXECUTE AS OWNER AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM [Storage].[Revisions] WHERE [State] = 0)
                    THROW 50023, 'Resolve pending revisions before provider migration.', 1;
            END;
            """);
        migration.Sql("""
            CREATE PROCEDURE [Storage].[ExportManifest] @AfterId uniqueidentifier = NULL
            WITH EXECUTE AS OWNER AS
            BEGIN
                SET NOCOUNT ON;
                SELECT TOP (500) [TenantId], [Id], [ProviderAlias], [Length], [Sha256]
                FROM [Storage].[Revisions]
                WHERE [State] = 1 AND (@AfterId IS NULL OR [Id] > @AfterId)
                ORDER BY [Id];
            END;
            """);
        migration.Sql("""
            CREATE PROCEDURE [Storage].[RelocateRevision]
                @TenantId uniqueidentifier, @Id uniqueidentifier,
                @OldAlias nvarchar(64), @NewAlias nvarchar(64), @Length bigint, @Sha256 varchar(64)
            WITH EXECUTE AS OWNER AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                IF @NewAlias IS NULL OR LEN(@NewAlias) = 0 THROW 50023, 'A provider alias is required.', 1;
                UPDATE [Storage].[Revisions] SET [ProviderAlias] = @NewAlias
                WHERE [TenantId] = @TenantId AND [Id] = @Id AND [ProviderAlias] = @OldAlias
                    AND [State] = 1 AND [Length] = @Length AND [Sha256] = @Sha256;
                IF @@ROWCOUNT <> 1 THROW 50023, 'Revision changed during migration.', 1;
                INSERT INTO [Security].[SystemSecurityAuditEvents] ([Id], [Action], [Outcome], [OccurredAtUtc])
                VALUES (NEWID(), N'storage.provider-migrated', N'Succeeded', SYSUTCDATETIME());
            END;
            """);
        migration.Sql("""
            CREATE PROCEDURE [Storage].[CompleteRecoveryVerification]
            WITH EXECUTE AS OWNER AS
            BEGIN
                SET NOCOUNT ON;
                UPDATE [Security].[BlobRecoveryState] SET [IsPending] = 0 WHERE [Id] = 1;
                INSERT INTO [Security].[SystemSecurityAuditEvents] ([Id], [Action], [Outcome], [OccurredAtUtc])
                VALUES (NEWID(), N'storage.restore-verified', N'Succeeded', SYSUTCDATETIME());
            END;
            """);
        migration.Sql("""
            CREATE PROCEDURE [Storage].[ReplayDeletion] @Id uniqueidentifier
            WITH EXECUTE AS OWNER AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                UPDATE [Operations].[WorkItems] SET [State] = 0, [Attempts] = 0,
                    [Generation] = [Generation] + 1, [AvailableAtUtc] = SYSUTCDATETIME(), [Outcome] = NULL
                WHERE [Id] = @Id AND [Kind] = 1 AND [State] = 3;
                IF @@ROWCOUNT <> 1 THROW 50023, 'Deletion work is not replayable.', 1;
                INSERT INTO [Security].[SystemSecurityAuditEvents] ([Id], [Action], [Outcome], [OccurredAtUtc])
                VALUES (NEWID(), N'storage.deletion-replayed', N'Succeeded', SYSUTCDATETIME());
                COMMIT TRANSACTION;
            END;
            """);
        migration.Sql("""
            GRANT EXECUTE ON [Storage].[ExportManifest] TO [workbench_storage_maintenance];
            GRANT EXECUTE ON [Storage].[AssertMigrationReady] TO [workbench_storage_maintenance];
            GRANT EXECUTE ON [Storage].[RelocateRevision] TO [workbench_storage_maintenance];
            GRANT EXECUTE ON [Storage].[CompleteRecoveryVerification] TO [workbench_storage_maintenance];
            GRANT EXECUTE ON [Storage].[ReplayDeletion] TO [workbench_storage_maintenance];
            """);
    }
}
