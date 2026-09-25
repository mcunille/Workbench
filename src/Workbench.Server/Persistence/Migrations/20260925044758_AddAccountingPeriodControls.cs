// Copyright (c) 2026 The White Stag Collection.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountingPeriodControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Periods",
                schema: "Accounting",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    FiscalYearStart = table.Column<DateOnly>(type: "date", nullable: false),
                    ConfigurationVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    Scale = table.Column<int>(type: "int", nullable: false),
                    FiscalStartMonth = table.Column<int>(type: "int", nullable: false),
                    StartApproach = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    AccountingStartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Periods", x => new { x.TenantId, x.PeriodStart });
                    table.CheckConstraint("CK_Periods_Calendar", "DAY([PeriodStart])=1 AND [PeriodEnd]=EOMONTH([PeriodStart]) AND DAY([FiscalYearStart])=1 AND MONTH([FiscalYearStart])=[FiscalStartMonth] AND YEAR([FiscalYearStart])=YEAR([PeriodStart])-CASE WHEN MONTH([PeriodStart])<[FiscalStartMonth] THEN 1 ELSE 0 END AND [AccountingStartDate]<=[PeriodEnd] AND [Scale] BETWEEN 0 AND 4");
                    table.ForeignKey(
                        name: "FK_Periods_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "Tenancy",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PeriodClosures",
                schema: "Accounting",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    EvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EvidenceSha256 = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeriodClosures", x => new { x.TenantId, x.PeriodStart });
                    table.UniqueConstraint("AK_PeriodClosures_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.UniqueConstraint("AK_PeriodClosures_TenantId_PeriodStart_Id", x => new { x.TenantId, x.PeriodStart, x.Id });
                    table.CheckConstraint("CK_PeriodClosures_Evidence", "LEN(TRIM(NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+NCHAR(8239)+NCHAR(8287)+NCHAR(12288) FROM [Reason]))>0 AND DATALENGTH([Reason])<=4000 AND ISJSON([EvidenceJson],OBJECT)=1 AND DATALENGTH([EvidenceJson])<=262144 AND DATALENGTH([EvidenceSha256])=32");
                    table.ForeignKey(
                        name: "FK_PeriodClosures_Periods_TenantId_PeriodStart",
                        columns: x => new { x.TenantId, x.PeriodStart },
                        principalSchema: "Accounting",
                        principalTable: "Periods",
                        principalColumns: new[] { "TenantId", "PeriodStart" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PeriodClosures_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PeriodCloseReceipts",
                schema: "Accounting",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommandKind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CommandVersion = table.Column<int>(type: "int", nullable: false),
                    CanonicalInput = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InputSha256 = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    ClosureId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeriodCloseReceipts", x => new { x.TenantId, x.RequestId });
                    table.CheckConstraint("CK_PeriodCloseReceipts_Input", "[RequestId]<>'00000000-0000-0000-0000-000000000000' AND [CommandKind]='Period.Close' AND [CommandVersion]=1 AND ISJSON([CanonicalInput],OBJECT)=1 AND DATALENGTH([CanonicalInput])<=262144 AND DATALENGTH([InputSha256])=32");
                    table.ForeignKey(
                        name: "FK_PeriodCloseReceipts_PeriodClosures_TenantId_PeriodStart_ClosureId",
                        columns: x => new { x.TenantId, x.PeriodStart, x.ClosureId },
                        principalSchema: "Accounting",
                        principalTable: "PeriodClosures",
                        principalColumns: new[] { "TenantId", "PeriodStart", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PeriodCloseReceipts_Periods_TenantId_PeriodStart",
                        columns: x => new { x.TenantId, x.PeriodStart },
                        principalSchema: "Accounting",
                        principalTable: "Periods",
                        principalColumns: new[] { "TenantId", "PeriodStart" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PeriodCloseReceipts_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PeriodCloseReceipts_TenantId_ActorId",
                schema: "Accounting",
                table: "PeriodCloseReceipts",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_PeriodCloseReceipts_TenantId_PeriodStart_ClosureId",
                schema: "Accounting",
                table: "PeriodCloseReceipts",
                columns: new[] { "TenantId", "PeriodStart", "ClosureId" });

            migrationBuilder.CreateIndex(
                name: "IX_PeriodClosures_TenantId_ActorId",
                schema: "Accounting",
                table: "PeriodClosures",
                columns: new[] { "TenantId", "ActorId" });
            migrationBuilder.CreateTable(
                name: "CorrectionGroups",
                schema: "Accounting",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalSourceEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalJournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReversalSourceEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReversalJournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReversalRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReplacementSourceEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReplacementJournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReplacementRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReplacementSourceRevision = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    EvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EvidenceSha256 = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CorrectionGroups", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_CorrectionGroups_Evidence", "LEN(TRIM([Reason]))>0 AND DATALENGTH([Reason])<=4000 AND ISJSON([EvidenceJson],OBJECT)=1 AND DATALENGTH([EvidenceJson])<=262144 AND DATALENGTH([EvidenceSha256])=32");
                    table.CheckConstraint("CK_CorrectionGroups_Roles", "[OriginalJournalId]<>[ReversalJournalId] AND (([ReplacementJournalId] IS NULL AND [ReplacementSourceEventId] IS NULL AND [ReplacementRequestId] IS NULL AND [ReplacementSourceRevision] IS NULL) OR ([ReplacementJournalId] IS NOT NULL AND [ReplacementSourceEventId] IS NOT NULL AND [ReplacementRequestId] IS NOT NULL AND [ReplacementSourceRevision] IS NOT NULL AND [ReplacementJournalId]<>[OriginalJournalId] AND [ReplacementJournalId]<>[ReversalJournalId]))");
                    table.ForeignKey(
                        name: "FK_CorrectionGroups_JournalEntries_TenantId_OriginalJournalId",
                        columns: x => new { x.TenantId, x.OriginalJournalId },
                        principalSchema: "Accounting",
                        principalTable: "JournalEntries",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CorrectionGroups_JournalEntries_TenantId_ReplacementJournalId",
                        columns: x => new { x.TenantId, x.ReplacementJournalId },
                        principalSchema: "Accounting",
                        principalTable: "JournalEntries",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CorrectionGroups_JournalEntries_TenantId_ReversalJournalId",
                        columns: x => new { x.TenantId, x.ReversalJournalId },
                        principalSchema: "Accounting",
                        principalTable: "JournalEntries",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CorrectionGroups_PostingReceipts_TenantId_ReplacementRequestId",
                        columns: x => new { x.TenantId, x.ReplacementRequestId },
                        principalSchema: "Accounting",
                        principalTable: "PostingReceipts",
                        principalColumns: new[] { "TenantId", "RequestId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CorrectionGroups_PostingReceipts_TenantId_ReversalRequestId",
                        columns: x => new { x.TenantId, x.ReversalRequestId },
                        principalSchema: "Accounting",
                        principalTable: "PostingReceipts",
                        principalColumns: new[] { "TenantId", "RequestId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CorrectionGroups_SourceEvents_TenantId_OriginalSourceEventId",
                        columns: x => new { x.TenantId, x.OriginalSourceEventId },
                        principalSchema: "Accounting",
                        principalTable: "SourceEvents",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CorrectionGroups_SourceEvents_TenantId_ReplacementSourceEventId",
                        columns: x => new { x.TenantId, x.ReplacementSourceEventId },
                        principalSchema: "Accounting",
                        principalTable: "SourceEvents",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CorrectionGroups_SourceEvents_TenantId_ReversalSourceEventId",
                        columns: x => new { x.TenantId, x.ReversalSourceEventId },
                        principalSchema: "Accounting",
                        principalTable: "SourceEvents",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CorrectionGroups_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CorrectionReceipts",
                schema: "Accounting",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceCommandKind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceCommandVersion = table.Column<int>(type: "int", nullable: false),
                    CanonicalInput = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InputSha256 = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    CorrectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CorrectionReceipts", x => new { x.TenantId, x.RequestId });
                    table.CheckConstraint("CK_CorrectionReceipts_Input", "[RequestId]<>'00000000-0000-0000-0000-000000000000' AND [SourceCommandVersion]>0 AND ISJSON([CanonicalInput],OBJECT)=1 AND DATALENGTH([CanonicalInput])<=262144 AND DATALENGTH([InputSha256])=32");
                    table.ForeignKey(
                        name: "FK_CorrectionReceipts_CorrectionGroups_TenantId_CorrectionId",
                        columns: x => new { x.TenantId, x.CorrectionId },
                        principalSchema: "Accounting",
                        principalTable: "CorrectionGroups",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CorrectionReceipts_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionGroups_TenantId_ActorId",
                schema: "Accounting",
                table: "CorrectionGroups",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionGroups_TenantId_OriginalJournalId",
                schema: "Accounting",
                table: "CorrectionGroups",
                columns: new[] { "TenantId", "OriginalJournalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionGroups_TenantId_OriginalSourceEventId",
                schema: "Accounting",
                table: "CorrectionGroups",
                columns: new[] { "TenantId", "OriginalSourceEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionGroups_TenantId_ReplacementJournalId",
                schema: "Accounting",
                table: "CorrectionGroups",
                columns: new[] { "TenantId", "ReplacementJournalId" },
                unique: true,
                filter: "[ReplacementJournalId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionGroups_TenantId_ReplacementRequestId",
                schema: "Accounting",
                table: "CorrectionGroups",
                columns: new[] { "TenantId", "ReplacementRequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionGroups_TenantId_ReplacementSourceEventId",
                schema: "Accounting",
                table: "CorrectionGroups",
                columns: new[] { "TenantId", "ReplacementSourceEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionGroups_TenantId_ReversalJournalId",
                schema: "Accounting",
                table: "CorrectionGroups",
                columns: new[] { "TenantId", "ReversalJournalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionGroups_TenantId_ReversalRequestId",
                schema: "Accounting",
                table: "CorrectionGroups",
                columns: new[] { "TenantId", "ReversalRequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionGroups_TenantId_ReversalSourceEventId",
                schema: "Accounting",
                table: "CorrectionGroups",
                columns: new[] { "TenantId", "ReversalSourceEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionReceipts_TenantId_ActorId",
                schema: "Accounting",
                table: "CorrectionReceipts",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_CorrectionReceipts_TenantId_CorrectionId",
                schema: "Accounting",
                table: "CorrectionReceipts",
                columns: new[] { "TenantId", "CorrectionId" });
            AccountingPeriodSchema.Up(migrationBuilder);
            JournalCorrectionSchema.Up(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("THROW 50020, 'Accounting period history requires a reviewed forward correction or protected restore.', 1;");
        }
    }
}
