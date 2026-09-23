// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    public partial class ReconcileAccountingReadiness : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness'));
                IF @Readiness IS NULL OR (CHARINDEX(N'20260921041331_MakeSupplierProfilesCustom',@Readiness)=0
                    AND CHARINDEX(N'20260921051843_AddAccountingFoundation',@Readiness)=0)
                    THROW 50020,'Unsupported accounting readiness predecessor.',1;
                SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
                SET @Readiness=REPLACE(@Readiness,N'20260921041331_MakeSupplierProfilesCustom',N'20260923043000_ReconcileAccountingReadiness');
                SET @Readiness=REPLACE(@Readiness,N'20260921051843_AddAccountingFoundation',N'20260923043000_ReconcileAccountingReadiness');
                EXEC sys.sp_executesql @Readiness;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("THROW 50020, 'Accounting readiness requires forward correction or guarded recovery.', 1;");
    }
}
