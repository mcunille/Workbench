// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence.Migrations;

public partial class DeferInvitationIdentityClaim : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Pending and cancelled credentialless accounts never proved mailbox ownership.
        // Operations may already be absent after restore sanitation.
        migrationBuilder.Sql("""
            DELETE d FROM [Identity].[LoginDirectory] d
            JOIN [Identity].[Users] u ON u.[Id] = d.[UserId] AND u.[TenantId] = d.[TenantId]
            WHERE u.[State] IN (2, 3) AND u.[PasswordHash] IS NULL;
            UPDATE [Identity].[Users] SET [UserName] = NULL, [NormalizedUserName] = NULL
            WHERE [State] IN (2, 3) AND [PasswordHash] IS NULL;
            """);
        migrationBuilder.Sql("""
            ALTER PROCEDURE [Identity].[CreateInvitation]
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
                DECLARE @ExpectedProof binary(32) =
                (
                    SELECT HASHBYTES(
                        'SHA2_256',
                        CONVERT(varbinary(max), [key].[ProofKey]) +
                        CONVERT(varbinary(max), CONVERT(nvarchar(36), @TenantId)) +
                        CONVERT(varbinary(max), TRY_CONVERT(varbinary(32), SESSION_CONTEXT(N'TenantNonce'))))
                    FROM [Security].[TenantContextKeys] AS [key]
                    WHERE [key].[Id] = 1
                );
                IF TRY_CONVERT(uniqueidentifier, SESSION_CONTEXT(N'TenantId')) <> @TenantId
                    OR TRY_CONVERT(varbinary(32), SESSION_CONTEXT(N'TenantNonce')) IS NULL
                    OR TRY_CONVERT(varbinary(32), SESSION_CONTEXT(N'TenantProof')) IS NULL
                    OR @ExpectedProof IS NULL
                    OR TRY_CONVERT(varbinary(32), SESSION_CONTEXT(N'TenantProof')) <> @ExpectedProof
                    THROW 50001, 'Tenant context mismatch.', 1;

                DECLARE @MemberRoleId uniqueidentifier =
                (
                    SELECT TOP (1) [Id]
                    FROM [Identity].[Roles]
                    WHERE [TenantId] = @TenantId AND [NormalizedName] = N'TENANT MEMBER'
                );
                IF @MemberRoleId IS NULL
                    THROW 50002, 'Tenant member role is missing.', 1;

                BEGIN TRANSACTION;
                INSERT INTO [Identity].[Users]
                    ([Id], [TenantId], [SecurityVersion], [State], [CreatedAtUtc],
                     [UserName], [NormalizedUserName], [Email], [NormalizedEmail], [EmailConfirmed],
                     [PhoneNumberConfirmed], [TwoFactorEnabled], [LockoutEnabled], [AccessFailedCount])
                VALUES
                    (@UserId, @TenantId, 1, 3, @Now,
                     NULL, NULL, @Email, @NormalizedEmail, 0, 0, 0, 0, 0);
                INSERT INTO [Identity].[UserRoles] ([UserId], [RoleId], [TenantId])
                VALUES (@UserId, @MemberRoleId, @TenantId);
                INSERT INTO [Identity].[IdentityOperations]
                    ([Id], [TenantId], [UserId], [Purpose], [TokenHash], [SecurityVersion],
                     [CreatedAtUtc], [ExpiresAtUtc])
                VALUES (@OperationId, @TenantId, @UserId, 2, @TokenHash, 1, @Now, @Expires);
                COMMIT TRANSACTION;
            END;
            """);
        migrationBuilder.Sql("""
            CREATE PROCEDURE [Identity].[ClaimInvitationIdentity]
                @TenantId uniqueidentifier,
                @UserId uniqueidentifier,
                @TokenHash binary(32),
                @Now datetimeoffset
            WITH EXECUTE AS OWNER
            AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                IF @@TRANCOUNT = 0
                    THROW 50003, 'Invitation claim requires the consumption transaction.', 1;
                DECLARE @ExpectedProof binary(32) =
                (
                    SELECT HASHBYTES(
                        'SHA2_256',
                        CONVERT(varbinary(max), [key].[ProofKey]) +
                        CONVERT(varbinary(max), CONVERT(nvarchar(36), @TenantId)) +
                        CONVERT(varbinary(max), TRY_CONVERT(varbinary(32), SESSION_CONTEXT(N'TenantNonce'))))
                    FROM [Security].[TenantContextKeys] AS [key]
                    WHERE [key].[Id] = 1
                );
                IF TRY_CONVERT(uniqueidentifier, SESSION_CONTEXT(N'TenantId')) <> @TenantId
                    OR TRY_CONVERT(varbinary(32), SESSION_CONTEXT(N'TenantNonce')) IS NULL
                    OR TRY_CONVERT(varbinary(32), SESSION_CONTEXT(N'TenantProof')) IS NULL
                    OR @ExpectedProof IS NULL
                    OR TRY_CONVERT(varbinary(32), SESSION_CONTEXT(N'TenantProof')) <> @ExpectedProof
                    THROW 50001, 'Tenant context mismatch.', 1;

                DECLARE @Email nvarchar(256), @NormalizedEmail nvarchar(256);
                SELECT @Email = u.[Email], @NormalizedEmail = u.[NormalizedEmail]
                FROM [Identity].[Users] u WITH (UPDLOCK, HOLDLOCK)
                JOIN [Identity].[IdentityOperations] o WITH (UPDLOCK, HOLDLOCK)
                    ON o.[UserId] = u.[Id] AND o.[TenantId] = u.[TenantId]
                WHERE u.[TenantId] = @TenantId AND u.[Id] = @UserId
                    AND u.[State] = 3 AND u.[PasswordHash] IS NULL
                    AND o.[TokenHash] = @TokenHash AND o.[Purpose] = 2
                    AND o.[ConsumedAtUtc] IS NULL AND o.[ExpiresAtUtc] > @Now
                    AND o.[SecurityVersion] = u.[SecurityVersion];
                IF @Email IS NULL OR @NormalizedEmail IS NULL
                    THROW 50004, 'Invitation is invalid or expired.', 1;
                INSERT INTO [Identity].[LoginDirectory] ([NormalizedEmail], [UserId], [TenantId])
                VALUES (@NormalizedEmail, @UserId, @TenantId);
                UPDATE [Identity].[Users]
                SET [UserName] = @Email, [NormalizedUserName] = @NormalizedEmail
                WHERE [TenantId] = @TenantId AND [Id] = @UserId;
            END;
            """);
        migrationBuilder.Sql("GRANT EXECUTE ON [Identity].[ClaimInvitationIdentity] TO [workbench_web];");
        migrationBuilder.Sql("""
            DECLARE @Readiness nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness = REPLACE(@Readiness, N'CREATE PROCEDURE', N'ALTER PROCEDURE');
            SET @Readiness = REPLACE(@Readiness, N'20260906031109_AddDeploymentQueueTelemetry', N'20260906092000_DeferInvitationIdentityClaim');
            EXEC sys.sp_executesql @Readiness;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("THROW 50020, 'Invitation identity claims cannot be restored safely; use a reviewed forward migration.', 1;");
}