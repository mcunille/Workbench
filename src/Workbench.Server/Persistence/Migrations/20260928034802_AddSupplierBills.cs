using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierBills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupplierBillReceipts",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BillId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CanonicalInput = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierBillReceipts", x => new { x.TenantId, x.Operation, x.RequestId });
                    table.CheckConstraint("CK_SupplierBillReceipts_Json", "ISJSON([CanonicalInput],OBJECT)=1 AND DATALENGTH([CanonicalInput])<=262144 AND ISJSON([ResultJson],OBJECT)=1 AND [RequestId]<>'00000000-0000-0000-0000-000000000000'");
                    table.ForeignKey(
                        name: "FK_SupplierBillReceipts_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierBillRevisions",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BillId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    PurchaseOrderRevision = table.Column<int>(type: "int", nullable: false),
                    SupplierName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NormalizedReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true, collation: "Latin1_General_100_BIN2"),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierBillRevisions", x => new { x.TenantId, x.Id });
                    table.UniqueConstraint("AK_SupplierBillRevisions_TenantId_BillId_Id", x => new { x.TenantId, x.BillId, x.Id });
                    table.CheckConstraint("CK_SupplierBillRevisions_Payload", "[Sequence]>0 AND ISJSON([Payload],OBJECT)=1 AND DATALENGTH([Payload])<=262144");
                    table.ForeignKey(
                        name: "FK_SupplierBillRevisions_Users_TenantId_ActorId",
                        columns: x => new { x.TenantId, x.ActorId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierBills",
                schema: "Purchasing",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    CurrentRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierBills", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SupplierBills_State", "[State] COLLATE Latin1_General_100_BIN2 IN ('Draft','Reviewed','Posted','Abandoned')");
                    table.ForeignKey(
                        name: "FK_SupplierBills_DraftOrders_TenantId_PurchaseOrderId",
                        columns: x => new { x.TenantId, x.PurchaseOrderId },
                        principalSchema: "Purchasing",
                        principalTable: "DraftOrders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierBills_SupplierBillRevisions_TenantId_Id_CurrentRevisionId",
                        columns: x => new { x.TenantId, x.Id, x.CurrentRevisionId },
                        principalSchema: "Purchasing",
                        principalTable: "SupplierBillRevisions",
                        principalColumns: new[] { "TenantId", "BillId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierBills_Suppliers_TenantId_SupplierId",
                        columns: x => new { x.TenantId, x.SupplierId },
                        principalSchema: "Purchasing",
                        principalTable: "Suppliers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBillReceipts_TenantId_ActorId",
                schema: "Purchasing",
                table: "SupplierBillReceipts",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBillReceipts_TenantId_BillId",
                schema: "Purchasing",
                table: "SupplierBillReceipts",
                columns: new[] { "TenantId", "BillId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBillRevisions_TenantId_ActorId",
                schema: "Purchasing",
                table: "SupplierBillRevisions",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBillRevisions_TenantId_BillId_Sequence",
                schema: "Purchasing",
                table: "SupplierBillRevisions",
                columns: new[] { "TenantId", "BillId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBillRevisions_TenantId_NormalizedReference",
                schema: "Purchasing",
                table: "SupplierBillRevisions",
                columns: new[] { "TenantId", "NormalizedReference" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBills_TenantId_Id_CurrentRevisionId",
                schema: "Purchasing",
                table: "SupplierBills",
                columns: new[] { "TenantId", "Id", "CurrentRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBills_TenantId_PurchaseOrderId_Id",
                schema: "Purchasing",
                table: "SupplierBills",
                columns: new[] { "TenantId", "PurchaseOrderId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBills_TenantId_SupplierId",
                schema: "Purchasing",
                table: "SupplierBills",
                columns: new[] { "TenantId", "SupplierId" });

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierBillReceipts_SupplierBills_TenantId_BillId",
                schema: "Purchasing",
                table: "SupplierBillReceipts",
                columns: new[] { "TenantId", "BillId" },
                principalSchema: "Purchasing",
                principalTable: "SupplierBills",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierBillRevisions_SupplierBills_TenantId_BillId",
                schema: "Purchasing",
                table: "SupplierBillRevisions",
                columns: new[] { "TenantId", "BillId" },
                principalSchema: "Purchasing",
                principalTable: "SupplierBills",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
            SupplierBillSchema.Up(migrationBuilder, "20260928034802_AddSupplierBills");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("THROW 50020, 'Supplier bill history requires forward correction or guarded recovery.', 1;");
        }
    }
}
