// Copyright (c) 2026 The White Stag Collection.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableWork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "Operations");

            migrationBuilder.CreateTable(
                name: "WorkItems",
                schema: "Operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IdentityOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProtectedPayload = table.Column<byte[]>(type: "varbinary(8000)", maxLength: 8000, nullable: true),
                    State = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Attempts = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Generation = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    LeaseOwner = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AvailableAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItems", x => x.Id);
                    table.UniqueConstraint("AK_WorkItems_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_WorkItems_Kind", "([Kind] = 1 AND [AttachmentId] IS NOT NULL AND [IdentityOperationId] IS NULL AND [ProtectedPayload] IS NULL) OR ([Kind] = 2 AND [IdentityOperationId] IS NOT NULL AND [AttachmentId] IS NULL)");
                    table.CheckConstraint("CK_WorkItems_State", "[State] BETWEEN 0 AND 3 AND [Attempts] BETWEEN 0 AND 5 AND [Generation] >= 0");
                    table.ForeignKey(
                        name: "FK_WorkItems_Attachments_TenantId_AttachmentId",
                        columns: x => new { x.TenantId, x.AttachmentId },
                        principalSchema: "Storage",
                        principalTable: "Attachments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkItems_IdentityOperations_TenantId_IdentityOperationId",
                        columns: x => new { x.TenantId, x.IdentityOperationId },
                        principalSchema: "Identity",
                        principalTable: "IdentityOperations",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_State_AvailableAtUtc",
                schema: "Operations",
                table: "WorkItems",
                columns: new[] { "State", "AvailableAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_TenantId_AttachmentId",
                schema: "Operations",
                table: "WorkItems",
                columns: new[] { "TenantId", "AttachmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_TenantId_IdentityOperationId",
                schema: "Operations",
                table: "WorkItems",
                columns: new[] { "TenantId", "IdentityOperationId" });
            OperationalSchema.CreateQueueProcedures(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("THROW 50020, 'Durable work rollback requires an explicit offline recovery procedure.', 1;");
        }
    }
}
