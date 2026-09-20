// Copyright (c) 2026 The White Stag Collection.
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseOrderDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PurchaseOrderDocumentOperations",
                schema: "Purchasing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MediaType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Extension = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    Length = table.Column<long>(type: "bigint", nullable: true),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ExpectedOrderVersion = table.Column<byte[]>(type: "varbinary(8)", maxLength: 8, nullable: false),
                    ExpectedDocumentVersion = table.Column<byte[]>(type: "varbinary(8)", maxLength: 8, nullable: true),
                    ResultOrderVersion = table.Column<byte[]>(type: "varbinary(8)", maxLength: 8, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderDocumentOperations", x => x.Id);
                    table.CheckConstraint("CK_PurchaseOrderDocumentOperations_State", "[Kind] BETWEEN 0 AND 2 AND [State] BETWEEN 0 AND 2");
                    table.ForeignKey(
                        name: "FK_PurchaseOrderDocumentOperations_DraftOrders_TenantId_OrderId",
                        columns: x => new { x.TenantId, x.OrderId },
                        principalSchema: "Purchasing",
                        principalTable: "DraftOrders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderDocumentOperations_Revisions_TenantId_AttachmentId_RevisionId",
                        columns: x => new { x.TenantId, x.AttachmentId, x.RevisionId },
                        principalSchema: "Storage",
                        principalTable: "Revisions",
                        principalColumns: new[] { "TenantId", "AttachmentId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderDocuments",
                schema: "Purchasing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MediaType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Extension = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RemovedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderDocuments", x => x.Id);
                    table.UniqueConstraint("AK_PurchaseOrderDocuments_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_PurchaseOrderDocuments_Content", "[Length] BETWEEN 1 AND 10485760 AND LEN([Sha256])=64 AND [Sha256] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9A-F]%'");
                    table.CheckConstraint("CK_PurchaseOrderDocuments_Label", "DATALENGTH([Label]) BETWEEN 2 AND 400");
                    table.ForeignKey(
                        name: "FK_PurchaseOrderDocuments_DraftOrders_TenantId_OrderId",
                        columns: x => new { x.TenantId, x.OrderId },
                        principalSchema: "Purchasing",
                        principalTable: "DraftOrders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderDocuments_Revisions_TenantId_AttachmentId_RevisionId",
                        columns: x => new { x.TenantId, x.AttachmentId, x.RevisionId },
                        principalSchema: "Storage",
                        principalTable: "Revisions",
                        principalColumns: new[] { "TenantId", "AttachmentId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderDocumentOperations_TenantId_AttachmentId_RevisionId",
                schema: "Purchasing",
                table: "PurchaseOrderDocumentOperations",
                columns: new[] { "TenantId", "AttachmentId", "RevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderDocumentOperations_TenantId_OrderId_Kind_State",
                schema: "Purchasing",
                table: "PurchaseOrderDocumentOperations",
                columns: new[] { "TenantId", "OrderId", "Kind", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderDocumentOperations_TenantId_RequestId",
                schema: "Purchasing",
                table: "PurchaseOrderDocumentOperations",
                columns: new[] { "TenantId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderDocuments_TenantId_AttachmentId_RevisionId",
                schema: "Purchasing",
                table: "PurchaseOrderDocuments",
                columns: new[] { "TenantId", "AttachmentId", "RevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderDocuments_TenantId_OrderId_RemovedAtUtc",
                schema: "Purchasing",
                table: "PurchaseOrderDocuments",
                columns: new[] { "TenantId", "OrderId", "RemovedAtUtc" });
            PurchaseOrderDocumentSchema.Protect(migrationBuilder, "20260918061646_AddPurchaseOrderDocuments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("THROW 50020, 'Purchase order documents and command evidence require forward correction or paired recovery.', 1;");
    }
}
