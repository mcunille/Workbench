// Copyright (c) 2026 The White Stag Collection.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantGemReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantEntries",
                schema: "Gemology",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantEntries", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_GemTenantEntries_Content", "ISJSON([ContentJson],OBJECT)=1 AND DATALENGTH([ContentJson])<=2097152");
                    table.CheckConstraint("CK_GemTenantEntries_Id", "[Id]<>'00000000-0000-0000-0000-000000000000'");
                    table.ForeignKey(
                        name: "FK_TenantEntries_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "Tenancy",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantEntries_Users_TenantId_CreatedBy",
                        columns: x => new { x.TenantId, x.CreatedBy },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantEntries_Users_TenantId_UpdatedBy",
                        columns: x => new { x.TenantId, x.UpdatedBy },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantOverrides",
                schema: "Gemology",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OverridesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantOverrides", x => new { x.TenantId, x.EntryId });
                    table.CheckConstraint("CK_GemTenantOverrides_Content", "ISJSON([OverridesJson],OBJECT)=1 AND DATALENGTH([OverridesJson])<=2097152");
                    table.ForeignKey(
                        name: "FK_TenantOverrides_Entries_EntryId",
                        column: x => x.EntryId,
                        principalSchema: "Gemology",
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantOverrides_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "Tenancy",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantOverrides_Users_TenantId_CreatedBy",
                        columns: x => new { x.TenantId, x.CreatedBy },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantOverrides_Users_TenantId_UpdatedBy",
                        columns: x => new { x.TenantId, x.UpdatedBy },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantEntries_TenantId_CreatedBy",
                schema: "Gemology",
                table: "TenantEntries",
                columns: new[] { "TenantId", "CreatedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantEntries_TenantId_UpdatedBy",
                schema: "Gemology",
                table: "TenantEntries",
                columns: new[] { "TenantId", "UpdatedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantOverrides_EntryId",
                schema: "Gemology",
                table: "TenantOverrides",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantOverrides_TenantId_CreatedBy",
                schema: "Gemology",
                table: "TenantOverrides",
                columns: new[] { "TenantId", "CreatedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantOverrides_TenantId_UpdatedBy",
                schema: "Gemology",
                table: "TenantOverrides",
                columns: new[] { "TenantId", "UpdatedBy" });
            GemReferenceTenantSchema.Up(migrationBuilder, "20261003214043_AddTenantGemReference");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("THROW 50020,'Tenant gem reference rollback is blocked to preserve tenant additions and override evidence.',1;");
        }
    }
}
