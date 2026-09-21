// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GemRockAuctions",
                schema: "Purchasing",
                table: "Suppliers",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Instagram",
                schema: "Purchasing",
                table: "Suppliers",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "X",
                schema: "Purchasing",
                table: "Suppliers",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);
            migrationBuilder.Sql(SupplierProfileSchema.SaveSupplier);
            migrationBuilder.Sql("""
                DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness'));
                IF @Readiness IS NULL OR CHARINDEX(N'20260918063409_HardenPurchaseOrderDocumentAuthority',@Readiness)=0
                    THROW 50020,'Unsupported supplier profile readiness predecessor.',1;
                SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
                SET @Readiness=REPLACE(@Readiness,N'20260918063409_HardenPurchaseOrderDocumentAuthority',N'20260921012247_AddSupplierProfiles');
                EXEC sys.sp_executesql @Readiness;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("THROW 50020, 'Supplier profiles require forward correction or guarded recovery.', 1;");
    }
}
