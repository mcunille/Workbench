// Copyright (c) 2026 The White Stag Collection.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAtomicJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourceEvents",
                schema: "Accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceKind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRevision = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventKind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RuleVersion = table.Column<int>(type: "int", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SnapshotSha256 = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceEvents", x => x.Id);
                    table.UniqueConstraint("AK_SourceEvents_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SourceEvents_Snapshot", "ISJSON([SnapshotJson], OBJECT) = 1 AND DATALENGTH([SnapshotJson]) <= 262144 AND DATALENGTH([SnapshotSha256]) = 32");
                    table.ForeignKey(
                        name: "FK_SourceEvents_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "Tenancy",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceEvents_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JournalEntries",
                schema: "Accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigurationVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    Scale = table.Column<int>(type: "int", nullable: false),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DebitTotal = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    CreditTotal = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntries", x => x.Id);
                    table.UniqueConstraint("AK_JournalEntries_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_JournalEntries_Balance", "[DebitTotal] = [CreditTotal] AND [DebitTotal] > 0 AND [Scale] BETWEEN 0 AND 4");
                    table.ForeignKey(
                        name: "FK_JournalEntries_SourceEvents_TenantId_SourceEventId",
                        columns: x => new { x.TenantId, x.SourceEventId },
                        principalSchema: "Accounting",
                        principalTable: "SourceEvents",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalEntries_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JournalLines",
                schema: "Accounting",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    AccountName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    AccountType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AccountPurpose = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Debit = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false),
                    Credit = table.Column<decimal>(type: "decimal(28,4)", precision: 28, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalLines", x => new { x.TenantId, x.JournalId, x.Ordinal });
                    table.CheckConstraint("CK_JournalLines_OneSide", "[Ordinal] BETWEEN 1 AND 1000 AND (([Debit] > 0 AND [Credit] = 0) OR ([Credit] > 0 AND [Debit] = 0))");
                    table.ForeignKey(
                        name: "FK_JournalLines_Accounts_TenantId_AccountId",
                        columns: x => new { x.TenantId, x.AccountId },
                        principalSchema: "Accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalLines_JournalEntries_TenantId_JournalId",
                        columns: x => new { x.TenantId, x.JournalId },
                        principalSchema: "Accounting",
                        principalTable: "JournalEntries",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PolicyFreezes",
                schema: "Accounting",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigurationVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    Scale = table.Column<int>(type: "int", nullable: false),
                    FiscalStartMonth = table.Column<int>(type: "int", nullable: false),
                    StartApproach = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    FirstJournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyFreezes", x => x.TenantId);
                    table.ForeignKey(
                        name: "FK_PolicyFreezes_JournalEntries_TenantId_FirstJournalId",
                        columns: x => new { x.TenantId, x.FirstJournalId },
                        principalSchema: "Accounting",
                        principalTable: "JournalEntries",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PolicyFreezes_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "Tenancy",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PostingReceipts",
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
                    SourceEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostingReceipts", x => new { x.TenantId, x.RequestId });
                    table.CheckConstraint("CK_PostingReceipts_Input", "ISJSON([CanonicalInput], OBJECT) = 1 AND DATALENGTH([CanonicalInput]) <= 262144 AND DATALENGTH([InputSha256]) = 32");
                    table.ForeignKey(
                        name: "FK_PostingReceipts_JournalEntries_TenantId_JournalId",
                        columns: x => new { x.TenantId, x.JournalId },
                        principalSchema: "Accounting",
                        principalTable: "JournalEntries",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PostingReceipts_SourceEvents_TenantId_SourceEventId",
                        columns: x => new { x.TenantId, x.SourceEventId },
                        principalSchema: "Accounting",
                        principalTable: "SourceEvents",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PostingReceipts_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_TenantId_ActorId",
                schema: "Accounting",
                table: "JournalEntries",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_TenantId_PostingDate_Sequence",
                schema: "Accounting",
                table: "JournalEntries",
                columns: new[] { "TenantId", "PostingDate", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_TenantId_Sequence",
                schema: "Accounting",
                table: "JournalEntries",
                columns: new[] { "TenantId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_TenantId_SourceEventId",
                schema: "Accounting",
                table: "JournalEntries",
                columns: new[] { "TenantId", "SourceEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalLines_TenantId_AccountId_JournalId_Ordinal",
                schema: "Accounting",
                table: "JournalLines",
                columns: new[] { "TenantId", "AccountId", "JournalId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyFreezes_TenantId_FirstJournalId",
                schema: "Accounting",
                table: "PolicyFreezes",
                columns: new[] { "TenantId", "FirstJournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_PostingReceipts_TenantId_ActorId",
                schema: "Accounting",
                table: "PostingReceipts",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_PostingReceipts_TenantId_JournalId",
                schema: "Accounting",
                table: "PostingReceipts",
                columns: new[] { "TenantId", "JournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_PostingReceipts_TenantId_SourceEventId",
                schema: "Accounting",
                table: "PostingReceipts",
                columns: new[] { "TenantId", "SourceEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceEvents_TenantId_ActorId",
                schema: "Accounting",
                table: "SourceEvents",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceEvents_TenantId_SourceKind_SourceId_SourceRevision_EventKind",
                schema: "Accounting",
                table: "SourceEvents",
                columns: new[] { "TenantId", "SourceKind", "SourceId", "SourceRevision", "EventKind" },
                unique: true);
            JournalSchema.Up(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("THROW 50020, 'Journal evidence requires a reviewed forward correction or protected restore.', 1;");
        }
    }
}
