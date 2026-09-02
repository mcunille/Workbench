// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDatabaseSecurityState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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
                                N'20260902060523_AddDatabaseSecurityState'
                            THEN 1 ELSE 0 END) AS [CompatibleMigration],
                        CONVERT(bit, CASE WHEN EXISTS
                        (
                            SELECT 1 FROM sys.security_policies
                            WHERE [name] = N'TenantIsolationPolicy'
                                AND SCHEMA_NAME([schema_id]) = N'Security'
                                AND [is_enabled] = 1
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
                        [RestoreGeneration],
                        [RestoreSanitizedGeneration]
                    FROM [Security].[DatabaseSecurityState]
                    WHERE [Id] = 1;
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
                    UPDATE [Identity].[Users]
                    SET [SecurityVersion] = [SecurityVersion] + 1,
                        [SecurityStamp] = CONVERT(nvarchar(36), NEWID()),
                        [ConcurrencyStamp] = CONVERT(nvarchar(36), NEWID());
                    UPDATE [Security].[DatabaseSecurityState]
                    SET [RestoreSanitizedGeneration] = [RestoreGeneration], [SanitizedAtUtc] = @Now
                    WHERE [Id] = 1;
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
                GRANT SELECT ON [Tenancy].[Tenants] TO [workbench_web];
                GRANT EXECUTE ON [Security].[ReadDatabaseReadiness] TO [workbench_web];
                GRANT EXECUTE ON [Administration].[SanitizeRestore] TO [workbench_operator];
                GRANT EXECUTE ON [Administration].[CreateDevelopmentRecovery] TO [workbench_operator];
                DENY SELECT, INSERT, UPDATE, DELETE ON [Security].[DatabaseSecurityState] TO [workbench_web];
                DENY SELECT, INSERT, UPDATE, DELETE ON [Security].[DatabaseSecurityState] TO [workbench_operator];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE [Administration].[CreateDevelopmentRecovery];");
            migrationBuilder.Sql("DROP PROCEDURE [Administration].[SanitizeRestore];");
            migrationBuilder.Sql("DROP PROCEDURE [Security].[ReadDatabaseReadiness];");
            migrationBuilder.Sql("DROP TABLE [Security].[DatabaseSecurityState];");
        }
    }
}
