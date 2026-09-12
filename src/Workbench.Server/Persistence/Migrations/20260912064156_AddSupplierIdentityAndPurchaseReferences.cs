// Copyright (c) 2026 The White Stag Collection.
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierIdentityAndPurchaseReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DraftOrders_DeletedContent",
                schema: "Purchasing",
                table: "DraftOrders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DraftOrderRequestReceipts_Fingerprint",
                schema: "Purchasing",
                table: "DraftOrderRequestReceipts");

            migrationBuilder.AddColumn<string>(
                name: "Platform",
                schema: "Purchasing",
                table: "DraftOrders",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PoNumber",
                schema: "Purchasing",
                table: "DraftOrders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupplierContactName",
                schema: "Purchasing",
                table: "DraftOrders",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupplierEmail",
                schema: "Purchasing",
                table: "DraftOrders",
                type: "nvarchar(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupplierId",
                schema: "Purchasing",
                table: "DraftOrders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupplierOrderReference",
                schema: "Purchasing",
                table: "DraftOrders",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupplierPhone",
                schema: "Purchasing",
                table: "DraftOrders",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupplierPostalAddress",
                schema: "Purchasing",
                table: "DraftOrders",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupplierWebsite",
                schema: "Purchasing",
                table: "DraftOrders",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PurchaseOrderCounters",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LastNumber = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderCounters", x => x.TenantId);
                    table.CheckConstraint("CK_PurchaseOrderCounters_Number", "[LastNumber]>=1");
                    table.ForeignKey(
                        name: "FK_PurchaseOrderCounters_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "Tenancy",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Suppliers",
                schema: "Purchasing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContactName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Website = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    PostalAddress = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => x.Id);
                    table.UniqueConstraint("AK_Suppliers_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_Suppliers_Id", "[Id]<>'00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_Suppliers_Timestamps", "[UpdatedAtUtc]>=[CreatedAtUtc] AND DATEPART(TZOFFSET,[CreatedAtUtc])=0 AND DATEPART(TZOFFSET,[UpdatedAtUtc])=0");
                    table.ForeignKey(
                        name: "FK_Suppliers_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "Tenancy",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Suppliers_Users_TenantId_CreatedByUserId",
                        columns: x => new { x.TenantId, x.CreatedByUserId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Suppliers_Users_TenantId_UpdatedByUserId",
                        columns: x => new { x.TenantId, x.UpdatedByUserId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierRequestReceipts",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "varchar(7)", unicode: false, maxLength: 7, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpectedRowVersion = table.Column<byte[]>(type: "binary(8)", nullable: true),
                    InputFingerprint = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    ResultRowVersion = table.Column<byte[]>(type: "binary(8)", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierRequestReceipts", x => new { x.TenantId, x.RequestId });
                    table.CheckConstraint("CK_SupplierRequestReceipts_Completed", "DATEPART(TZOFFSET,[CompletedAtUtc])=0");
                    table.CheckConstraint("CK_SupplierRequestReceipts_Operation", "([Operation] COLLATE Latin1_General_100_BIN2='Create' AND [ExpectedRowVersion] IS NULL) OR ([Operation] COLLATE Latin1_General_100_BIN2 IN ('Update','Archive') AND [ExpectedRowVersion] IS NOT NULL)");
                    table.CheckConstraint("CK_SupplierRequestReceipts_RequestId", "[RequestId]<>'00000000-0000-0000-0000-000000000000'");
                    table.ForeignKey(
                        name: "FK_SupplierRequestReceipts_Suppliers_TenantId_SupplierId",
                        columns: x => new { x.TenantId, x.SupplierId },
                        principalSchema: "Purchasing",
                        principalTable: "Suppliers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierRequestReceipts_Users_TenantId_ActorUserId",
                        columns: x => new { x.TenantId, x.ActorUserId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DraftOrders_TenantId_PoNumber",
                schema: "Purchasing",
                table: "DraftOrders",
                columns: new[] { "TenantId", "PoNumber" },
                unique: true,
                filter: "[PoNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DraftOrders_TenantId_SupplierId",
                schema: "Purchasing",
                table: "DraftOrders",
                columns: new[] { "TenantId", "SupplierId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_DraftOrders_DeletedContent",
                schema: "Purchasing",
                table: "DraftOrders",
                sql: "[IsDeleted]=0 OR ([SupplierId] IS NULL AND [SupplierContactName] IS NULL AND [SupplierEmail] IS NULL AND [SupplierPhone] IS NULL AND [SupplierWebsite] IS NULL AND [SupplierPostalAddress] IS NULL AND [SupplierOrderReference] IS NULL AND [Platform] IS NULL AND [Title] IS NULL AND [SupplierName] IS NULL AND [Currency] IS NULL AND [Notes] IS NULL AND CONVERT(varbinary(max),[ContentJson])=CONVERT(varbinary(max),N'{\"sourceLinks\":[],\"entries\":[]}'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DraftOrderRequestReceipts_Fingerprint",
                schema: "Purchasing",
                table: "DraftOrderRequestReceipts",
                sql: "[FingerprintVersion] IN (1,2)");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierRequestReceipts_TenantId_ActorUserId",
                schema: "Purchasing",
                table: "SupplierRequestReceipts",
                columns: new[] { "TenantId", "ActorUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierRequestReceipts_TenantId_SupplierId",
                schema: "Purchasing",
                table: "SupplierRequestReceipts",
                columns: new[] { "TenantId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_TenantId_CreatedByUserId",
                schema: "Purchasing",
                table: "Suppliers",
                columns: new[] { "TenantId", "CreatedByUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_TenantId_UpdatedAtUtc_Id",
                schema: "Purchasing",
                table: "Suppliers",
                columns: new[] { "TenantId", "UpdatedAtUtc", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_TenantId_UpdatedByUserId",
                schema: "Purchasing",
                table: "Suppliers",
                columns: new[] { "TenantId", "UpdatedByUserId" });

            migrationBuilder.Sql("""
                -- Migration authority sees all tenants. Number active drafts deterministically, retaining creation timestamps.
                WITH Ordered AS (SELECT Id,ROW_NUMBER() OVER(PARTITION BY TenantId ORDER BY CreatedAtUtc,Id) Number FROM Purchasing.DraftOrders WHERE IsDeleted=0)
                UPDATE d SET PoNumber=o.Number FROM Purchasing.DraftOrders d JOIN Ordered o ON o.Id=d.Id;
                INSERT Purchasing.PurchaseOrderCounters(TenantId,LastNumber)
                  SELECT TenantId,MAX(PoNumber) FROM Purchasing.DraftOrders WHERE PoNumber IS NOT NULL GROUP BY TenantId;
                """);
            migrationBuilder.AddForeignKey(
                name: "FK_DraftOrders_Suppliers_TenantId_SupplierId",
                schema: "Purchasing",
                table: "DraftOrders",
                columns: new[] { "TenantId", "SupplierId" },
                principalSchema: "Purchasing",
                principalTable: "Suppliers",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
            PurchasingIdentitySchema.Protect(migrationBuilder, "20260912064156_AddSupplierIdentityAndPurchaseReferences");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("THROW 50020, 'Supplier identity and purchase references require forward correction or guarded recovery.', 1;");
    }
}
