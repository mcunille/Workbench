// Copyright (c) 2026 The White Stag Collection.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentitySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "Identity");

            migrationBuilder.CreateTable(
                name: "Roles",
                schema: "Identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                    table.UniqueConstraint("AK_Roles_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Roles_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "Tenancy",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                schema: "Identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SecurityVersion = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    State = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SecurityStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.UniqueConstraint("AK_Users_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Users_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "Tenancy",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RoleClaims",
                schema: "Identity",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleClaims", x => x.Id);
                    table.UniqueConstraint("AK_RoleClaims_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_RoleClaims_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "Identity",
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RoleClaims_Roles_TenantId_RoleId",
                        columns: x => new { x.TenantId, x.RoleId },
                        principalSchema: "Identity",
                        principalTable: "Roles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LoginDirectory",
                schema: "Identity",
                columns: table => new
                {
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginDirectory", x => x.NormalizedEmail);
                    table.ForeignKey(
                        name: "FK_LoginDirectory_Users_TenantId_UserId",
                        columns: x => new { x.TenantId, x.UserId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserClaims",
                schema: "Identity",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserClaims", x => x.Id);
                    table.UniqueConstraint("AK_UserClaims_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_UserClaims_Users_TenantId_UserId",
                        columns: x => new { x.TenantId, x.UserId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserClaims_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserLogins",
                schema: "Identity",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserLogins", x => new { x.TenantId, x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_UserLogins_Users_TenantId_UserId",
                        columns: x => new { x.TenantId, x.UserId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserLogins_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                schema: "Identity",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => new { x.TenantId, x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "Identity",
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_TenantId_RoleId",
                        columns: x => new { x.TenantId, x.RoleId },
                        principalSchema: "Identity",
                        principalTable: "Roles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_TenantId_UserId",
                        columns: x => new { x.TenantId, x.UserId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserTokens",
                schema: "Identity",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTokens", x => new { x.TenantId, x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_UserTokens_Users_TenantId_UserId",
                        columns: x => new { x.TenantId, x.UserId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserTokens_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoginDirectory_TenantId_UserId",
                schema: "Identity",
                table: "LoginDirectory",
                columns: new[] { "TenantId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleClaims_RoleId",
                schema: "Identity",
                table: "RoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleClaims_TenantId_RoleId",
                schema: "Identity",
                table: "RoleClaims",
                columns: new[] { "TenantId", "RoleId" });

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                schema: "Identity",
                table: "Roles",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "RoleNamePerTenantIndex",
                schema: "Identity",
                table: "Roles",
                columns: new[] { "TenantId", "NormalizedName" },
                unique: true,
                filter: "[NormalizedName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserClaims_TenantId_UserId",
                schema: "Identity",
                table: "UserClaims",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserClaims_UserId",
                schema: "Identity",
                table: "UserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserLogins_TenantId_UserId",
                schema: "Identity",
                table: "UserLogins",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserLogins_UserId",
                schema: "Identity",
                table: "UserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                schema: "Identity",
                table: "UserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_TenantId_RoleId",
                schema: "Identity",
                table: "UserRoles",
                columns: new[] { "TenantId", "RoleId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_UserId",
                schema: "Identity",
                table: "UserRoles",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                schema: "Identity",
                table: "Users",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                schema: "Identity",
                table: "Users",
                column: "NormalizedUserName",
                unique: true,
                filter: "[NormalizedUserName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserTokens_UserId",
                schema: "Identity",
                table: "UserTokens",
                column: "UserId");

            migrationBuilder.Sql("""
                ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
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
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[UserTokens] AFTER UPDATE;
                """);

            migrationBuilder.Sql("""
                CREATE PROCEDURE [Identity].[ResolveCredential]
                    @NormalizedEmail nvarchar(256)
                WITH EXECUTE AS OWNER
                AS
                BEGIN
                    SET NOCOUNT ON;

                    SELECT TOP (1)
                        [user].[Id],
                        [user].[TenantId],
                        [user].[PasswordHash],
                        [user].[SecurityStamp],
                        [user].[State],
                        [user].[SecurityVersion],
                        [user].[CreatedAtUtc]
                    FROM [Identity].[LoginDirectory] AS [directory]
                    INNER JOIN [Identity].[Users] AS [user]
                        ON [user].[Id] = [directory].[UserId]
                        AND [user].[TenantId] = [directory].[TenantId]
                    WHERE [directory].[NormalizedEmail] = @NormalizedEmail;
                END;
                """);

            migrationBuilder.Sql("""
                IF DATABASE_PRINCIPAL_ID(N'workbench_web') IS NULL
                    EXEC(N'CREATE ROLE [workbench_web]');

                GRANT EXECUTE ON [Identity].[ResolveCredential] TO [workbench_web];
                GRANT SELECT, INSERT, UPDATE, DELETE ON [Identity].[Users] TO [workbench_web];
                GRANT SELECT, INSERT, UPDATE, DELETE ON [Identity].[Roles] TO [workbench_web];
                GRANT SELECT, INSERT, UPDATE, DELETE ON [Identity].[UserClaims] TO [workbench_web];
                GRANT SELECT, INSERT, UPDATE, DELETE ON [Identity].[UserLogins] TO [workbench_web];
                GRANT SELECT, INSERT, UPDATE, DELETE ON [Identity].[UserRoles] TO [workbench_web];
                GRANT SELECT, INSERT, UPDATE, DELETE ON [Identity].[RoleClaims] TO [workbench_web];
                GRANT SELECT, INSERT, UPDATE, DELETE ON [Identity].[UserTokens] TO [workbench_web];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE [Identity].[ResolveCredential];");

            migrationBuilder.Sql("""
                ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                    DROP FILTER PREDICATE ON [Identity].[Users],
                    DROP BLOCK PREDICATE ON [Identity].[Users] AFTER INSERT,
                    DROP BLOCK PREDICATE ON [Identity].[Users] AFTER UPDATE,
                    DROP FILTER PREDICATE ON [Identity].[Roles],
                    DROP BLOCK PREDICATE ON [Identity].[Roles] AFTER INSERT,
                    DROP BLOCK PREDICATE ON [Identity].[Roles] AFTER UPDATE,
                    DROP FILTER PREDICATE ON [Identity].[UserClaims],
                    DROP BLOCK PREDICATE ON [Identity].[UserClaims] AFTER INSERT,
                    DROP BLOCK PREDICATE ON [Identity].[UserClaims] AFTER UPDATE,
                    DROP FILTER PREDICATE ON [Identity].[UserLogins],
                    DROP BLOCK PREDICATE ON [Identity].[UserLogins] AFTER INSERT,
                    DROP BLOCK PREDICATE ON [Identity].[UserLogins] AFTER UPDATE,
                    DROP FILTER PREDICATE ON [Identity].[UserRoles],
                    DROP BLOCK PREDICATE ON [Identity].[UserRoles] AFTER INSERT,
                    DROP BLOCK PREDICATE ON [Identity].[UserRoles] AFTER UPDATE,
                    DROP FILTER PREDICATE ON [Identity].[RoleClaims],
                    DROP BLOCK PREDICATE ON [Identity].[RoleClaims] AFTER INSERT,
                    DROP BLOCK PREDICATE ON [Identity].[RoleClaims] AFTER UPDATE,
                    DROP FILTER PREDICATE ON [Identity].[UserTokens],
                    DROP BLOCK PREDICATE ON [Identity].[UserTokens] AFTER INSERT,
                    DROP BLOCK PREDICATE ON [Identity].[UserTokens] AFTER UPDATE;
                """);

            migrationBuilder.DropTable(
                name: "LoginDirectory",
                schema: "Identity");

            migrationBuilder.DropTable(
                name: "RoleClaims",
                schema: "Identity");

            migrationBuilder.DropTable(
                name: "UserClaims",
                schema: "Identity");

            migrationBuilder.DropTable(
                name: "UserLogins",
                schema: "Identity");

            migrationBuilder.DropTable(
                name: "UserRoles",
                schema: "Identity");

            migrationBuilder.DropTable(
                name: "UserTokens",
                schema: "Identity");

            migrationBuilder.DropTable(
                name: "Roles",
                schema: "Identity");

            migrationBuilder.DropTable(
                name: "Users",
                schema: "Identity");

            migrationBuilder.Sql("""
                IF DATABASE_PRINCIPAL_ID(N'workbench_web') IS NOT NULL
                    AND NOT EXISTS
                    (
                        SELECT 1
                        FROM sys.database_role_members
                        WHERE role_principal_id = DATABASE_PRINCIPAL_ID(N'workbench_web')
                    )
                    DROP ROLE [workbench_web];
                """);
        }
    }
}
