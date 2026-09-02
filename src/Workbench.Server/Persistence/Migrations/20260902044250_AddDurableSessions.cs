// Copyright (c) 2026 The White Stag Collection.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                schema: "Identity",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FriendlyName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Xml = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                schema: "Identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    SecurityVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IdleExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AbsoluteExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevocationReason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.UniqueConstraint("AK_Sessions_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Sessions_Users_TenantId_UserId",
                        columns: x => new { x.TenantId, x.UserId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TenantId_UserId",
                schema: "Identity",
                table: "Sessions",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TokenHash",
                schema: "Identity",
                table: "Sessions",
                column: "TokenHash",
                unique: true);

            migrationBuilder.Sql("""
                ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                    ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Sessions],
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Sessions] AFTER INSERT,
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[Sessions] AFTER UPDATE;
                """);

            migrationBuilder.Sql("""
                CREATE PROCEDURE [Identity].[ResolveSession]
                    @TokenHash binary(32)
                WITH EXECUTE AS OWNER
                AS
                BEGIN
                    SET NOCOUNT ON;

                    SELECT TOP (1)
                        [session].[Id],
                        [session].[TenantId],
                        [session].[UserId],
                        [session].[SecurityVersion],
                        [session].[LastSeenAtUtc],
                        [session].[IdleExpiresAtUtc],
                        [session].[AbsoluteExpiresAtUtc],
                        [session].[RevokedAtUtc],
                        [user].[SecurityVersion],
                        [user].[State],
                        [tenant].[IsEnabled],
                        [user].[Email]
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
                GRANT EXECUTE ON [Identity].[ResolveSession] TO [workbench_web];
                GRANT SELECT, INSERT, UPDATE, DELETE ON [Identity].[Sessions] TO [workbench_web];
                GRANT SELECT, INSERT, UPDATE, DELETE ON [Identity].[DataProtectionKeys] TO [workbench_web];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE [Identity].[ResolveSession];");

            migrationBuilder.Sql("""
                ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                    DROP FILTER PREDICATE ON [Identity].[Sessions],
                    DROP BLOCK PREDICATE ON [Identity].[Sessions] AFTER INSERT,
                    DROP BLOCK PREDICATE ON [Identity].[Sessions] AFTER UPDATE;
                """);

            migrationBuilder.DropTable(
                name: "DataProtectionKeys",
                schema: "Identity");

            migrationBuilder.DropTable(
                name: "Sessions",
                schema: "Identity");
        }
    }
}
