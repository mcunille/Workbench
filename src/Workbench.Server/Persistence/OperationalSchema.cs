// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class OperationalSchema
{
    public static void CreateQueueProcedures(MigrationBuilder migration)
    {
        migration.Sql("""
            CREATE ROLE [workbench_worker];
            CREATE TABLE [Security].[BlobRecoveryState]
            (
                [Id] int NOT NULL PRIMARY KEY CHECK ([Id] = 1),
                [IsPending] bit NOT NULL
            );
            INSERT INTO [Security].[BlobRecoveryState] VALUES (1, 0);
            ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Operations].[WorkItems],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Operations].[WorkItems] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Operations].[WorkItems] AFTER UPDATE;
            GRANT SELECT, INSERT ON [Operations].[WorkItems] TO [workbench_web];
            GRANT SELECT ON [Operations].[WorkItems] TO [workbench_worker];
            GRANT SELECT ON [Storage].[Attachments] TO [workbench_worker];
            GRANT SELECT ON [Storage].[Revisions] TO [workbench_worker];
            GRANT SELECT ON [Identity].[IdentityOperations] TO [workbench_worker];
            GRANT SELECT ON [Identity].[Users] TO [workbench_worker];
            GRANT SELECT ON [Identity].[DataProtectionKeys] TO [workbench_worker];
            GRANT SELECT ON [Security].[fn_tenant_access] TO [workbench_worker];
            GRANT INSERT ON [Security].[TenantSecurityAuditEvents] TO [workbench_worker];
            """);
        migration.Sql("""
            CREATE PROCEDURE [Operations].[ClaimWork] @Owner uniqueidentifier
            WITH EXECUTE AS OWNER AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                IF @Owner IS NULL OR @Owner = '00000000-0000-0000-0000-000000000000'
                    THROW 50022, 'A worker owner is required.', 1;
                IF EXISTS (SELECT 1 FROM [Security].[WorkbenchRestorePending] WHERE [IsPending] = 1)
                    OR EXISTS (SELECT 1 FROM [Security].[BlobRecoveryState] WHERE [IsPending] = 1)
                    THROW 50022, 'Work processing is paused.', 1;
                DECLARE @Now datetimeoffset = SYSUTCDATETIME();
                UPDATE [Operations].[WorkItems]
                SET [State] = 3, [ProtectedPayload] = NULL, [Outcome] = N'AttemptsExhausted',
                    [LeaseOwner] = NULL, [LeaseExpiresAtUtc] = NULL
                WHERE [State] = 1 AND [Attempts] >= 5 AND [LeaseExpiresAtUtc] <= @Now;
                ;WITH candidate AS (
                    SELECT TOP (1) * FROM [Operations].[WorkItems] WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE ([State] = 0 AND [AvailableAtUtc] <= @Now OR [State] = 1 AND [LeaseExpiresAtUtc] <= @Now)
                        AND [Attempts] < 5
                    ORDER BY [AvailableAtUtc], [Id]
                )
                UPDATE candidate SET [State] = 1, [Attempts] = [Attempts] + 1,
                    [Generation] = [Generation] + 1, [LeaseOwner] = @Owner,
                    [LeaseExpiresAtUtc] = DATEADD(second, 120, @Now)
                OUTPUT inserted.[Id], inserted.[TenantId], inserted.[Kind],
                    COALESCE(inserted.[AttachmentId], inserted.[IdentityOperationId]), inserted.[Generation], inserted.[Attempts];
            END;
            """);
        migration.Sql("""
            CREATE PROCEDURE [Operations].[CompleteWork]
                @Id uniqueidentifier, @Owner uniqueidentifier, @Generation bigint
            AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                DECLARE @Completed TABLE ([TenantId] uniqueidentifier, [Kind] int, [AttachmentId] uniqueidentifier);
                UPDATE [Operations].[WorkItems]
                SET [State] = 2, [ProtectedPayload] = NULL, [Outcome] = N'Completed',
                    [LeaseOwner] = NULL, [LeaseExpiresAtUtc] = NULL
                OUTPUT inserted.[TenantId], inserted.[Kind], inserted.[AttachmentId] INTO @Completed
                WHERE [Id] = @Id AND [LeaseOwner] = @Owner AND [Generation] = @Generation
                    AND [State] = 1 AND [LeaseExpiresAtUtc] > SYSUTCDATETIME();
                UPDATE r SET [State] = CASE WHEN r.[State] = 1 THEN 3 ELSE 2 END
                FROM [Storage].[Revisions] r JOIN @Completed c
                    ON c.[TenantId] = r.[TenantId] AND c.[AttachmentId] = r.[AttachmentId]
                WHERE c.[Kind] = 1 AND r.[State] IN (0,1);
                INSERT INTO [Security].[TenantSecurityAuditEvents]
                    ([Id], [TenantId], [Action], [TargetType], [TargetId], [Outcome], [OccurredAtUtc])
                    SELECT NEWID(), [TenantId], N'operations.work.completed', N'WorkItem', @Id,
                        N'Succeeded', SYSUTCDATETIME() FROM @Completed;
                COMMIT;
                SELECT COUNT(*) FROM @Completed;
            END;
            """);
        migration.Sql("""
            CREATE PROCEDURE [Operations].[RetryWork]
                @Id uniqueidentifier, @Owner uniqueidentifier, @Generation bigint, @Transient bit
            AS
            BEGIN
                SET NOCOUNT ON;
                DECLARE @Now datetimeoffset = SYSUTCDATETIME();
                UPDATE [Operations].[WorkItems]
                SET [State] = CASE WHEN @Transient = 1 AND [Attempts] < 5 THEN 0 ELSE 3 END,
                    [ProtectedPayload] = CASE WHEN @Transient = 1 AND [Attempts] < 5 THEN [ProtectedPayload] ELSE NULL END,
                    [Outcome] = CASE WHEN @Transient = 1 THEN N'ProviderUnavailable' ELSE N'Rejected' END,
                    [AvailableAtUtc] = DATEADD(second, CONVERT(int, POWER(2, [Attempts])) + ABS(CHECKSUM(NEWID()) % 5), @Now),
                    [LeaseOwner] = NULL, [LeaseExpiresAtUtc] = NULL
                WHERE [Id] = @Id AND [LeaseOwner] = @Owner AND [Generation] = @Generation
                    AND [State] = 1 AND [LeaseExpiresAtUtc] > @Now;
                SELECT @@ROWCOUNT;
            END;
            """);
        migration.Sql("""
            CREATE PROCEDURE [Operations].[LockWork]
                @Id uniqueidentifier, @Owner uniqueidentifier, @Generation bigint
            AS
            BEGIN
                SET NOCOUNT ON;
                IF @@TRANCOUNT = 0 THROW 50022, 'Work execution requires a transaction.', 1;
                SELECT COUNT(*) FROM [Operations].[WorkItems] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = @Id AND [LeaseOwner] = @Owner AND [Generation] = @Generation
                    AND [State] = 1 AND [LeaseExpiresAtUtc] > SYSUTCDATETIME();
            END;
            """);
        migration.Sql("""
            GRANT EXECUTE ON [Operations].[ClaimWork] TO [workbench_worker];
            GRANT EXECUTE ON [Operations].[CompleteWork] TO [workbench_worker];
            GRANT EXECUTE ON [Operations].[RetryWork] TO [workbench_worker];
            GRANT EXECUTE ON [Operations].[LockWork] TO [workbench_worker];
            """);
        UpdateRecovery(migration);
        StorageMaintenanceSchema.Create(migration);
    }

    private static void UpdateRecovery(MigrationBuilder migration)
    {
        migration.Sql("""
            DECLARE @Readiness nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness = REPLACE(@Readiness, N'CREATE PROCEDURE', N'ALTER PROCEDURE');
            SET @Readiness = REPLACE(@Readiness, N'20260904061246_EstablishSecurityBoundaries', N'20260905222755_AddDurableWork');
            EXEC sys.sp_executesql @Readiness;
            DECLARE @Sanitize nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'[Administration].[SanitizeRestore]'));
            SET @Sanitize = REPLACE(@Sanitize, N'CREATE PROCEDURE', N'ALTER PROCEDURE');
            SET @Sanitize = REPLACE(@Sanitize, N'DELETE FROM [Identity].[IdentityOperations];', N'
                DELETE FROM [Operations].[WorkItems] WHERE [Kind] = 2;
                DELETE FROM [Identity].[IdentityOperations];
                UPDATE [Operations].[WorkItems] SET [State] = 0, [Attempts] = 0,
                    [Generation] = [Generation] + 1, [LeaseOwner] = NULL, [LeaseExpiresAtUtc] = NULL
                    WHERE [State] IN (0,1);
                UPDATE [Security].[BlobRecoveryState] SET [IsPending] =
                    CASE WHEN EXISTS (SELECT 1 FROM [Storage].[Revisions] WHERE [State] = 1) THEN 1 ELSE 0 END
                    WHERE [Id] = 1;
                ');
            EXEC sys.sp_executesql @Sanitize;
            """);
        migration.Sql("""
            CREATE PROCEDURE [Security].[ReadOperationalReadiness]
            WITH EXECUTE AS OWNER AS
            BEGIN
                SET NOCOUNT ON;
                SELECT CONVERT(bit, CASE WHEN
                    EXISTS (SELECT 1 FROM [Security].[BlobRecoveryState] WHERE [Id] = 1 AND [IsPending] = 0)
                    AND (SELECT COUNT(*) FROM sys.security_predicates
                        WHERE [object_id] = OBJECT_ID(N'[Security].[TenantIsolationPolicy]')
                        AND [target_object_id] IN (OBJECT_ID(N'[Storage].[Attachments]'), OBJECT_ID(N'[Storage].[Revisions]'), OBJECT_ID(N'[Operations].[WorkItems]'))) = 9
                    AND EXISTS (SELECT 1 FROM sys.triggers WHERE [object_id] = OBJECT_ID(N'[Storage].[ProtectRevision]') AND [is_disabled] = 0)
                    THEN 1 ELSE 0 END);
            END;
            """);
        migration.Sql("GRANT EXECUTE ON [Security].[ReadOperationalReadiness] TO [workbench_web];");
    }
}
