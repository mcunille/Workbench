// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDatabasePrincipals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(name: "Administration");

            migrationBuilder.Sql("""
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
                """);

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
                GRANT EXECUTE ON [Administration].[ProvisionTenant] TO [workbench_operator];
                DENY SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[Identity] TO [workbench_operator];
                DENY SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[Tenancy] TO [workbench_operator];
                DENY SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[Security] TO [workbench_operator];
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE [Administration].[ProvisionTenant];");

        }
    }
}
