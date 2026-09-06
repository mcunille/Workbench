// Copyright (c) 2026 The White Stag Collection.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBlobMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "Storage");

            migrationBuilder.CreateTable(
                name: "Attachments",
                schema: "Storage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CurrentRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeleteAfterUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Held = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                    table.UniqueConstraint("AK_Attachments_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Attachments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "Tenancy",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                    ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Storage].[Attachments],
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Storage].[Attachments] AFTER INSERT,
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Storage].[Attachments] AFTER UPDATE;
                GRANT SELECT, INSERT, UPDATE ON [Storage].[Attachments] TO [workbench_web];
                DENY DELETE ON [Storage].[Attachments] TO [workbench_web];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("THROW 50020, 'Blob metadata rollback requires an explicit offline recovery procedure.', 1;");
        }
    }
}
