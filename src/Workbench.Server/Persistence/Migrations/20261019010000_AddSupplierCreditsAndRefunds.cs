// Copyright (c) 2026 The White Stag Collection.
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierCreditsAndRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SupplierFinancialGroups_Operation",
                schema: "Purchasing",
                table: "SupplierFinancialGroups");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialEvidenceSets_Owner",
                schema: "Accounting",
                table: "FinancialEvidenceSets");

            migrationBuilder.CreateTable(
                name: "SupplierCredits",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Total = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NormalizedReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    SourceSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierCredits", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SupplierCredits_Source", "[Total]>0 AND [DocumentDate]<=[PostingDate] AND [EffectiveDate]<=[PostingDate] AND LEN([NormalizedReference])>0 AND LEN([Reason])>0 AND ISJSON([SourceSnapshotJson],OBJECT)=1 AND DATALENGTH([SourceSnapshotJson])<=262144");
                    table.ForeignKey(
                        name: "FK_SupplierCredits_DraftOrders_TenantId_PurchaseOrderId",
                        columns: x => new { x.TenantId, x.PurchaseOrderId },
                        principalSchema: "Purchasing",
                        principalTable: "DraftOrders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCredits_SupplierFinancialGroups_TenantId_GroupId",
                        columns: x => new { x.TenantId, x.GroupId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierFinancialGroups",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCredits_Suppliers_TenantId_SupplierId",
                        columns: x => new { x.TenantId, x.SupplierId },
                        principalSchema: "Purchasing",
                        principalTable: "Suppliers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCredits_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierCreditClaims",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreditId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BillId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BillRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecognitionEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComponentKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Treatment = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalJournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalJournalOrdinal = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierCreditClaims", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SupplierCreditClaims_Amount", "[Amount]>0 AND [Treatment] COLLATE Latin1_General_100_BIN2 IN ('Expense','Prepayment','Inventory','RecoverableTax')");
                    table.ForeignKey(
                        name: "FK_SupplierCreditClaims_Accounts_TenantId_AccountId",
                        columns: x => new { x.TenantId, x.AccountId },
                        principalSchema: "Accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCreditClaims_JournalLines_TenantId_OriginalJournalId_OriginalJournalOrdinal",
                        columns: x => new { x.TenantId, x.OriginalJournalId, x.OriginalJournalOrdinal },
                        principalSchema: "Accounting",
                        principalTable: "JournalLines",
                        principalColumns: new[] { "TenantId", "JournalId", "Ordinal" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCreditClaims_RecognitionComponents_TenantId_RecognitionEventId_ComponentKey",
                        columns: x => new { x.TenantId, x.RecognitionEventId, x.ComponentKey },
                        principalSchema: "Purchasing",
                        principalTable: "RecognitionComponents",
                        principalColumns: new[] { "TenantId", "EventId", "ComponentKey" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCreditClaims_SupplierBillRevisions_TenantId_BillId_BillRevisionId",
                        columns: x => new { x.TenantId, x.BillId, x.BillRevisionId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierBillRevisions",
                        principalColumns: new[] { "TenantId", "BillId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCreditClaims_SupplierCredits_TenantId_CreditId",
                        columns: x => new { x.TenantId, x.CreditId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierCredits",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierCreditVersions",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreditId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierCreditVersions", x => new { x.TenantId, x.CreditId });
                    table.ForeignKey(
                        name: "FK_SupplierCreditVersions_SupplierCredits_TenantId_CreditId",
                        columns: x => new { x.TenantId, x.CreditId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierCredits",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SupplierFinancialGroups_Operation",
                schema: "Purchasing",
                table: "SupplierFinancialGroups",
                sql: "[Operation] COLLATE Latin1_General_100_BIN2 IN ('OpenRecognitionPayable','RecordPayment','Apply','ReverseApplication','CorrectPayment','CorrectSource','RecordCredit')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialEvidenceSets_Owner",
                schema: "Accounting",
                table: "FinancialEvidenceSets",
                sql: "[OwnerKind] COLLATE Latin1_General_100_BIN2 IN ('SupplierBill','SupplierPayment','PurchaseRecognition','SupplierCredit')");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCreditClaims_TenantId_AccountId",
                schema: "Purchasing",
                table: "SupplierCreditClaims",
                columns: new[] { "TenantId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCreditClaims_TenantId_BillId_BillRevisionId",
                schema: "Purchasing",
                table: "SupplierCreditClaims",
                columns: new[] { "TenantId", "BillId", "BillRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCreditClaims_TenantId_CreditId_RecognitionEventId_ComponentKey",
                schema: "Purchasing",
                table: "SupplierCreditClaims",
                columns: new[] { "TenantId", "CreditId", "RecognitionEventId", "ComponentKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCreditClaims_TenantId_OriginalJournalId_OriginalJournalOrdinal",
                schema: "Purchasing",
                table: "SupplierCreditClaims",
                columns: new[] { "TenantId", "OriginalJournalId", "OriginalJournalOrdinal" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCreditClaims_TenantId_RecognitionEventId_ComponentKey",
                schema: "Purchasing",
                table: "SupplierCreditClaims",
                columns: new[] { "TenantId", "RecognitionEventId", "ComponentKey" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCredits_TenantId_ActorId",
                schema: "Purchasing",
                table: "SupplierCredits",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCredits_TenantId_GroupId",
                schema: "Purchasing",
                table: "SupplierCredits",
                columns: new[] { "TenantId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCredits_TenantId_PurchaseOrderId",
                schema: "Purchasing",
                table: "SupplierCredits",
                columns: new[] { "TenantId", "PurchaseOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCredits_TenantId_RevisionId",
                schema: "Purchasing",
                table: "SupplierCredits",
                columns: new[] { "TenantId", "RevisionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCredits_TenantId_SupplierId_NormalizedReference",
                schema: "Purchasing",
                table: "SupplierCredits",
                columns: new[] { "TenantId", "SupplierId", "NormalizedReference" });
            SupplierCreditSchema.Up(migrationBuilder, "20261019010000_AddSupplierCreditsAndRefunds");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("THROW 50020, 'Supplier credit financial history cannot be rolled back destructively.', 1;");
        }
    }
}
