using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierOpenItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupplierFinancialGroups",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierFinancialGroups", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SupplierFinancialGroups_Operation", "[Operation] COLLATE Latin1_General_100_BIN2 IN ('OpenRecognitionPayable','RecordPayment','Apply','ReverseApplication','CorrectPayment','CorrectSource')");
                });

            migrationBuilder.CreateTable(
                name: "SupplierOpenItems",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    SourceKind = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BillId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourcePostingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    SourceSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierOpenItems", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SupplierOpenItems_Kind", "[Kind] COLLATE Latin1_General_100_BIN2 IN ('Payable','Advance','CreditReceivable','RefundClearing') AND ISJSON([SourceSnapshotJson],OBJECT)=1 AND DATALENGTH([SourceSnapshotJson])<=262144");
                    table.ForeignKey(
                        name: "FK_SupplierOpenItems_DraftOrders_TenantId_PurchaseOrderId",
                        columns: x => new { x.TenantId, x.PurchaseOrderId },
                        principalSchema: "Purchasing",
                        principalTable: "DraftOrders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierOpenItems_SupplierBills_TenantId_BillId",
                        columns: x => new { x.TenantId, x.BillId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierBills",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierOpenItems_Suppliers_TenantId_SupplierId",
                        columns: x => new { x.TenantId, x.SupplierId },
                        principalSchema: "Purchasing",
                        principalTable: "Suppliers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierFinancialReceipts",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CanonicalInput = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InputSha256 = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierFinancialReceipts", x => new { x.TenantId, x.RequestId });
                    table.CheckConstraint("CK_SupplierFinancialReceipts_Input", "[RequestId]<>'00000000-0000-0000-0000-000000000000' AND ISJSON([CanonicalInput],OBJECT)=1 AND DATALENGTH([CanonicalInput])<=262144 AND DATALENGTH([InputSha256])=32 AND ISJSON([ResultJson],OBJECT)=1");
                    table.ForeignKey(
                        name: "FK_SupplierFinancialReceipts_SupplierFinancialGroups_TenantId_GroupId",
                        columns: x => new { x.TenantId, x.GroupId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierFinancialGroups",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierFinancialReceipts_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierPayments",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    PaymentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    Method = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    FundingAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FundingAccountVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FundingAccountPurpose = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierPayments", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SupplierPayments_Details", "[Amount]>0 AND [PaymentDate]<=[PostingDate] AND [EffectiveDate]<=[PostingDate] AND [Method] COLLATE Latin1_General_100_BIN2 IN ('Bank','Cash') AND ISJSON([EvidenceJson],OBJECT)=1 AND DATALENGTH([EvidenceJson])<=262144");
                    table.ForeignKey(
                        name: "FK_SupplierPayments_Accounts_TenantId_FundingAccountId",
                        columns: x => new { x.TenantId, x.FundingAccountId },
                        principalSchema: "Accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierPayments_DraftOrders_TenantId_PurchaseOrderId",
                        columns: x => new { x.TenantId, x.PurchaseOrderId },
                        principalSchema: "Purchasing",
                        principalTable: "DraftOrders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierPayments_SupplierFinancialGroups_TenantId_GroupId",
                        columns: x => new { x.TenantId, x.GroupId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierFinancialGroups",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierPayments_Suppliers_TenantId_SupplierId",
                        columns: x => new { x.TenantId, x.SupplierId },
                        principalSchema: "Purchasing",
                        principalTable: "Suppliers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierPayments_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierApplications",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FundingItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DebtItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierApplications", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SupplierApplications_Amount", "[Amount]>0 AND [FundingItemId]<>[DebtItemId]");
                    table.ForeignKey(
                        name: "FK_SupplierApplications_SupplierFinancialGroups_TenantId_GroupId",
                        columns: x => new { x.TenantId, x.GroupId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierFinancialGroups",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierApplications_SupplierOpenItems_TenantId_DebtItemId",
                        columns: x => new { x.TenantId, x.DebtItemId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierOpenItems",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierApplications_SupplierOpenItems_TenantId_FundingItemId",
                        columns: x => new { x.TenantId, x.FundingItemId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierOpenItems",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierApplications_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierItemMovements",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventKind = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    SourceEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecognitionEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierItemMovements", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SupplierItemMovements_Amount", "[Amount]<>0 AND [EventKind] COLLATE Latin1_General_100_BIN2 IN ('Open','Apply','ReverseApplication','ReverseSource')");
                    table.ForeignKey(
                        name: "FK_SupplierItemMovements_RecognitionSideEvents_TenantId_RecognitionEventId",
                        columns: x => new { x.TenantId, x.RecognitionEventId },
                        principalSchema: "Purchasing",
                        principalTable: "RecognitionSideEvents",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierItemMovements_SourceEvents_TenantId_SourceEventId",
                        columns: x => new { x.TenantId, x.SourceEventId },
                        principalSchema: "Accounting",
                        principalTable: "SourceEvents",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierItemMovements_SupplierFinancialGroups_TenantId_GroupId",
                        columns: x => new { x.TenantId, x.GroupId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierFinancialGroups",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierItemMovements_SupplierOpenItems_TenantId_ItemId",
                        columns: x => new { x.TenantId, x.ItemId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierOpenItems",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierItemVersions",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierItemVersions", x => new { x.TenantId, x.ItemId });
                    table.ForeignKey(
                        name: "FK_SupplierItemVersions_SupplierOpenItems_TenantId_ItemId",
                        columns: x => new { x.TenantId, x.ItemId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierOpenItems",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierPaymentCorrections",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalPaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReplacementPaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierPaymentCorrections", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SupplierPaymentCorrections_Reason", "DATALENGTH([Reason])>0 AND DATALENGTH([Reason])<=4000");
                    table.ForeignKey(
                        name: "FK_SupplierPaymentCorrections_SupplierFinancialGroups_TenantId_GroupId",
                        columns: x => new { x.TenantId, x.GroupId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierFinancialGroups",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierPaymentCorrections_SupplierPayments_TenantId_OriginalPaymentId",
                        columns: x => new { x.TenantId, x.OriginalPaymentId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierPayments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierPaymentCorrections_SupplierPayments_TenantId_ReplacementPaymentId",
                        columns: x => new { x.TenantId, x.ReplacementPaymentId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierPayments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierPaymentCorrections_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierPaymentVersions",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierPaymentVersions", x => new { x.TenantId, x.PaymentId });
                    table.ForeignKey(
                        name: "FK_SupplierPaymentVersions_SupplierPayments_TenantId_PaymentId",
                        columns: x => new { x.TenantId, x.PaymentId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierPayments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierApplicationReversals",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierApplicationReversals", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SupplierApplicationReversals_Reason", "DATALENGTH([Reason])>0 AND DATALENGTH([Reason])<=4000");
                    table.ForeignKey(
                        name: "FK_SupplierApplicationReversals_SupplierApplications_TenantId_ApplicationId",
                        columns: x => new { x.TenantId, x.ApplicationId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierApplications",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierApplicationReversals_SupplierFinancialGroups_TenantId_GroupId",
                        columns: x => new { x.TenantId, x.GroupId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierFinancialGroups",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierApplicationReversals_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierApplicationVersions",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierApplicationVersions", x => new { x.TenantId, x.ApplicationId });
                    table.ForeignKey(
                        name: "FK_SupplierApplicationVersions_SupplierApplications_TenantId_ApplicationId",
                        columns: x => new { x.TenantId, x.ApplicationId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierApplications",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierControlAttributions",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountPurpose = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierControlAttributions", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SupplierControlAttributions_Amount", "[Amount]<>0");
                    table.ForeignKey(
                        name: "FK_SupplierControlAttributions_Accounts_TenantId_AccountId",
                        columns: x => new { x.TenantId, x.AccountId },
                        principalSchema: "Accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierControlAttributions_JournalLines_TenantId_JournalId_Ordinal",
                        columns: x => new { x.TenantId, x.JournalId, x.Ordinal },
                        principalSchema: "Accounting",
                        principalTable: "JournalLines",
                        principalColumns: new[] { "TenantId", "JournalId", "Ordinal" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierControlAttributions_SupplierFinancialGroups_TenantId_GroupId",
                        columns: x => new { x.TenantId, x.GroupId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierFinancialGroups",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierControlAttributions_SupplierItemMovements_TenantId_MovementId",
                        columns: x => new { x.TenantId, x.MovementId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierItemMovements",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierApplicationReversals_TenantId_ActorId",
                schema: "Purchasing",
                table: "SupplierApplicationReversals",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierApplicationReversals_TenantId_ApplicationId",
                schema: "Purchasing",
                table: "SupplierApplicationReversals",
                columns: new[] { "TenantId", "ApplicationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierApplicationReversals_TenantId_GroupId",
                schema: "Purchasing",
                table: "SupplierApplicationReversals",
                columns: new[] { "TenantId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierApplications_TenantId_ActorId",
                schema: "Purchasing",
                table: "SupplierApplications",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierApplications_TenantId_DebtItemId",
                schema: "Purchasing",
                table: "SupplierApplications",
                columns: new[] { "TenantId", "DebtItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierApplications_TenantId_FundingItemId",
                schema: "Purchasing",
                table: "SupplierApplications",
                columns: new[] { "TenantId", "FundingItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierApplications_TenantId_GroupId",
                schema: "Purchasing",
                table: "SupplierApplications",
                columns: new[] { "TenantId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierControlAttributions_TenantId_AccountId",
                schema: "Purchasing",
                table: "SupplierControlAttributions",
                columns: new[] { "TenantId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierControlAttributions_TenantId_GroupId",
                schema: "Purchasing",
                table: "SupplierControlAttributions",
                columns: new[] { "TenantId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierControlAttributions_TenantId_JournalId_Ordinal_MovementId",
                schema: "Purchasing",
                table: "SupplierControlAttributions",
                columns: new[] { "TenantId", "JournalId", "Ordinal", "MovementId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierControlAttributions_TenantId_MovementId",
                schema: "Purchasing",
                table: "SupplierControlAttributions",
                columns: new[] { "TenantId", "MovementId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierFinancialGroups_TenantId_SourceId_Operation",
                schema: "Purchasing",
                table: "SupplierFinancialGroups",
                columns: new[] { "TenantId", "SourceId", "Operation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierFinancialReceipts_TenantId_ActorId",
                schema: "Purchasing",
                table: "SupplierFinancialReceipts",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierFinancialReceipts_TenantId_GroupId",
                schema: "Purchasing",
                table: "SupplierFinancialReceipts",
                columns: new[] { "TenantId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierItemMovements_TenantId_GroupId",
                schema: "Purchasing",
                table: "SupplierItemMovements",
                columns: new[] { "TenantId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierItemMovements_TenantId_ItemId_PostingDate_Id",
                schema: "Purchasing",
                table: "SupplierItemMovements",
                columns: new[] { "TenantId", "ItemId", "PostingDate", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierItemMovements_TenantId_RecognitionEventId_EventKind",
                schema: "Purchasing",
                table: "SupplierItemMovements",
                columns: new[] { "TenantId", "RecognitionEventId", "EventKind" },
                unique: true,
                filter: "[RecognitionEventId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierItemMovements_TenantId_SourceEventId",
                schema: "Purchasing",
                table: "SupplierItemMovements",
                columns: new[] { "TenantId", "SourceEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierOpenItems_TenantId_BillId",
                schema: "Purchasing",
                table: "SupplierOpenItems",
                columns: new[] { "TenantId", "BillId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierOpenItems_TenantId_PurchaseOrderId",
                schema: "Purchasing",
                table: "SupplierOpenItems",
                columns: new[] { "TenantId", "PurchaseOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierOpenItems_TenantId_SourceKind_SourceId_SourceRevisionId_Kind",
                schema: "Purchasing",
                table: "SupplierOpenItems",
                columns: new[] { "TenantId", "SourceKind", "SourceId", "SourceRevisionId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierOpenItems_TenantId_SupplierId_PurchaseOrderId_Kind",
                schema: "Purchasing",
                table: "SupplierOpenItems",
                columns: new[] { "TenantId", "SupplierId", "PurchaseOrderId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPaymentCorrections_TenantId_ActorId",
                schema: "Purchasing",
                table: "SupplierPaymentCorrections",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPaymentCorrections_TenantId_GroupId",
                schema: "Purchasing",
                table: "SupplierPaymentCorrections",
                columns: new[] { "TenantId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPaymentCorrections_TenantId_OriginalPaymentId",
                schema: "Purchasing",
                table: "SupplierPaymentCorrections",
                columns: new[] { "TenantId", "OriginalPaymentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPaymentCorrections_TenantId_ReplacementPaymentId",
                schema: "Purchasing",
                table: "SupplierPaymentCorrections",
                columns: new[] { "TenantId", "ReplacementPaymentId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_TenantId_ActorId",
                schema: "Purchasing",
                table: "SupplierPayments",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_TenantId_FundingAccountId",
                schema: "Purchasing",
                table: "SupplierPayments",
                columns: new[] { "TenantId", "FundingAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_TenantId_GroupId",
                schema: "Purchasing",
                table: "SupplierPayments",
                columns: new[] { "TenantId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_TenantId_Id_RevisionId",
                schema: "Purchasing",
                table: "SupplierPayments",
                columns: new[] { "TenantId", "Id", "RevisionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_TenantId_PurchaseOrderId",
                schema: "Purchasing",
                table: "SupplierPayments",
                columns: new[] { "TenantId", "PurchaseOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_TenantId_SupplierId_PurchaseOrderId_PostingDate",
                schema: "Purchasing",
                table: "SupplierPayments",
                columns: new[] { "TenantId", "SupplierId", "PurchaseOrderId", "PostingDate" });
            SupplierOpenItemSchema.Up(migrationBuilder, "20260928071548_AddSupplierOpenItems");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("THROW 50020, 'Supplier open-item history requires forward correction or guarded recovery.', 1;");
            migrationBuilder.DropTable(
                name: "SupplierApplicationReversals",
                schema: "Purchasing");

            migrationBuilder.DropTable(
                name: "SupplierApplicationVersions",
                schema: "Purchasing");

            migrationBuilder.DropTable(
                name: "SupplierControlAttributions",
                schema: "Purchasing");

            migrationBuilder.DropTable(
                name: "SupplierFinancialReceipts",
                schema: "Purchasing");

            migrationBuilder.DropTable(
                name: "SupplierItemVersions",
                schema: "Purchasing");

            migrationBuilder.DropTable(
                name: "SupplierPaymentCorrections",
                schema: "Purchasing");

            migrationBuilder.DropTable(
                name: "SupplierPaymentVersions",
                schema: "Purchasing");

            migrationBuilder.DropTable(
                name: "SupplierApplications",
                schema: "Purchasing");

            migrationBuilder.DropTable(
                name: "SupplierItemMovements",
                schema: "Purchasing");

            migrationBuilder.DropTable(
                name: "SupplierPayments",
                schema: "Purchasing");

            migrationBuilder.DropTable(
                name: "SupplierOpenItems",
                schema: "Purchasing");

            migrationBuilder.DropTable(
                name: "SupplierFinancialGroups",
                schema: "Purchasing");
        }
    }
}
