// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations;

/// <inheritdoc />
public partial class EstablishSecurityBoundaries : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "Administration");

        CreateSecurityTables(migrationBuilder);
        CreateTenantIsolation(migrationBuilder);
        CreateIdentityProcedures(migrationBuilder);
        CreateAdministrationProcedures(migrationBuilder);
        CreateSecurityProcedures(migrationBuilder);
        ConfigurePermissions(migrationBuilder);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        RemovePermissions(migrationBuilder);

        migrationBuilder.Sql("DROP SECURITY POLICY [Security].[TenantIsolationPolicy];");
        migrationBuilder.Sql("DROP PROCEDURE [Security].[ReadDatabaseReadiness];");
        migrationBuilder.Sql("DROP PROCEDURE [Security].[TryAcquireSensitiveRequest];");
        migrationBuilder.Sql("DROP PROCEDURE [Administration].[MarkRestorePending];");
        migrationBuilder.Sql("DROP PROCEDURE [Administration].[CreateDevelopmentRecovery];");
        migrationBuilder.Sql("DROP PROCEDURE [Administration].[SanitizeRestore];");
        migrationBuilder.Sql("DROP PROCEDURE [Administration].[ProvisionTenant];");
        migrationBuilder.Sql("DROP PROCEDURE [Identity].[CreateInvitation];");
        migrationBuilder.Sql("DROP PROCEDURE [Identity].[ResolveOperationAuthority];");
        migrationBuilder.Sql("DROP PROCEDURE [Identity].[ResolveRecoveryTarget];");
        migrationBuilder.Sql("DROP PROCEDURE [Identity].[ResolveSession];");
        migrationBuilder.Sql("DROP PROCEDURE [Identity].[ResolveCredential];");
        migrationBuilder.Sql("DROP FUNCTION [Security].[fn_tenant_access];");
        migrationBuilder.Sql("DROP TABLE [Security].[WorkbenchRestorePending];");
        migrationBuilder.Sql("DROP TABLE [Security].[SensitiveRequestLimits];");
        migrationBuilder.Sql("DROP TABLE [Security].[TenantContextKeys];");
        migrationBuilder.Sql("DROP TABLE [Security].[DatabaseSecurityState];");
    }

    private static void CreateSecurityTables(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE [Security].[DatabaseSecurityState]
            (
                [Id] tinyint NOT NULL CONSTRAINT [PK_DatabaseSecurityState] PRIMARY KEY,
                [RestoreGeneration] bigint NOT NULL CONSTRAINT [DF_DatabaseSecurityState_RestoreGeneration] DEFAULT 0,
                [RestoreSanitizedGeneration] bigint NOT NULL CONSTRAINT [DF_DatabaseSecurityState_RestoreSanitizedGeneration] DEFAULT 0,
                [SanitizedAtUtc] datetimeoffset NULL,
                [RowVersion] rowversion NOT NULL,
                CONSTRAINT [CK_DatabaseSecurityState_Singleton] CHECK ([Id] = 1),
                CONSTRAINT [CK_DatabaseSecurityState_Generations]
                    CHECK ([RestoreSanitizedGeneration] <= [RestoreGeneration])
            );
            INSERT INTO [Security].[DatabaseSecurityState] ([Id]) VALUES (1);

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

            CREATE TABLE [Security].[WorkbenchRestorePending]
            (
                [Id] tinyint NOT NULL CONSTRAINT [PK_WorkbenchRestorePending] PRIMARY KEY,
                [IsPending] bit NOT NULL,
                CONSTRAINT [CK_WorkbenchRestorePending_Singleton] CHECK ([Id] = 1)
            );
            INSERT INTO [Security].[WorkbenchRestorePending] ([Id], [IsPending]) VALUES (1, 0);
            """);
    }

    private static void CreateTenantIsolation(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE FUNCTION [Security].[fn_tenant_access](@TenantId uniqueidentifier)
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
    }

    private static void CreateIdentityProcedures(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE PROCEDURE [Identity].[ResolveCredential]
                @NormalizedEmail nvarchar(256)
            WITH EXECUTE AS OWNER
            AS
            BEGIN
                SET NOCOUNT ON;
                SELECT TOP (1)
                    [user].[Id], [user].[TenantId], [user].[PasswordHash], [user].[SecurityStamp],
                    [user].[State], [user].[SecurityVersion], [user].[CreatedAtUtc]
                FROM [Identity].[LoginDirectory] AS [directory]
                INNER JOIN [Identity].[Users] AS [user]
                    ON [user].[Id] = [directory].[UserId]
                    AND [user].[TenantId] = [directory].[TenantId]
                WHERE [directory].[NormalizedEmail] = @NormalizedEmail;
            END;
            """);

        migrationBuilder.Sql("""
            CREATE PROCEDURE [Identity].[ResolveSession]
                @TokenHash binary(32)
            WITH EXECUTE AS OWNER
            AS
            BEGIN
                SET NOCOUNT ON;
                SELECT TOP (1)
                    [session].[Id], [session].[TenantId], [session].[UserId], [session].[SecurityVersion],
                    [session].[LastSeenAtUtc], [session].[IdleExpiresAtUtc], [session].[AbsoluteExpiresAtUtc],
                    [session].[RevokedAtUtc], [user].[SecurityVersion], [user].[State],
                    [tenant].[IsEnabled], [user].[Email]
                FROM [Identity].[Sessions] AS [session]
                INNER JOIN [Identity].[Users] AS [user]
                    ON [user].[Id] = [session].[UserId]
                    AND [user].[TenantId] = [session].[TenantId]
                INNER JOIN [Tenancy].[Tenants] AS [tenant]
                    ON [tenant].[Id] = [session].[TenantId]
                WHERE [session].[TokenHash] = @TokenHash;

                SELECT DISTINCT [permission].[ClaimValue]
                FROM
                (
                    SELECT [claim].[ClaimValue]
                    FROM [Identity].[UserClaims] AS [claim]
                    WHERE [claim].[TenantId] =
                        (SELECT TOP (1) [TenantId] FROM [Identity].[Sessions] WHERE [TokenHash] = @TokenHash)
                        AND [claim].[UserId] =
                        (SELECT TOP (1) [UserId] FROM [Identity].[Sessions] WHERE [TokenHash] = @TokenHash)
                        AND [claim].[ClaimType] = N'workbench/permission'
                    UNION
                    SELECT [claim].[ClaimValue]
                    FROM [Identity].[Sessions] AS [session]
                    INNER JOIN [Identity].[UserRoles] AS [membership]
                        ON [membership].[TenantId] = [session].[TenantId]
                        AND [membership].[UserId] = [session].[UserId]
                    INNER JOIN [Identity].[RoleClaims] AS [claim]
                        ON [claim].[TenantId] = [membership].[TenantId]
                        AND [claim].[RoleId] = [membership].[RoleId]
                    WHERE [session].[TokenHash] = @TokenHash
                        AND [claim].[ClaimType] = N'workbench/permission'
                ) AS [permission]
                WHERE [permission].[ClaimValue] IS NOT NULL;
            END;
            """);

        migrationBuilder.Sql("""
            CREATE PROCEDURE [Identity].[ResolveRecoveryTarget]
                @NormalizedEmail nvarchar(256)
            WITH EXECUTE AS OWNER
            AS
            BEGIN
                SET NOCOUNT ON;
                SELECT TOP (1) [user].[Id], [user].[TenantId], [user].[SecurityVersion], [user].[Email]
                FROM [Identity].[LoginDirectory] AS [directory]
                INNER JOIN [Identity].[Users] AS [user]
                    ON [user].[TenantId] = [directory].[TenantId] AND [user].[Id] = [directory].[UserId]
                INNER JOIN [Tenancy].[Tenants] AS [tenant] ON [tenant].[Id] = [user].[TenantId]
                WHERE [directory].[NormalizedEmail] = @NormalizedEmail
                    AND [user].[State] = 1 AND [tenant].[IsEnabled] = 1 AND [user].[Email] IS NOT NULL;
            END;
            """);

        migrationBuilder.Sql("""
            CREATE PROCEDURE [Identity].[ResolveOperationAuthority]
                @TokenHash binary(32)
            WITH EXECUTE AS OWNER
            AS
            BEGIN
                SET NOCOUNT ON;
                SELECT TOP (1) [TenantId], [UserId]
                FROM [Identity].[IdentityOperations]
                WHERE [TokenHash] = @TokenHash;
            END;
            """);

        migrationBuilder.Sql("""
            CREATE PROCEDURE [Identity].[CreateInvitation]
                @TenantId uniqueidentifier,
                @UserId uniqueidentifier,
                @OperationId uniqueidentifier,
                @Email nvarchar(256),
                @NormalizedEmail nvarchar(256),
                @TokenHash binary(32),
                @Now datetimeoffset,
                @Expires datetimeoffset
            WITH EXECUTE AS OWNER
            AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                IF TRY_CONVERT(uniqueidentifier, SESSION_CONTEXT(N'TenantId')) <> @TenantId
                    THROW 50001, 'Tenant context mismatch.', 1;

                BEGIN TRANSACTION;
                INSERT INTO [Identity].[Users]
                    ([Id], [TenantId], [SecurityVersion], [State], [CreatedAtUtc],
                     [UserName], [NormalizedUserName], [Email], [NormalizedEmail], [EmailConfirmed],
                     [PhoneNumberConfirmed], [TwoFactorEnabled], [LockoutEnabled], [AccessFailedCount])
                VALUES
                    (@UserId, @TenantId, 1, 3, @Now,
                     @Email, @NormalizedEmail, @Email, @NormalizedEmail, 0, 0, 0, 0, 0);
                INSERT INTO [Identity].[LoginDirectory] ([NormalizedEmail], [UserId], [TenantId])
                VALUES (@NormalizedEmail, @UserId, @TenantId);
                INSERT INTO [Identity].[IdentityOperations]
                    ([Id], [TenantId], [UserId], [Purpose], [TokenHash], [SecurityVersion],
                     [CreatedAtUtc], [ExpiresAtUtc])
                VALUES (@OperationId, @TenantId, @UserId, 2, @TokenHash, 1, @Now, @Expires);
                COMMIT TRANSACTION;
            END;
            """);
    }

    private static void CreateAdministrationProcedures(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE PROCEDURE [Administration].[ProvisionTenant]
                @TenantId uniqueidentifier,
                @UserId uniqueidentifier,
                @TenantName nvarchar(200),
                @NormalizedTenantName nvarchar(200),
                @Email nvarchar(256),
                @NormalizedEmail nvarchar(256),
                @PasswordHash nvarchar(max),
                @SecurityStamp nvarchar(max),
                @ConcurrencyStamp nvarchar(max),
                @AdministratorRoleId uniqueidentifier,
                @MemberRoleId uniqueidentifier,
                @Now datetimeoffset,
                @RequireEmptyDatabase bit
            WITH EXECUTE AS OWNER
            AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
                BEGIN TRANSACTION;
                IF @RequireEmptyDatabase = 1
                    AND EXISTS (SELECT 1 FROM [Tenancy].[Tenants] WITH (UPDLOCK, HOLDLOCK))
                BEGIN
                    ROLLBACK TRANSACTION;
                    THROW 50010, 'Initial bootstrap has already been completed.', 1;
                END;

                INSERT INTO [Tenancy].[Tenants]
                    ([Id], [Name], [NormalizedName], [IsEnabled], [CreatedAtUtc])
                VALUES (@TenantId, @TenantName, @NormalizedTenantName, 1, @Now);
                INSERT INTO [Identity].[Users]
                    ([Id], [TenantId], [SecurityVersion], [State], [CreatedAtUtc],
                     [UserName], [NormalizedUserName], [Email], [NormalizedEmail], [EmailConfirmed],
                     [PasswordHash], [SecurityStamp], [ConcurrencyStamp], [PhoneNumberConfirmed],
                     [TwoFactorEnabled], [LockoutEnabled], [AccessFailedCount])
                VALUES
                    (@UserId, @TenantId, 1, 1, @Now,
                     @Email, @NormalizedEmail, @Email, @NormalizedEmail, 1,
                     @PasswordHash, @SecurityStamp, @ConcurrencyStamp, 0, 0, 0, 0);
                INSERT INTO [Identity].[LoginDirectory] ([NormalizedEmail], [UserId], [TenantId])
                VALUES (@NormalizedEmail, @UserId, @TenantId);
                INSERT INTO [Identity].[Roles]
                    ([Id], [TenantId], [Name], [NormalizedName], [ConcurrencyStamp])
                VALUES
                    (@AdministratorRoleId, @TenantId, N'Tenant administrator', N'TENANT ADMINISTRATOR', NEWID()),
                    (@MemberRoleId, @TenantId, N'Tenant member', N'TENANT MEMBER', NEWID());
                INSERT INTO [Identity].[RoleClaims] ([TenantId], [RoleId], [ClaimType], [ClaimValue])
                VALUES
                    (@TenantId, @AdministratorRoleId, N'workbench/permission', N'TenantAccess'),
                    (@TenantId, @AdministratorRoleId, N'workbench/permission', N'TenantUsersManage'),
                    (@TenantId, @MemberRoleId, N'workbench/permission', N'TenantAccess');
                INSERT INTO [Identity].[UserRoles] ([TenantId], [UserId], [RoleId])
                VALUES (@TenantId, @UserId, @AdministratorRoleId);
                INSERT INTO [Security].[SystemSecurityAuditEvents]
                    ([Id], [Action], [Outcome], [CorrelationId], [MetadataJson], [OccurredAtUtc])
                VALUES
                    (NEWID(),
                     CASE WHEN @RequireEmptyDatabase = 1 THEN N'system.bootstrap.completed'
                          ELSE N'system.tenant.provisioned' END,
                     N'Succeeded', NULL, NULL, @Now);
                COMMIT TRANSACTION;
            END;
            """);

        migrationBuilder.Sql("""
            CREATE PROCEDURE [Administration].[SanitizeRestore]
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
            CREATE PROCEDURE [Administration].[CreateDevelopmentRecovery]
                @OperationId uniqueidentifier,
                @NormalizedEmail nvarchar(256),
                @TokenHash binary(32),
                @Now datetimeoffset,
                @ExpiresAtUtc datetimeoffset
            WITH EXECUTE AS OWNER
            AS
            BEGIN
                SET NOCOUNT ON;
                DECLARE @TenantId uniqueidentifier;
                DECLARE @UserId uniqueidentifier;
                DECLARE @SecurityVersion bigint;
                SELECT TOP (1)
                    @TenantId = [user].[TenantId], @UserId = [user].[Id],
                    @SecurityVersion = [user].[SecurityVersion]
                FROM [Identity].[LoginDirectory] AS [directory]
                INNER JOIN [Identity].[Users] AS [user]
                    ON [user].[TenantId] = [directory].[TenantId] AND [user].[Id] = [directory].[UserId]
                INNER JOIN [Tenancy].[Tenants] AS [tenant] ON [tenant].[Id] = [user].[TenantId]
                WHERE [directory].[NormalizedEmail] = @NormalizedEmail
                    AND [user].[State] = 1 AND [tenant].[IsEnabled] = 1;
                IF @UserId IS NULL
                BEGIN
                    SELECT CONVERT(bit, 0);
                    RETURN;
                END;
                INSERT INTO [Identity].[IdentityOperations]
                    ([Id], [TenantId], [UserId], [Purpose], [TokenHash], [SecurityVersion],
                     [CreatedAtUtc], [ExpiresAtUtc])
                VALUES
                    (@OperationId, @TenantId, @UserId, 1, @TokenHash, @SecurityVersion,
                     @Now, @ExpiresAtUtc);
                SELECT CONVERT(bit, 1);
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
    }

    private static void CreateSecurityProcedures(MigrationBuilder migrationBuilder)
    {
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
            CREATE PROCEDURE [Security].[ReadDatabaseReadiness]
            WITH EXECUTE AS OWNER
            AS
            BEGIN
                SET NOCOUNT ON;
                SELECT
                    CONVERT(bit, CASE WHEN
                        (SELECT MAX([MigrationId]) FROM [dbo].[__EFMigrationsHistory]) =
                            N'20260904061246_EstablishSecurityBoundaries'
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
    }

    private static void ConfigurePermissions(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            IF DATABASE_PRINCIPAL_ID(N'workbench_web') IS NULL
                EXEC(N'CREATE ROLE [workbench_web]');
            IF DATABASE_PRINCIPAL_ID(N'workbench_migrator') IS NULL
                EXEC(N'CREATE ROLE [workbench_migrator]');
            IF DATABASE_PRINCIPAL_ID(N'workbench_operator') IS NULL
                EXEC(N'CREATE ROLE [workbench_operator]');

            DECLARE @grantMigrator nvarchar(max) =
                N'GRANT CONTROL ON DATABASE::' + QUOTENAME(DB_NAME()) + N' TO [workbench_migrator]';
            EXEC sys.sp_executesql @grantMigrator;

            DENY ALTER ANY SECURITY POLICY TO [workbench_web];
            DENY ALTER ANY DATABASE DDL TRIGGER TO [workbench_web];
            DENY SELECT, INSERT, UPDATE, DELETE ON [dbo].[__EFMigrationsHistory] TO [workbench_web];
            GRANT SELECT ON [Tenancy].[Tenants] TO [workbench_web];
            GRANT SELECT, INSERT, UPDATE, DELETE ON [Identity].[Users] TO [workbench_web];
            GRANT SELECT, INSERT, UPDATE, DELETE ON [Identity].[Roles] TO [workbench_web];
            GRANT SELECT, INSERT, UPDATE, DELETE ON [Identity].[UserClaims] TO [workbench_web];
            GRANT SELECT, INSERT, UPDATE, DELETE ON [Identity].[UserLogins] TO [workbench_web];
            GRANT SELECT, INSERT, UPDATE, DELETE ON [Identity].[UserRoles] TO [workbench_web];
            GRANT SELECT, INSERT, UPDATE, DELETE ON [Identity].[RoleClaims] TO [workbench_web];
            GRANT SELECT, INSERT, UPDATE, DELETE ON [Identity].[UserTokens] TO [workbench_web];
            GRANT SELECT, INSERT, UPDATE, DELETE ON [Identity].[Sessions] TO [workbench_web];
            GRANT SELECT, INSERT, UPDATE, DELETE ON [Identity].[DataProtectionKeys] TO [workbench_web];
            GRANT SELECT, INSERT, UPDATE ON [Identity].[IdentityOperations] TO [workbench_web];
            GRANT SELECT, INSERT ON [Security].[TenantSecurityAuditEvents] TO [workbench_web];
            DENY UPDATE, DELETE ON [Security].[TenantSecurityAuditEvents] TO [workbench_web];
            DENY SELECT, INSERT, UPDATE, DELETE ON [Security].[SystemSecurityAuditEvents] TO [workbench_web];
            GRANT EXECUTE ON [Identity].[ResolveCredential] TO [workbench_web];
            GRANT EXECUTE ON [Identity].[ResolveSession] TO [workbench_web];
            GRANT EXECUTE ON [Identity].[ResolveRecoveryTarget] TO [workbench_web];
            GRANT EXECUTE ON [Identity].[ResolveOperationAuthority] TO [workbench_web];
            GRANT EXECUTE ON [Identity].[CreateInvitation] TO [workbench_web];
            GRANT EXECUTE ON [Security].[TryAcquireSensitiveRequest] TO [workbench_web];
            GRANT EXECUTE ON [Security].[ReadDatabaseReadiness] TO [workbench_web];
            GRANT SELECT ON [Security].[fn_tenant_access] TO [workbench_web];
            GRANT EXECUTE ON [Administration].[ProvisionTenant] TO [workbench_operator];
            GRANT EXECUTE ON [Administration].[SanitizeRestore] TO [workbench_operator];
            DENY EXECUTE ON [Administration].[CreateDevelopmentRecovery] TO [workbench_operator];
            DENY EXECUTE ON [Administration].[MarkRestorePending] TO [workbench_web];
            DENY EXECUTE ON [Administration].[MarkRestorePending] TO [workbench_operator];
            DENY SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[Identity] TO [workbench_operator];
            DENY SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[Tenancy] TO [workbench_operator];
            DENY SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[Security] TO [workbench_operator];
            DENY SELECT, INSERT, UPDATE, DELETE ON [Security].[DatabaseSecurityState] TO [workbench_web];
            DENY SELECT, INSERT, UPDATE, DELETE ON [Security].[DatabaseSecurityState] TO [workbench_operator];
            DENY SELECT, INSERT, UPDATE, DELETE ON [Security].[TenantContextKeys] TO [workbench_web];
            DENY SELECT, INSERT, UPDATE, DELETE ON [Security].[TenantContextKeys] TO [workbench_operator];
            DENY SELECT, INSERT, UPDATE, DELETE ON [Security].[SensitiveRequestLimits] TO [workbench_web];
            DENY SELECT, INSERT, UPDATE, DELETE ON [Security].[SensitiveRequestLimits] TO [workbench_operator];
            DENY SELECT, INSERT, UPDATE, DELETE ON [Security].[WorkbenchRestorePending] TO [workbench_web];
            DENY SELECT, INSERT, UPDATE, DELETE ON [Security].[WorkbenchRestorePending] TO [workbench_operator];
            """);
    }

    private static void RemovePermissions(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            REVOKE ALTER ANY SECURITY POLICY FROM [workbench_web];
            REVOKE ALTER ANY DATABASE DDL TRIGGER FROM [workbench_web];
            REVOKE SELECT, INSERT, UPDATE, DELETE ON [dbo].[__EFMigrationsHistory] FROM [workbench_web];
            REVOKE SELECT ON [Tenancy].[Tenants] FROM [workbench_web];
            REVOKE SELECT, INSERT, UPDATE, DELETE ON [Identity].[Users] FROM [workbench_web];
            REVOKE SELECT, INSERT, UPDATE, DELETE ON [Identity].[Roles] FROM [workbench_web];
            REVOKE SELECT, INSERT, UPDATE, DELETE ON [Identity].[UserClaims] FROM [workbench_web];
            REVOKE SELECT, INSERT, UPDATE, DELETE ON [Identity].[UserLogins] FROM [workbench_web];
            REVOKE SELECT, INSERT, UPDATE, DELETE ON [Identity].[UserRoles] FROM [workbench_web];
            REVOKE SELECT, INSERT, UPDATE, DELETE ON [Identity].[RoleClaims] FROM [workbench_web];
            REVOKE SELECT, INSERT, UPDATE, DELETE ON [Identity].[UserTokens] FROM [workbench_web];
            REVOKE SELECT, INSERT, UPDATE, DELETE ON [Identity].[Sessions] FROM [workbench_web];
            REVOKE SELECT, INSERT, UPDATE, DELETE ON [Identity].[DataProtectionKeys] FROM [workbench_web];
            REVOKE SELECT, INSERT, UPDATE ON [Identity].[IdentityOperations] FROM [workbench_web];
            REVOKE SELECT, INSERT, UPDATE, DELETE ON [Security].[TenantSecurityAuditEvents] FROM [workbench_web];
            REVOKE SELECT, INSERT, UPDATE, DELETE ON [Security].[SystemSecurityAuditEvents] FROM [workbench_web];
            REVOKE SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[Identity] FROM [workbench_operator];
            REVOKE SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[Tenancy] FROM [workbench_operator];
            REVOKE SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[Security] FROM [workbench_operator];

            DECLARE @revokeMigrator nvarchar(max) =
                N'REVOKE CONTROL ON DATABASE::' + QUOTENAME(DB_NAME()) + N' FROM [workbench_migrator]';
            EXEC sys.sp_executesql @revokeMigrator;
            """);
    }
}
