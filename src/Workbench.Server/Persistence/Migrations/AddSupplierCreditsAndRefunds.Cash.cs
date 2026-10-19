// Copyright (c) 2026 The White Stag Collection.
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    public partial class AddSupplierCreditsAndRefunds
    {
        private static void CreateSupplierCash(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SupplierItemMovements_Amount",
                schema: "Purchasing",
                table: "SupplierItemMovements");



            migrationBuilder.CreateTable(
                name: "SupplierCashSources",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    ActualDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    Method = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    FundingAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FundingAccountVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FundingAccountPurpose = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SourceSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierCashSources", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SupplierCashSources_Source", "[Amount]>0 AND [ActualDate]<=[PostingDate] AND [EffectiveDate]<=[PostingDate] AND [Kind] COLLATE Latin1_General_100_BIN2 IN ('SupplierRefund','SupplierRefundRepayment') AND [Method] COLLATE Latin1_General_100_BIN2 IN ('Bank','Cash') AND [Method]=[FundingAccountPurpose] AND ISJSON([SourceSnapshotJson],OBJECT)=1 AND DATALENGTH([SourceSnapshotJson])<=262144");
                    table.ForeignKey(
                        name: "FK_SupplierCashSources_Accounts_TenantId_FundingAccountId",
                        columns: x => new { x.TenantId, x.FundingAccountId },
                        principalSchema: "Accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCashSources_DraftOrders_TenantId_PurchaseOrderId",
                        columns: x => new { x.TenantId, x.PurchaseOrderId },
                        principalSchema: "Purchasing",
                        principalTable: "DraftOrders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCashSources_SupplierFinancialGroups_TenantId_GroupId",
                        columns: x => new { x.TenantId, x.GroupId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierFinancialGroups",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCashSources_Suppliers_TenantId_SupplierId",
                        columns: x => new { x.TenantId, x.SupplierId },
                        principalSchema: "Purchasing",
                        principalTable: "Suppliers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCashSources_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierCashSettlements",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CashSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    MovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JournalOrdinal = table.Column<int>(type: "int", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierCashSettlements", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SupplierCashSettlements_Amount", "[Amount]>0");
                    table.ForeignKey(
                        name: "FK_SupplierCashSettlements_Accounts_TenantId_AccountId",
                        columns: x => new { x.TenantId, x.AccountId },
                        principalSchema: "Accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCashSettlements_JournalLines_TenantId_JournalId_JournalOrdinal",
                        columns: x => new { x.TenantId, x.JournalId, x.JournalOrdinal },
                        principalSchema: "Accounting",
                        principalTable: "JournalLines",
                        principalColumns: new[] { "TenantId", "JournalId", "Ordinal" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCashSettlements_SupplierCashSources_TenantId_CashSourceId",
                        columns: x => new { x.TenantId, x.CashSourceId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierCashSources",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCashSettlements_SupplierItemMovements_TenantId_MovementId",
                        columns: x => new { x.TenantId, x.MovementId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierItemMovements",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCashSettlements_SupplierOpenItems_TenantId_ItemId",
                        columns: x => new { x.TenantId, x.ItemId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierOpenItems",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierCashVersions",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CashSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierCashVersions", x => new { x.TenantId, x.CashSourceId });
                    table.ForeignKey(
                        name: "FK_SupplierCashVersions_SupplierCashSources_TenantId_CashSourceId",
                        columns: x => new { x.TenantId, x.CashSourceId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierCashSources",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SupplierItemMovements_Amount",
                schema: "Purchasing",
                table: "SupplierItemMovements",
                sql: "[Amount]<>0 AND [EventKind] COLLATE Latin1_General_100_BIN2 IN ('Open','Apply','ReverseApplication','ReverseSource','CashSettlement')");



            migrationBuilder.CreateIndex(
                name: "IX_SupplierCashSettlements_TenantId_AccountId",
                schema: "Purchasing",
                table: "SupplierCashSettlements",
                columns: new[] { "TenantId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCashSettlements_TenantId_CashSourceId_ItemId",
                schema: "Purchasing",
                table: "SupplierCashSettlements",
                columns: new[] { "TenantId", "CashSourceId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCashSettlements_TenantId_ItemId",
                schema: "Purchasing",
                table: "SupplierCashSettlements",
                columns: new[] { "TenantId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCashSettlements_TenantId_JournalId_JournalOrdinal",
                schema: "Purchasing",
                table: "SupplierCashSettlements",
                columns: new[] { "TenantId", "JournalId", "JournalOrdinal" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCashSettlements_TenantId_MovementId",
                schema: "Purchasing",
                table: "SupplierCashSettlements",
                columns: new[] { "TenantId", "MovementId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCashSources_TenantId_ActorId",
                schema: "Purchasing",
                table: "SupplierCashSources",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCashSources_TenantId_FundingAccountId",
                schema: "Purchasing",
                table: "SupplierCashSources",
                columns: new[] { "TenantId", "FundingAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCashSources_TenantId_GroupId",
                schema: "Purchasing",
                table: "SupplierCashSources",
                columns: new[] { "TenantId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCashSources_TenantId_PurchaseOrderId",
                schema: "Purchasing",
                table: "SupplierCashSources",
                columns: new[] { "TenantId", "PurchaseOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCashSources_TenantId_RevisionId",
                schema: "Purchasing",
                table: "SupplierCashSources",
                columns: new[] { "TenantId", "RevisionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCashSources_TenantId_SupplierId_PurchaseOrderId_PostingDate",
                schema: "Purchasing",
                table: "SupplierCashSources",
                columns: new[] { "TenantId", "SupplierId", "PurchaseOrderId", "PostingDate" });

        }
    }
}