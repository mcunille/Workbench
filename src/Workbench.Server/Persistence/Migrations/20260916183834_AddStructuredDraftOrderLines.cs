using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStructuredDraftOrderLines : Migration
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
                sql: "[ContentSchemaVersion] IN (1,2) AND ISJSON([ContentJson],OBJECT)=1 AND DATALENGTH([ContentJson])<=1048576");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DraftOrderRequestReceipts_Fingerprint",
                schema: "Purchasing",
                table: "DraftOrderRequestReceipts",
                sql: "[FingerprintVersion] IN (1,2,3)");
            StructuredDraftOrderSchema.Protect(migrationBuilder, "20260916183834_AddStructuredDraftOrderLines");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("THROW 50020, 'Structured draft lines require forward correction or guarded recovery.', 1;");
    }
}
