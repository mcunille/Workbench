// Copyright (c) 2026 The White Stag Collection.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantIsolation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "Tenancy");

            migrationBuilder.EnsureSchema(
                name: "Security");

            migrationBuilder.CreateTable(
                name: "Tenants",
                schema: "Tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantSecurityAuditEvents",
                schema: "Security",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantSecurityAuditEvents", x => x.Id);
                    table.UniqueConstraint("AK_TenantSecurityAuditEvents_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_TenantSecurityAuditEvents_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "Tenancy",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_NormalizedName",
                schema: "Tenancy",
                table: "Tenants",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.Sql("""
                CREATE FUNCTION [Security].[fn_tenant_access](@TenantId uniqueidentifier)
                RETURNS TABLE
                WITH SCHEMABINDING
                AS
                RETURN
                (
                    SELECT 1 AS [is_allowed]
                    WHERE
                        USER_NAME() = N'dbo'
                        OR @TenantId = TRY_CONVERT(uniqueidentifier, SESSION_CONTEXT(N'TenantId'))
                );
                """);

            migrationBuilder.Sql("""
                CREATE SECURITY POLICY [Security].[TenantIsolationPolicy]
                    ADD FILTER PREDICATE [Security].[fn_tenant_access]([Id])
                        ON [Tenancy].[Tenants],
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([Id])
                        ON [Tenancy].[Tenants] AFTER INSERT,
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([Id])
                        ON [Tenancy].[Tenants] AFTER UPDATE,
                    ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId])
                        ON [Security].[TenantSecurityAuditEvents],
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId])
                        ON [Security].[TenantSecurityAuditEvents] AFTER INSERT,
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId])
                        ON [Security].[TenantSecurityAuditEvents] AFTER UPDATE
                WITH (STATE = ON, SCHEMABINDING = ON);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP SECURITY POLICY [Security].[TenantIsolationPolicy];");
            migrationBuilder.Sql("DROP FUNCTION [Security].[fn_tenant_access];");

            migrationBuilder.DropTable(
                name: "TenantSecurityAuditEvents",
                schema: "Security");

            migrationBuilder.DropTable(
                name: "Tenants",
                schema: "Tenancy");
        }
    }
}
