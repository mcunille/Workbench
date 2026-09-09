// Copyright (c) 2026 The White Stag Collection.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAcquisitionContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Acquisitions",
                schema: "Inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Year = table.Column<int>(type: "int", nullable: true),
                    Month = table.Column<int>(type: "int", nullable: true),
                    Day = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreationRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Acquisitions", x => x.Id);
                    table.UniqueConstraint("AK_Acquisitions_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_Acquisitions_Date", "([Year] IS NULL AND [Month] IS NULL AND [Day] IS NULL) OR ([Year] IS NOT NULL AND [Year] BETWEEN 1 AND 9999 AND (([Month] IS NULL AND [Day] IS NULL) OR ([Month] IS NOT NULL AND [Month] BETWEEN 1 AND 12 AND ([Day] IS NULL OR ([Day] BETWEEN 1 AND 31 AND TRY_CONVERT(date, CONCAT(RIGHT('0000'+CONVERT(varchar(4),[Year]),4), RIGHT('00'+CONVERT(varchar(2),[Month]),2), RIGHT('00'+CONVERT(varchar(2),[Day]),2)),112) IS NOT NULL)))))");
                    table.CheckConstraint("CK_Acquisitions_Method", "[Method] COLLATE Latin1_General_100_BIN2 IN ('Purchase','Gift','Inheritance','Trade','Other','Unknown') AND DATALENGTH([Method]) = DATALENGTH(RTRIM([Method]))");
                    table.CheckConstraint("CK_Acquisitions_Request", "[CreationRequestId] <> '00000000-0000-0000-0000-000000000000'");
                    table.ForeignKey(
                        name: "FK_Acquisitions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "Tenancy",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AcquisitionCreationRecords",
                schema: "Inventory",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreationRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AcquisitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Year = table.Column<int>(type: "int", nullable: true),
                    Month = table.Column<int>(type: "int", nullable: true),
                    Day = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcquisitionCreationRecords", x => new { x.TenantId, x.CreationRequestId });
                    table.CheckConstraint("CK_AcquisitionCreationRecords_Date", "([Year] IS NULL AND [Month] IS NULL AND [Day] IS NULL) OR ([Year] IS NOT NULL AND [Year] BETWEEN 1 AND 9999 AND (([Month] IS NULL AND [Day] IS NULL) OR ([Month] IS NOT NULL AND [Month] BETWEEN 1 AND 12 AND ([Day] IS NULL OR ([Day] BETWEEN 1 AND 31 AND TRY_CONVERT(date, CONCAT(RIGHT('0000'+CONVERT(varchar(4),[Year]),4), RIGHT('00'+CONVERT(varchar(2),[Month]),2), RIGHT('00'+CONVERT(varchar(2),[Day]),2)),112) IS NOT NULL)))))");
                    table.CheckConstraint("CK_AcquisitionCreationRecords_Method", "[Method] COLLATE Latin1_General_100_BIN2 IN ('Purchase','Gift','Inheritance','Trade','Other','Unknown') AND DATALENGTH([Method]) = DATALENGTH(RTRIM([Method]))");
                    table.ForeignKey(
                        name: "FK_AcquisitionCreationRecords_Acquisitions_TenantId_AcquisitionId",
                        columns: x => new { x.TenantId, x.AcquisitionId },
                        principalSchema: "Inventory",
                        principalTable: "Acquisitions",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AcquisitionCreationRecords_Items_TenantId_ItemId",
                        columns: x => new { x.TenantId, x.ItemId },
                        principalSchema: "Inventory",
                        principalTable: "Items",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AcquisitionItems",
                schema: "Inventory",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AcquisitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcquisitionItems", x => new { x.TenantId, x.ItemId });
                    table.ForeignKey(
                        name: "FK_AcquisitionItems_Acquisitions_TenantId_AcquisitionId",
                        columns: x => new { x.TenantId, x.AcquisitionId },
                        principalSchema: "Inventory",
                        principalTable: "Acquisitions",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AcquisitionItems_Items_TenantId_ItemId",
                        columns: x => new { x.TenantId, x.ItemId },
                        principalSchema: "Inventory",
                        principalTable: "Items",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AcquisitionCreationRecords_TenantId_AcquisitionId",
                schema: "Inventory",
                table: "AcquisitionCreationRecords",
                columns: new[] { "TenantId", "AcquisitionId" });

            migrationBuilder.CreateIndex(
                name: "IX_AcquisitionCreationRecords_TenantId_ItemId",
                schema: "Inventory",
                table: "AcquisitionCreationRecords",
                columns: new[] { "TenantId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_AcquisitionItems_TenantId_AcquisitionId",
                schema: "Inventory",
                table: "AcquisitionItems",
                columns: new[] { "TenantId", "AcquisitionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Acquisitions_TenantId_CreationRequestId",
                schema: "Inventory",
                table: "Acquisitions",
                columns: new[] { "TenantId", "CreationRequestId" },
                unique: true);
            AddCommands(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Migrators remain subject to RLS; a filtered empty view cannot authorize deletion.
            migrationBuilder.Sql("THROW 50020, 'Acquisition context and replay evidence require forward correction or paired recovery.', 1;");
        }
    }
}
