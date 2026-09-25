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
            AccountingPeriodSchema.Up(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("THROW 50020, 'Accounting period history requires a reviewed forward correction or protected restore.', 1;");
        }
    }
}
