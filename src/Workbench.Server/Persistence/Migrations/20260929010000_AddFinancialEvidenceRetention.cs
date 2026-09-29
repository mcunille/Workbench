// Copyright (c) 2026 The White Stag Collection.
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialEvidenceRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EvidenceMutationPermission",
                schema: "Purchasing",
                table: "RecognitionSideEvents",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IndependentHeld",
                schema: "Storage",
                table: "Attachments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "FinancialEvidenceAttachmentStates",
                schema: "Storage",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialEvidenceAttachmentStates", x => new { x.TenantId, x.AttachmentId });
                    table.ForeignKey(
                        name: "FK_FinancialEvidenceAttachmentStates_Attachments_TenantId_AttachmentId",
                        columns: x => new { x.TenantId, x.AttachmentId },
                        principalSchema: "Storage",
                        principalTable: "Attachments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FinancialEvidenceSets",
                schema: "Accounting",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerKind = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SourceSnapshotSha256 = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    MissingEvidenceReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LegacyEvidence = table.Column<bool>(type: "bit", nullable: false),
                    MutationPermission = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    InheritedEvidenceSetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialEvidenceSets", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_FinancialEvidenceSets_Owner", "[OwnerKind] COLLATE Latin1_General_100_BIN2 IN ('SupplierBill','SupplierPayment','PurchaseRecognition')");
                    table.ForeignKey(
                        name: "FK_FinancialEvidenceSets_DraftOrders_TenantId_PurchaseOrderId",
                        columns: x => new { x.TenantId, x.PurchaseOrderId },
                        principalSchema: "Purchasing",
                        principalTable: "DraftOrders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialEvidenceSets_FinancialEvidenceSets_TenantId_InheritedEvidenceSetId",
                        columns: x => new { x.TenantId, x.InheritedEvidenceSetId },
                        principalSchema: "Accounting",
                        principalTable: "FinancialEvidenceSets",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialEvidenceSets_Suppliers_TenantId_SupplierId",
                        columns: x => new { x.TenantId, x.SupplierId },
                        principalSchema: "Purchasing",
                        principalTable: "Suppliers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialEvidenceSets_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FinancialEvidenceLinks",
                schema: "Accounting",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvidenceSetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MediaType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Extension = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ConfigurationVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RetentionYears = table.Column<int>(type: "int", nullable: true),
                    RetentionRationale = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AnchorAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    MinimumRetentionDeadlineUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialEvidenceLinks", x => new { x.TenantId, x.Id });
                    table.UniqueConstraint("AK_FinancialEvidenceLinks_TenantId_EvidenceSetId_Id", x => new { x.TenantId, x.EvidenceSetId, x.Id });
                    table.CheckConstraint("CK_FinancialEvidenceLinks_Policy", "[Length]>0 AND LEN([Sha256])=64 AND [Sha256] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9A-F]%' AND (([RetentionYears] IS NULL AND [MinimumRetentionDeadlineUtc] IS NULL) OR ([RetentionYears] BETWEEN 1 AND 1000 AND [MinimumRetentionDeadlineUtc] IS NOT NULL AND [MinimumRetentionDeadlineUtc]>=[AnchorAtUtc]))");
                    table.ForeignKey(
                        name: "FK_FinancialEvidenceLinks_FinancialEvidenceSets_TenantId_EvidenceSetId",
                        columns: x => new { x.TenantId, x.EvidenceSetId },
                        principalSchema: "Accounting",
                        principalTable: "FinancialEvidenceSets",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialEvidenceLinks_PurchaseOrderDocuments_TenantId_DocumentId",
                        columns: x => new { x.TenantId, x.DocumentId },
                        principalSchema: "Purchasing",
                        principalTable: "PurchaseOrderDocuments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialEvidenceLinks_Revisions_TenantId_AttachmentId_RevisionId",
                        columns: x => new { x.TenantId, x.AttachmentId, x.RevisionId },
                        principalSchema: "Storage",
                        principalTable: "Revisions",
                        principalColumns: new[] { "TenantId", "AttachmentId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FinancialEvidenceReceipts",
                schema: "Accounting",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvidenceSetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CanonicalInput = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InputSha256 = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialEvidenceReceipts", x => new { x.TenantId, x.RequestId });
                    table.CheckConstraint("CK_FinancialEvidenceReceipts_Input", "ISJSON([CanonicalInput],OBJECT)=1 AND DATALENGTH([CanonicalInput])<=262144 AND ISJSON([ResultJson],OBJECT)=1");
                    table.ForeignKey(
                        name: "FK_FinancialEvidenceReceipts_FinancialEvidenceSets_TenantId_EvidenceSetId",
                        columns: x => new { x.TenantId, x.EvidenceSetId },
                        principalSchema: "Accounting",
                        principalTable: "FinancialEvidenceSets",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialEvidenceReceipts_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FinancialEvidenceAdditions",
                schema: "Accounting",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvidenceSetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LinkId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReplacesLinkId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialEvidenceAdditions", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_FinancialEvidenceAdditions_Reason", "LEN(TRIM([Reason]))>0 AND ([ReplacesLinkId] IS NULL OR [ReplacesLinkId]<>[LinkId])");
                    table.ForeignKey(
                        name: "FK_FinancialEvidenceAdditions_FinancialEvidenceLinks_TenantId_EvidenceSetId_LinkId",
                        columns: x => new { x.TenantId, x.EvidenceSetId, x.LinkId },
                        principalSchema: "Accounting",
                        principalTable: "FinancialEvidenceLinks",
                        principalColumns: new[] { "TenantId", "EvidenceSetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialEvidenceAdditions_FinancialEvidenceLinks_TenantId_EvidenceSetId_ReplacesLinkId",
                        columns: x => new { x.TenantId, x.EvidenceSetId, x.ReplacesLinkId },
                        principalSchema: "Accounting",
                        principalTable: "FinancialEvidenceLinks",
                        principalColumns: new[] { "TenantId", "EvidenceSetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialEvidenceAdditions_FinancialEvidenceReceipts_TenantId_RequestId",
                        columns: x => new { x.TenantId, x.RequestId },
                        principalSchema: "Accounting",
                        principalTable: "FinancialEvidenceReceipts",
                        principalColumns: new[] { "TenantId", "RequestId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialEvidenceAdditions_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialEvidenceAdditions_TenantId_ActorId",
                schema: "Accounting",
                table: "FinancialEvidenceAdditions",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialEvidenceAdditions_TenantId_EvidenceSetId_LinkId",
                schema: "Accounting",
                table: "FinancialEvidenceAdditions",
                columns: new[] { "TenantId", "EvidenceSetId", "LinkId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialEvidenceAdditions_TenantId_EvidenceSetId_ReplacesLinkId",
                schema: "Accounting",
                table: "FinancialEvidenceAdditions",
                columns: new[] { "TenantId", "EvidenceSetId", "ReplacesLinkId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialEvidenceAdditions_TenantId_RequestId",
                schema: "Accounting",
                table: "FinancialEvidenceAdditions",
                columns: new[] { "TenantId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialEvidenceLinks_TenantId_AttachmentId_RevisionId",
                schema: "Accounting",
                table: "FinancialEvidenceLinks",
                columns: new[] { "TenantId", "AttachmentId", "RevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialEvidenceLinks_TenantId_DocumentId",
                schema: "Accounting",
                table: "FinancialEvidenceLinks",
                columns: new[] { "TenantId", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialEvidenceLinks_TenantId_EvidenceSetId_DocumentId_RevisionId",
                schema: "Accounting",
                table: "FinancialEvidenceLinks",
                columns: new[] { "TenantId", "EvidenceSetId", "DocumentId", "RevisionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialEvidenceReceipts_TenantId_ActorId",
                schema: "Accounting",
                table: "FinancialEvidenceReceipts",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialEvidenceReceipts_TenantId_EvidenceSetId",
                schema: "Accounting",
                table: "FinancialEvidenceReceipts",
                columns: new[] { "TenantId", "EvidenceSetId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialEvidenceSets_TenantId_ActorId",
                schema: "Accounting",
                table: "FinancialEvidenceSets",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialEvidenceSets_TenantId_InheritedEvidenceSetId",
                schema: "Accounting",
                table: "FinancialEvidenceSets",
                columns: new[] { "TenantId", "InheritedEvidenceSetId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialEvidenceSets_TenantId_OwnerKind_OwnerId_OwnerRevisionId",
                schema: "Accounting",
                table: "FinancialEvidenceSets",
                columns: new[] { "TenantId", "OwnerKind", "OwnerId", "OwnerRevisionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialEvidenceSets_TenantId_PurchaseOrderId",
                schema: "Accounting",
                table: "FinancialEvidenceSets",
                columns: new[] { "TenantId", "PurchaseOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialEvidenceSets_TenantId_SupplierId",
                schema: "Accounting",
                table: "FinancialEvidenceSets",
                columns: new[] { "TenantId", "SupplierId" });
            FinancialEvidenceSchema.Create(migrationBuilder);
            FinancialEvidenceStorageGuards.Create(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("THROW 50020, 'Financial evidence requires forward correction or guarded recovery.', 1;");
            migrationBuilder.DropTable(
                name: "FinancialEvidenceAdditions",
                schema: "Accounting");

            migrationBuilder.DropTable(
                name: "FinancialEvidenceAttachmentStates",
                schema: "Storage");

            migrationBuilder.DropTable(
                name: "FinancialEvidenceLinks",
                schema: "Accounting");

            migrationBuilder.DropTable(
                name: "FinancialEvidenceReceipts",
                schema: "Accounting");

            migrationBuilder.DropTable(
                name: "FinancialEvidenceSets",
                schema: "Accounting");

            migrationBuilder.DropColumn(
                name: "EvidenceMutationPermission",
                schema: "Purchasing",
                table: "RecognitionSideEvents");

            migrationBuilder.DropColumn(
                name: "IndependentHeld",
                schema: "Storage",
                table: "Attachments");
        }
    }
}
