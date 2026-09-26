using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseRecognition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecognitionGroupReceipts",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommandKind = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    CommandVersion = table.Column<int>(type: "int", nullable: false),
                    CanonicalInput = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InputSha256 = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecognitionGroupReceipts", x => new { x.TenantId, x.RequestId });
                    table.CheckConstraint("CK_RecognitionGroupReceipts_Input", "[RequestId]<>'00000000-0000-0000-0000-000000000000' AND [CommandVersion]=1 AND [CommandKind] IN ('Post','Reverse','Replace') AND ISJSON([CanonicalInput],OBJECT)=1 AND DATALENGTH([CanonicalInput])<=262144 AND DATALENGTH([InputSha256])=32 AND ISJSON([ResultJson],OBJECT)=1 AND DATALENGTH([ResultJson])<=262144");
                    table.ForeignKey(
                        name: "FK_RecognitionGroupReceipts_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecognitionUnits",
                schema: "Purchasing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchaseOrderRevision = table.Column<int>(type: "int", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    GoodsReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Classification = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(28,6)", precision: 28, scale: 6, nullable: false),
                    QuantityUnit = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PolicyVersion = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecognitionUnits", x => x.Id);
                    table.UniqueConstraint("AK_RecognitionUnits_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_RecognitionUnits_Identity", "[PurchaseOrderRevision]>0 AND [PolicyVersion]>0 AND [Quantity]>0 AND [Classification] IN ('Expense','Inventory') AND DATALENGTH([GoodsReference])>0 AND DATALENGTH([QuantityUnit])>0");
                    table.ForeignKey(
                        name: "FK_RecognitionUnits_DraftOrders_TenantId_PurchaseOrderId",
                        columns: x => new { x.TenantId, x.PurchaseOrderId },
                        principalSchema: "Purchasing",
                        principalTable: "DraftOrders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecognitionUnits_Suppliers_TenantId_SupplierId",
                        columns: x => new { x.TenantId, x.SupplierId },
                        principalSchema: "Purchasing",
                        principalTable: "Suppliers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecognitionUnits_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "Tenancy",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecognitionCorrectionGroups",
                schema: "Purchasing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecognitionCorrectionGroups", x => x.Id);
                    table.UniqueConstraint("AK_RecognitionCorrectionGroups_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_RecognitionCorrectionGroups_Operation", "[Operation] IN ('Reverse','Replace') AND DATALENGTH([Reason])>0 AND DATALENGTH([Reason])<=4000");
                    table.ForeignKey(
                        name: "FK_RecognitionCorrectionGroups_RecognitionUnits_TenantId_UnitId",
                        columns: x => new { x.TenantId, x.UnitId },
                        principalSchema: "Purchasing",
                        principalTable: "RecognitionUnits",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecognitionCorrectionGroups_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecognitionSideEvents",
                schema: "Purchasing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Side = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    EventRevision = table.Column<int>(type: "int", nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRevision = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceComponentKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SubdivisionKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceQuantity = table.Column<decimal>(type: "decimal(28,6)", precision: 28, scale: 6, nullable: false),
                    SourceAmount = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ConfigurationVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EvidenceSha256 = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    JournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrectionGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecognitionSideEvents", x => x.Id);
                    table.UniqueConstraint("AK_RecognitionSideEvents_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.UniqueConstraint("AK_RecognitionSideEvents_TenantId_UnitId_Id", x => new { x.TenantId, x.UnitId, x.Id });
                    table.CheckConstraint("CK_RecognitionSideEvents_Evidence", "[Side] IN ('Recognition','Invoice') AND [EventRevision]>0 AND [SourceQuantity]>0 AND [SourceAmount]>=0 AND ISJSON([EvidenceJson],OBJECT)=1 AND DATALENGTH([EvidenceJson])<=262144 AND DATALENGTH([EvidenceSha256])=32");
                    table.ForeignKey(
                        name: "FK_RecognitionSideEvents_JournalEntries_TenantId_JournalId",
                        columns: x => new { x.TenantId, x.JournalId },
                        principalSchema: "Accounting",
                        principalTable: "JournalEntries",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecognitionSideEvents_RecognitionCorrectionGroups_TenantId_CorrectionGroupId",
                        columns: x => new { x.TenantId, x.CorrectionGroupId },
                        principalSchema: "Purchasing",
                        principalTable: "RecognitionCorrectionGroups",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecognitionSideEvents_RecognitionUnits_TenantId_UnitId",
                        columns: x => new { x.TenantId, x.UnitId },
                        principalSchema: "Purchasing",
                        principalTable: "RecognitionUnits",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecognitionSideEvents_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecognitionComponents",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComponentKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AssignedCostComponentKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecognitionComponents", x => new { x.TenantId, x.EventId, x.ComponentKey });
                    table.CheckConstraint("CK_RecognitionComponents_Kind", "[Kind] IN ('BaseCost','Discount','Freight','Charge','NonrecoverableTax','RecoverableTax','Rounding') AND ([Amount]>=0 OR [Kind]='Rounding')");
                    table.ForeignKey(
                        name: "FK_RecognitionComponents_RecognitionSideEvents_TenantId_EventId",
                        columns: x => new { x.TenantId, x.EventId },
                        principalSchema: "Purchasing",
                        principalTable: "RecognitionSideEvents",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecognitionMatches",
                schema: "Purchasing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecognitionEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorrectionGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecognitionMatches", x => x.Id);
                    table.UniqueConstraint("AK_RecognitionMatches_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_RecognitionMatches_RecognitionCorrectionGroups_TenantId_CorrectionGroupId",
                        columns: x => new { x.TenantId, x.CorrectionGroupId },
                        principalSchema: "Purchasing",
                        principalTable: "RecognitionCorrectionGroups",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecognitionMatches_RecognitionSideEvents_TenantId_UnitId_InvoiceEventId",
                        columns: x => new { x.TenantId, x.UnitId, x.InvoiceEventId },
                        principalSchema: "Purchasing",
                        principalTable: "RecognitionSideEvents",
                        principalColumns: new[] { "TenantId", "UnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecognitionMatches_RecognitionSideEvents_TenantId_UnitId_RecognitionEventId",
                        columns: x => new { x.TenantId, x.UnitId, x.RecognitionEventId },
                        principalSchema: "Purchasing",
                        principalTable: "RecognitionSideEvents",
                        principalColumns: new[] { "TenantId", "UnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecognitionMatches_RecognitionUnits_TenantId_UnitId",
                        columns: x => new { x.TenantId, x.UnitId },
                        principalSchema: "Purchasing",
                        principalTable: "RecognitionUnits",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionCorrectionGroups_TenantId_ActorId",
                schema: "Purchasing",
                table: "RecognitionCorrectionGroups",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionCorrectionGroups_TenantId_UnitId",
                schema: "Purchasing",
                table: "RecognitionCorrectionGroups",
                columns: new[] { "TenantId", "UnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionGroupReceipts_TenantId_ActorId",
                schema: "Purchasing",
                table: "RecognitionGroupReceipts",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionMatches_TenantId_CorrectionGroupId",
                schema: "Purchasing",
                table: "RecognitionMatches",
                columns: new[] { "TenantId", "CorrectionGroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionMatches_TenantId_InvoiceEventId",
                schema: "Purchasing",
                table: "RecognitionMatches",
                columns: new[] { "TenantId", "InvoiceEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionMatches_TenantId_RecognitionEventId",
                schema: "Purchasing",
                table: "RecognitionMatches",
                columns: new[] { "TenantId", "RecognitionEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionMatches_TenantId_UnitId_InvoiceEventId",
                schema: "Purchasing",
                table: "RecognitionMatches",
                columns: new[] { "TenantId", "UnitId", "InvoiceEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionMatches_TenantId_UnitId_RecognitionEventId",
                schema: "Purchasing",
                table: "RecognitionMatches",
                columns: new[] { "TenantId", "UnitId", "RecognitionEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionSideEvents_TenantId_ActorId",
                schema: "Purchasing",
                table: "RecognitionSideEvents",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionSideEvents_TenantId_CorrectionGroupId",
                schema: "Purchasing",
                table: "RecognitionSideEvents",
                columns: new[] { "TenantId", "CorrectionGroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionSideEvents_TenantId_JournalId",
                schema: "Purchasing",
                table: "RecognitionSideEvents",
                columns: new[] { "TenantId", "JournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionSideEvents_TenantId_PostingDate_Id",
                schema: "Purchasing",
                table: "RecognitionSideEvents",
                columns: new[] { "TenantId", "PostingDate", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionSideEvents_TenantId_Side_SourceId_SourceRevision_SourceComponentKey_SubdivisionKey",
                schema: "Purchasing",
                table: "RecognitionSideEvents",
                columns: new[] { "TenantId", "Side", "SourceId", "SourceRevision", "SourceComponentKey", "SubdivisionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionSideEvents_TenantId_UnitId_Side_EventRevision",
                schema: "Purchasing",
                table: "RecognitionSideEvents",
                columns: new[] { "TenantId", "UnitId", "Side", "EventRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionUnits_TenantId_PurchaseOrderId_Id",
                schema: "Purchasing",
                table: "RecognitionUnits",
                columns: new[] { "TenantId", "PurchaseOrderId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionUnits_TenantId_SupplierId",
                schema: "Purchasing",
                table: "RecognitionUnits",
                columns: new[] { "TenantId", "SupplierId" });
            PurchaseRecognitionSchema.Up(migrationBuilder, "20260926210900_AddPurchaseRecognition");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("THROW 50020, 'Purchase recognition history requires a reviewed forward correction or protected restore.', 1;");
        }
    }
}
