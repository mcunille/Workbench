// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations;

[DbContext(typeof(WorkbenchDbContext))]
[Migration("20260902070000_HardenSecurityBoundaries")]
public sealed class HardenSecurityBoundaries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE [Security].[TenantContextKeys]
            (
                [Id] tinyint NOT NULL CONSTRAINT [PK_TenantContextKeys] PRIMARY KEY,
                [ProofKey] binary(32) NOT NULL,
                CONSTRAINT [CK_TenantContextKeys_Singleton] CHECK ([Id] = 1)
            );
            INSERT INTO [Security].[TenantContextKeys] ([Id], [ProofKey])
            VALUES (1, CRYPT_GEN_RANDOM(32));

            CREATE TABLE [Security].[SensitiveRequestLimits]
            (
                [PartitionHash] binary(32) NOT NULL CONSTRAINT [PK_SensitiveRequestLimits] PRIMARY KEY,
                [WindowStartedAtUtc] datetimeoffset NOT NULL,
                [RequestCount] int NOT NULL,
                CONSTRAINT [CK_SensitiveRequestLimits_RequestCount] CHECK ([RequestCount] > 0)
            );

            IF OBJECT_ID(N'[Security].[WorkbenchRestorePending]', N'U') IS NULL
            BEGIN
                CREATE TABLE [Security].[WorkbenchRestorePending]
                (
                    [Id] tinyint NOT NULL CONSTRAINT [PK_WorkbenchRestorePending] PRIMARY KEY,
                    [IsPending] bit NOT NULL,
                    CONSTRAINT [CK_WorkbenchRestorePending_Singleton] CHECK ([Id] = 1)
                );
            END;
            IF NOT EXISTS (SELECT 1 FROM [Security].[WorkbenchRestorePending] WHERE [Id] = 1)
                INSERT INTO [Security].[WorkbenchRestorePending] ([Id], [IsPending]) VALUES (1, 0);
            """);

        migrationBuilder.Sql("DROP SECURITY POLICY [Security].[TenantIsolationPolicy];");

        migrationBuilder.Sql("""
            ALTER FUNCTION [Security].[fn_tenant_access](@TenantId uniqueidentifier)
            RETURNS TABLE
            WITH SCHEMABINDING
            AS
            RETURN
            (
                SELECT 1 AS [is_allowed]
                WHERE USER_NAME() = N'dbo'
                    OR
                    (
                        @TenantId = TRY_CONVERT(uniqueidentifier, SESSION_CONTEXT(N'TenantId'))
                        AND TRY_CONVERT(varbinary(32), SESSION_CONTEXT(N'TenantProof')) =
                        (
                            SELECT HASHBYTES(
                                'SHA2_256',
                                CONVERT(varbinary(max), [key].[ProofKey]) +
                                CONVERT(varbinary(max), CONVERT(nvarchar(36), @TenantId)) +
                                CONVERT(varbinary(max), TRY_CONVERT(varbinary(32), SESSION_CONTEXT(N'TenantNonce'))))
                            FROM [Security].[TenantContextKeys] AS [key]
                            WHERE [key].[Id] = 1
                        )
                    )
            );
            """);

        migrationBuilder.Sql("""
            CREATE SECURITY POLICY [Security].[TenantIsolationPolicy]
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([Id]) ON [Tenancy].[Tenants],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([Id]) ON [Tenancy].[Tenants] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([Id]) ON [Tenancy].[Tenants] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Security].[TenantSecurityAuditEvents],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Security].[TenantSecurityAuditEvents] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Security].[TenantSecurityAuditEvents] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Users],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Users] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Users] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Roles],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Roles] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Roles] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserClaims],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserClaims] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserClaims] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserLogins],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserLogins] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserLogins] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserRoles],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserRoles] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserRoles] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[RoleClaims],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[RoleClaims] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[RoleClaims] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserTokens],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserTokens] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserTokens] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Sessions],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Sessions] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Sessions] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[IdentityOperations],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[IdentityOperations] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[IdentityOperations] AFTER UPDATE
            WITH (STATE = ON, SCHEMABINDING = ON);
            """);

        migrationBuilder.Sql("""
            CREATE PROCEDURE [Security].[TryAcquireSensitiveRequest]
                @PartitionHash binary(32),
                @Now datetimeoffset,
                @WindowSeconds int,
                @PermitLimit int
            WITH EXECUTE AS OWNER
            AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
                BEGIN TRANSACTION;

                DECLARE @WindowStartedAtUtc datetimeoffset;
                DECLARE @RequestCount int;
                SELECT @WindowStartedAtUtc = [WindowStartedAtUtc], @RequestCount = [RequestCount]
                FROM [Security].[SensitiveRequestLimits] WITH (UPDLOCK, HOLDLOCK)
                WHERE [PartitionHash] = @PartitionHash;

                IF @WindowStartedAtUtc IS NULL
                BEGIN
                    INSERT INTO [Security].[SensitiveRequestLimits]
                        ([PartitionHash], [WindowStartedAtUtc], [RequestCount])
                    VALUES (@PartitionHash, @Now, 1);
                    SET @RequestCount = 1;
                END
                ELSE IF DATEADD(second, @WindowSeconds, @WindowStartedAtUtc) <= @Now
                BEGIN
                    UPDATE [Security].[SensitiveRequestLimits]
                    SET [WindowStartedAtUtc] = @Now, [RequestCount] = 1
                    WHERE [PartitionHash] = @PartitionHash;
                    SET @RequestCount = 1;
                END
                ELSE
                BEGIN
                    SET @RequestCount = @RequestCount + 1;
                    UPDATE [Security].[SensitiveRequestLimits]
                    SET [RequestCount] = @RequestCount
                    WHERE [PartitionHash] = @PartitionHash;
                END;

                COMMIT TRANSACTION;
                SELECT CONVERT(bit, CASE WHEN @RequestCount <= @PermitLimit THEN 1 ELSE 0 END);
            END;
            """);

        migrationBuilder.Sql("""
            CREATE PROCEDURE [Administration].[MarkRestorePending]
            WITH EXECUTE AS OWNER
            AS
            BEGIN
                SET NOCOUNT ON;
                UPDATE [Security].[WorkbenchRestorePending] SET [IsPending] = 1 WHERE [Id] = 1;
            END;
            """);

        migrationBuilder.Sql("""
            ALTER PROCEDURE [Administration].[SanitizeRestore]
                @Now datetimeoffset,
                @CorrelationId nvarchar(100)
            WITH EXECUTE AS OWNER
            AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
                BEGIN TRANSACTION;

                UPDATE [Security].[DatabaseSecurityState] WITH (UPDLOCK, HOLDLOCK)
                SET [RestoreGeneration] = [RestoreGeneration] + 1
                WHERE [Id] = 1;
                DELETE FROM [Identity].[Sessions];
                DELETE FROM [Identity].[IdentityOperations];
                DELETE FROM [Identity].[DataProtectionKeys];
                DELETE FROM [Security].[SensitiveRequestLimits];
                UPDATE [Identity].[Users]
                SET [SecurityVersion] = [SecurityVersion] + 1,
                    [SecurityStamp] = CONVERT(nvarchar(36), NEWID()),
                    [ConcurrencyStamp] = CONVERT(nvarchar(36), NEWID());
                UPDATE [Security].[DatabaseSecurityState]
                SET [RestoreSanitizedGeneration] = [RestoreGeneration], [SanitizedAtUtc] = @Now
                WHERE [Id] = 1;
                UPDATE [Security].[WorkbenchRestorePending] SET [IsPending] = 0 WHERE [Id] = 1;
                INSERT INTO [Security].[SystemSecurityAuditEvents]
                    ([Id], [Action], [Outcome], [CorrelationId], [MetadataJson], [OccurredAtUtc])
                VALUES
                    (NEWID(), N'database.restore-sanitized', N'Succeeded', @CorrelationId,
                     N'{"version":1}', @Now);

                COMMIT TRANSACTION;
            END;
            """);

        migrationBuilder.Sql("""
            ALTER PROCEDURE [Security].[ReadDatabaseReadiness]
            WITH EXECUTE AS OWNER
            AS
            BEGIN
                SET NOCOUNT ON;
                SELECT
                    CONVERT(bit, CASE WHEN
                        (SELECT MAX([MigrationId]) FROM [dbo].[__EFMigrationsHistory]) =
                            N'20260902070000_HardenSecurityBoundaries'
                        THEN 1 ELSE 0 END) AS [CompatibleMigration],
                    CONVERT(bit, CASE WHEN EXISTS
                    (
                        SELECT 1 FROM sys.security_policies
                        WHERE [name] = N'TenantIsolationPolicy'
                            AND SCHEMA_NAME([schema_id]) = N'Security' AND [is_enabled] = 1
                    ) THEN 1 ELSE 0 END) AS [TenantPolicyEnabled],
                    CONVERT(bit, CASE WHEN EXISTS
                    (
                        SELECT 1 FROM sys.database_permissions
                        WHERE [grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'workbench_web')
                            AND [class] = 0 AND [permission_name] = N'ALTER ANY SECURITY POLICY'
                            AND [state] = N'D'
                    ) THEN 1 ELSE 0 END) AS [TenantPolicyAlterDenied],
                    CONVERT(bit, CASE WHEN
                    (
                        SELECT COUNT(DISTINCT [permission_name]) FROM sys.database_permissions
                        WHERE [grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'workbench_web')
                            AND [class] = 1
                            AND [major_id] = OBJECT_ID(N'[Identity].[DataProtectionKeys]')
                            AND [permission_name] IN (N'SELECT', N'INSERT', N'UPDATE', N'DELETE')
                            AND [state] IN (N'G', N'W')
                    ) = 4 THEN 1 ELSE 0 END) AS [KeyTableAvailable],
                    [state].[RestoreGeneration],
                    [state].[RestoreSanitizedGeneration],
                    [restore].[IsPending] AS [RestorePending],
                    CONVERT(bit, CASE WHEN EXISTS
                    (
                        SELECT 1 FROM sys.database_permissions
                        WHERE [grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'workbench_web')
                            AND [class] = 1
                            AND [major_id] = OBJECT_ID(N'[Security].[TenantContextKeys]')
                            AND [permission_name] = N'SELECT' AND [state] = N'D'
                    ) THEN 1 ELSE 0 END) AS [TenantContextKeyProtected],
                    CONVERT(bit, CASE WHEN OBJECT_ID(N'[Security].[TryAcquireSensitiveRequest]', N'P') IS NOT NULL
                        THEN 1 ELSE 0 END) AS [SensitiveLimiterAvailable]
                FROM [Security].[DatabaseSecurityState] AS [state]
                CROSS JOIN [Security].[WorkbenchRestorePending] AS [restore]
                WHERE [state].[Id] = 1 AND [restore].[Id] = 1;
            END;
            """);

        migrationBuilder.Sql("""
            GRANT EXECUTE ON [Security].[TryAcquireSensitiveRequest] TO [workbench_web];
            GRANT SELECT ON [Security].[fn_tenant_access] TO [workbench_web];
            DENY SELECT, INSERT, UPDATE, DELETE ON [Security].[TenantContextKeys] TO [workbench_web];
            DENY SELECT, INSERT, UPDATE, DELETE ON [Security].[TenantContextKeys] TO [workbench_operator];
            DENY SELECT, INSERT, UPDATE, DELETE ON [Security].[SensitiveRequestLimits] TO [workbench_web];
            DENY SELECT, INSERT, UPDATE, DELETE ON [Security].[SensitiveRequestLimits] TO [workbench_operator];
            DENY SELECT, INSERT, UPDATE, DELETE ON [Security].[WorkbenchRestorePending] TO [workbench_web];
            DENY SELECT, INSERT, UPDATE, DELETE ON [Security].[WorkbenchRestorePending] TO [workbench_operator];
            DENY EXECUTE ON [Administration].[MarkRestorePending] TO [workbench_web];
            DENY EXECUTE ON [Administration].[MarkRestorePending] TO [workbench_operator];
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP PROCEDURE [Administration].[MarkRestorePending];");
        migrationBuilder.Sql("DROP PROCEDURE [Security].[TryAcquireSensitiveRequest];");
        migrationBuilder.Sql("""
            ALTER PROCEDURE [Administration].[SanitizeRestore]
                @Now datetimeoffset,
                @CorrelationId nvarchar(100)
            WITH EXECUTE AS OWNER
            AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
                BEGIN TRANSACTION;
                UPDATE [Security].[DatabaseSecurityState] WITH (UPDLOCK, HOLDLOCK)
                SET [RestoreGeneration] = [RestoreGeneration] + 1 WHERE [Id] = 1;
                DELETE FROM [Identity].[Sessions];
                DELETE FROM [Identity].[IdentityOperations];
                DELETE FROM [Identity].[DataProtectionKeys];
                UPDATE [Identity].[Users]
                SET [SecurityVersion] = [SecurityVersion] + 1,
                    [SecurityStamp] = CONVERT(nvarchar(36), NEWID()),
                    [ConcurrencyStamp] = CONVERT(nvarchar(36), NEWID());
                UPDATE [Security].[DatabaseSecurityState]
                SET [RestoreSanitizedGeneration] = [RestoreGeneration], [SanitizedAtUtc] = @Now
                WHERE [Id] = 1;
                INSERT INTO [Security].[SystemSecurityAuditEvents]
                    ([Id], [Action], [Outcome], [CorrelationId], [MetadataJson], [OccurredAtUtc])
                VALUES (NEWID(), N'database.restore-sanitized', N'Succeeded', @CorrelationId,
                    N'{"version":1}', @Now);
                COMMIT TRANSACTION;
            END;
            """);
        migrationBuilder.Sql("""
            ALTER PROCEDURE [Security].[ReadDatabaseReadiness]
            WITH EXECUTE AS OWNER
            AS
            BEGIN
                SET NOCOUNT ON;
                SELECT
                    CONVERT(bit, CASE WHEN
                        (SELECT MAX([MigrationId]) FROM [dbo].[__EFMigrationsHistory]) =
                            N'20260902060523_AddDatabaseSecurityState'
                        THEN 1 ELSE 0 END),
                    CONVERT(bit, CASE WHEN EXISTS
                    (
                        SELECT 1 FROM sys.security_policies
                        WHERE [name] = N'TenantIsolationPolicy'
                            AND SCHEMA_NAME([schema_id]) = N'Security' AND [is_enabled] = 1
                    ) THEN 1 ELSE 0 END),
                    CONVERT(bit, CASE WHEN EXISTS
                    (
                        SELECT 1 FROM sys.database_permissions
                        WHERE [grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'workbench_web')
                            AND [class] = 0 AND [permission_name] = N'ALTER ANY SECURITY POLICY'
                            AND [state] = N'D'
                    ) THEN 1 ELSE 0 END),
                    CONVERT(bit, CASE WHEN
                    (
                        SELECT COUNT(DISTINCT [permission_name]) FROM sys.database_permissions
                        WHERE [grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'workbench_web')
                            AND [class] = 1
                            AND [major_id] = OBJECT_ID(N'[Identity].[DataProtectionKeys]')
                            AND [permission_name] IN (N'SELECT', N'INSERT', N'UPDATE', N'DELETE')
                            AND [state] IN (N'G', N'W')
                    ) = 4 THEN 1 ELSE 0 END),
                    [RestoreGeneration], [RestoreSanitizedGeneration]
                FROM [Security].[DatabaseSecurityState] WHERE [Id] = 1;
            END;
            """);
        migrationBuilder.Sql("DROP SECURITY POLICY [Security].[TenantIsolationPolicy];");
        migrationBuilder.Sql("""
            ALTER FUNCTION [Security].[fn_tenant_access](@TenantId uniqueidentifier)
            RETURNS TABLE
            WITH SCHEMABINDING
            AS
            RETURN
            (
                SELECT 1 AS [is_allowed]
                WHERE USER_NAME() = N'dbo'
                    OR @TenantId = TRY_CONVERT(uniqueidentifier, SESSION_CONTEXT(N'TenantId'))
            );
            """);
        migrationBuilder.Sql("""
            CREATE SECURITY POLICY [Security].[TenantIsolationPolicy]
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([Id]) ON [Tenancy].[Tenants],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([Id]) ON [Tenancy].[Tenants] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([Id]) ON [Tenancy].[Tenants] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Security].[TenantSecurityAuditEvents],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Security].[TenantSecurityAuditEvents] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Security].[TenantSecurityAuditEvents] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Users],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Users] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Users] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Roles],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Roles] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Roles] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserClaims],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserClaims] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserClaims] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserLogins],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserLogins] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserLogins] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserRoles],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserRoles] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserRoles] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[RoleClaims],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[RoleClaims] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[RoleClaims] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserTokens],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserTokens] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserTokens] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Sessions],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Sessions] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Sessions] AFTER UPDATE,
                ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[IdentityOperations],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[IdentityOperations] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[IdentityOperations] AFTER UPDATE
            WITH (STATE = ON, SCHEMABINDING = ON);
            """);
        migrationBuilder.Sql("REVOKE SELECT ON [Security].[fn_tenant_access] FROM [workbench_web];");
        migrationBuilder.Sql("DROP TABLE [Security].[SensitiveRequestLimits];");
        migrationBuilder.Sql("DROP TABLE [Security].[TenantContextKeys];");
        migrationBuilder.Sql("DROP TABLE [Security].[WorkbenchRestorePending];");
    }
}
