// Copyright (c) 2026 The White Stag Collection.
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    public partial class AddSupplierCreditsAndRefunds
    {
        private static void CreateSupplierSourceCorrections(MigrationBuilder migrationBuilder)
        {


            migrationBuilder.CreateTable(
                name: "SupplierSourceCorrections",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceKind = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalCreditId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OriginalBillId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OriginalCashSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReplacementSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReplacementSourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    OriginalSourceVersion = table.Column<byte[]>(type: "binary(8)", fixedLength: true, maxLength: 8, nullable: false),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierSourceCorrections", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SupplierSourceCorrections_Owner", "LEN([Reason])>0 AND (([SourceKind] COLLATE Latin1_General_100_BIN2='SupplierCredit' AND [OriginalCreditId] IS NOT NULL AND [OriginalCreditId]=[SourceId] AND [OriginalBillId] IS NULL AND [OriginalCashSourceId] IS NULL) OR ([SourceKind] COLLATE Latin1_General_100_BIN2='SupplierBill' AND [OriginalBillId] IS NOT NULL AND [OriginalBillId]=[SourceId] AND [OriginalCreditId] IS NULL AND [OriginalCashSourceId] IS NULL) OR ([SourceKind] COLLATE Latin1_General_100_BIN2 IN ('SupplierRefund','SupplierRefundRepayment') AND [OriginalCashSourceId] IS NOT NULL AND [OriginalCashSourceId]=[SourceId] AND [OriginalCreditId] IS NULL AND [OriginalBillId] IS NULL)) AND (([ReplacementSourceId] IS NULL AND [ReplacementSourceRevisionId] IS NULL) OR ([ReplacementSourceId] IS NOT NULL AND [ReplacementSourceRevisionId] IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_SupplierSourceCorrections_DraftOrders_TenantId_PurchaseOrderId",
                        columns: x => new { x.TenantId, x.PurchaseOrderId },
                        principalSchema: "Purchasing",
                        principalTable: "DraftOrders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierSourceCorrections_SupplierBills_TenantId_OriginalBillId",
                        columns: x => new { x.TenantId, x.OriginalBillId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierBills",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierSourceCorrections_SupplierCashSources_TenantId_OriginalCashSourceId",
                        columns: x => new { x.TenantId, x.OriginalCashSourceId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierCashSources",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierSourceCorrections_SupplierCredits_TenantId_OriginalCreditId",
                        columns: x => new { x.TenantId, x.OriginalCreditId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierCredits",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierSourceCorrections_SupplierFinancialGroups_TenantId_GroupId",
                        columns: x => new { x.TenantId, x.GroupId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierFinancialGroups",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierSourceCorrections_Suppliers_TenantId_SupplierId",
                        columns: x => new { x.TenantId, x.SupplierId },
                        principalSchema: "Purchasing",
                        principalTable: "Suppliers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierSourceCorrections_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierCashSettlementReleases",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SettlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorrectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RestoredMovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpeningMovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    ClearingAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClearingAccountVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierCashSettlementReleases", x => new { x.TenantId, x.SettlementId });
                    table.CheckConstraint("CK_SupplierCashSettlementReleases_Amount", "[Amount]>0");
                    table.ForeignKey(
                        name: "FK_SupplierCashSettlementReleases_Accounts_TenantId_ClearingAccountId",
                        columns: x => new { x.TenantId, x.ClearingAccountId },
                        principalSchema: "Accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCashSettlementReleases_JournalEntries_TenantId_JournalId",
                        columns: x => new { x.TenantId, x.JournalId },
                        principalSchema: "Accounting",
                        principalTable: "JournalEntries",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCashSettlementReleases_SupplierCashSettlements_TenantId_SettlementId",
                        columns: x => new { x.TenantId, x.SettlementId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierCashSettlements",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCashSettlementReleases_SupplierFinancialGroups_TenantId_GroupId",
                        columns: x => new { x.TenantId, x.GroupId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierFinancialGroups",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCashSettlementReleases_SupplierItemMovements_TenantId_OpeningMovementId",
                        columns: x => new { x.TenantId, x.OpeningMovementId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierItemMovements",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCashSettlementReleases_SupplierItemMovements_TenantId_RestoredMovementId",
                        columns: x => new { x.TenantId, x.RestoredMovementId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierItemMovements",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCashSettlementReleases_SupplierSourceCorrections_TenantId_CorrectionId",
                        columns: x => new { x.TenantId, x.CorrectionId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierSourceCorrections",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCashSettlementReleases_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierSourceCorrectionMembers",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorrectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecognitionEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EffectsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierSourceCorrectionMembers", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SupplierSourceCorrectionMembers_Effects", "[Kind] COLLATE Latin1_General_100_BIN2 IN ('ReverseSource','ReverseApplication','ReleaseSettlement','Replacement','Application','RecognitionAdjustment') AND ISJSON([EffectsJson],OBJECT)=1 AND DATALENGTH([EffectsJson])<=262144");
                    table.ForeignKey(
                        name: "FK_SupplierSourceCorrectionMembers_JournalEntries_TenantId_JournalId",
                        columns: x => new { x.TenantId, x.JournalId },
                        principalSchema: "Accounting",
                        principalTable: "JournalEntries",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierSourceCorrectionMembers_RecognitionSideEvents_TenantId_RecognitionEventId",
                        columns: x => new { x.TenantId, x.RecognitionEventId },
                        principalSchema: "Purchasing",
                        principalTable: "RecognitionSideEvents",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierSourceCorrectionMembers_SupplierFinancialGroups_TenantId_GroupId",
                        columns: x => new { x.TenantId, x.GroupId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierFinancialGroups",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierSourceCorrectionMembers_SupplierOpenItems_TenantId_ItemId",
                        columns: x => new { x.TenantId, x.ItemId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierOpenItems",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierSourceCorrectionMembers_SupplierSourceCorrections_TenantId_CorrectionId",
                        columns: x => new { x.TenantId, x.CorrectionId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierSourceCorrections",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });



            CreateSupplierSourceCorrectionIndexes(migrationBuilder);
        }
    }
}