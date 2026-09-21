// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MakeSupplierProfilesCustom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "SocialProfilesJson", schema: "Purchasing", table: "Suppliers", type: "nvarchar(max)", nullable: true);
            migrationBuilder.Sql(CustomSupplierProfileSchema.SaveSupplier);
            migrationBuilder.Sql("""
                DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness'));
                IF @Readiness IS NULL OR CHARINDEX(N'20260918063409_HardenPurchaseOrderDocumentAuthority',@Readiness)=0
                    THROW 50020,'Unsupported custom supplier profile readiness predecessor.',1;
                SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
                SET @Readiness=REPLACE(@Readiness,N'20260918063409_HardenPurchaseOrderDocumentAuthority',N'20260921041331_MakeSupplierProfilesCustom');
                EXEC sys.sp_executesql @Readiness;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("THROW 50020, 'Custom supplier handles require forward correction or guarded recovery.', 1;");
    }
}
