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
            // Preserve existing reference values verbatim; receipt fingerprints and saved versions stay immutable.
            // Only migrated supplier rows receive a new concurrency version, so stale editors cannot overwrite conversion.
            migrationBuilder.Sql("""
                UPDATE s SET SocialProfilesJson=(
                  SELECT p.label,p.handle FROM (VALUES (1,N'Instagram',s.Instagram),(2,N'X',s.X),(3,N'GemRockAuctions',s.GemRockAuctions)) p(position,label,handle)
                  WHERE p.handle IS NOT NULL ORDER BY p.position FOR JSON PATH)
                FROM Purchasing.Suppliers s
                WHERE s.Instagram IS NOT NULL OR s.X IS NOT NULL OR s.GemRockAuctions IS NOT NULL;
                """);
            migrationBuilder.DropColumn(name: "Instagram", schema: "Purchasing", table: "Suppliers");
            migrationBuilder.DropColumn(name: "X", schema: "Purchasing", table: "Suppliers");
            migrationBuilder.DropColumn(name: "GemRockAuctions", schema: "Purchasing", table: "Suppliers");
            migrationBuilder.Sql(CustomSupplierProfileSchema.SaveSupplier);
            migrationBuilder.Sql("""
                DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness'));
                IF @Readiness IS NULL OR CHARINDEX(N'20260921012247_AddSupplierProfiles',@Readiness)=0
                    THROW 50020,'Unsupported custom supplier profile readiness predecessor.',1;
                SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
                SET @Readiness=REPLACE(@Readiness,N'20260921012247_AddSupplierProfiles',N'20260921041331_MakeSupplierProfilesCustom');
                EXEC sys.sp_executesql @Readiness;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("THROW 50020, 'Custom supplier handles require forward correction or guarded recovery.', 1;");
    }
}
