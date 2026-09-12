// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDraftOrderDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DraftOrderRequestReceipts_Operation",
                schema: "Purchasing",
                table: "DraftOrderRequestReceipts");

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "Purchasing",
                table: "DraftOrders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_DraftOrders_DeletedContent",
                schema: "Purchasing",
                table: "DraftOrders",
                sql: "[IsDeleted]=0 OR ([Title] IS NULL AND [SupplierName] IS NULL AND [Currency] IS NULL AND [Notes] IS NULL AND CONVERT(varbinary(max),[ContentJson])=CONVERT(varbinary(max),N'{\"sourceLinks\":[],\"entries\":[]}'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DraftOrderRequestReceipts_Operation",
                schema: "Purchasing",
                table: "DraftOrderRequestReceipts",
                sql: "([Operation] COLLATE Latin1_General_100_BIN2='Create' AND [ExpectedRowVersion] IS NULL) OR ([Operation] COLLATE Latin1_General_100_BIN2 IN ('Update','Delete') AND [ExpectedRowVersion] IS NOT NULL)");
            DraftOrderDeletionSchema.Apply(migrationBuilder, "20260912045432_AddDraftOrderDeletion");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("THROW 50020, 'Draft deletion and request receipts require forward correction or guarded recovery.', 1;");
    }
}
