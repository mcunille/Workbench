// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierBasedDraftPricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DraftOrders_Content",
                schema: "Purchasing",
                table: "DraftOrders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DraftOrderRequestReceipts_Fingerprint",
                schema: "Purchasing",
                table: "DraftOrderRequestReceipts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DraftOrders_Content",
                schema: "Purchasing",
                table: "DraftOrders",
                sql: "[ContentSchemaVersion] IN (1,2,3) AND ISJSON([ContentJson],OBJECT)=1 AND DATALENGTH([ContentJson])<=1048576");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DraftOrderRequestReceipts_Fingerprint",
                schema: "Purchasing",
                table: "DraftOrderRequestReceipts",
                sql: "[FingerprintVersion] IN (1,2,3,4)");
            SupplierPricingDraftOrderSchema.Protect(migrationBuilder, "20260917010000_AddSupplierBasedDraftPricing");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("THROW 50020, 'Structured draft lines require forward correction or guarded recovery.', 1;");
    }
}
