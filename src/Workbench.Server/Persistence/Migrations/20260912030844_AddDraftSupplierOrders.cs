// Copyright (c) 2026 The White Stag Collection.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDraftSupplierOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "Purchasing");

            migrationBuilder.CreateTable(
                name: "DraftOrders",
                schema: "Purchasing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SupplierName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ContentSchemaVersion = table.Column<short>(type: "smallint", nullable: false),
                    ContentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DraftOrders", x => x.Id);
                    table.UniqueConstraint("AK_DraftOrders_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_DraftOrders_Content", "[ContentSchemaVersion]=1 AND ISJSON([ContentJson],OBJECT)=1 AND DATALENGTH([ContentJson])<=1048576");
                    table.CheckConstraint("CK_DraftOrders_Currency", "[Currency] IS NULL OR (DATALENGTH([Currency])=3 AND [Currency] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^A-Z]%')");
                    table.CheckConstraint("CK_DraftOrders_Id", "[Id]<>'00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_DraftOrders_Notes", "[Notes] IS NULL OR DATALENGTH([Notes])<=20000");
                    table.CheckConstraint("CK_DraftOrders_Timestamps", "[UpdatedAtUtc]>=[CreatedAtUtc] AND DATEPART(TZOFFSET,[CreatedAtUtc])=0 AND DATEPART(TZOFFSET,[UpdatedAtUtc])=0");
                    table.ForeignKey(
                        name: "FK_DraftOrders_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "Tenancy",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DraftOrders_Users_TenantId_CreatedByUserId",
                        columns: x => new { x.TenantId, x.CreatedByUserId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DraftOrders_Users_TenantId_UpdatedByUserId",
                        columns: x => new { x.TenantId, x.UpdatedByUserId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DraftOrderRequestReceipts",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DraftOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "varchar(6)", unicode: false, maxLength: 6, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpectedRowVersion = table.Column<byte[]>(type: "binary(8)", nullable: true),
                    FingerprintVersion = table.Column<short>(type: "smallint", nullable: false),
                    InputFingerprint = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    ResultRowVersion = table.Column<byte[]>(type: "binary(8)", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DraftOrderRequestReceipts", x => new { x.TenantId, x.RequestId });
                    table.CheckConstraint("CK_DraftOrderRequestReceipts_Completed", "DATEPART(TZOFFSET,[CompletedAtUtc])=0");
                    table.CheckConstraint("CK_DraftOrderRequestReceipts_Fingerprint", "[FingerprintVersion]=1");
                    table.CheckConstraint("CK_DraftOrderRequestReceipts_Operation", "([Operation] COLLATE Latin1_General_100_BIN2='Create' AND [ExpectedRowVersion] IS NULL) OR ([Operation] COLLATE Latin1_General_100_BIN2='Update' AND [ExpectedRowVersion] IS NOT NULL)");
                    table.CheckConstraint("CK_DraftOrderRequestReceipts_RequestId", "[RequestId]<>'00000000-0000-0000-0000-000000000000'");
                    table.ForeignKey(
                        name: "FK_DraftOrderRequestReceipts_DraftOrders_TenantId_DraftOrderId",
                        columns: x => new { x.TenantId, x.DraftOrderId },
                        principalSchema: "Purchasing",
                        principalTable: "DraftOrders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DraftOrderRequestReceipts_Users_TenantId_ActorUserId",
                        columns: x => new { x.TenantId, x.ActorUserId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DraftOrderRequestReceipts_TenantId_ActorUserId",
                schema: "Purchasing",
                table: "DraftOrderRequestReceipts",
                columns: new[] { "TenantId", "ActorUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_DraftOrderRequestReceipts_TenantId_DraftOrderId",
                schema: "Purchasing",
                table: "DraftOrderRequestReceipts",
                columns: new[] { "TenantId", "DraftOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_DraftOrders_TenantId_CreatedByUserId",
                schema: "Purchasing",
                table: "DraftOrders",
                columns: new[] { "TenantId", "CreatedByUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_DraftOrders_TenantId_UpdatedAtUtc_Id",
                schema: "Purchasing",
                table: "DraftOrders",
                columns: new[] { "TenantId", "UpdatedAtUtc", "Id" },
                descending: new[] { false, true, true })
                .Annotation("SqlServer:Include", new[] { "Title", "SupplierName" });

            migrationBuilder.CreateIndex(
                name: "IX_DraftOrders_TenantId_UpdatedByUserId",
                schema: "Purchasing",
                table: "DraftOrders",
                columns: new[] { "TenantId", "UpdatedByUserId" });
            DraftOrderSchema.Protect(migrationBuilder, "20260912030844_AddDraftSupplierOrders");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("THROW 50020, 'Draft orders and request receipts require forward correction or guarded recovery.', 1;");
    }
}
