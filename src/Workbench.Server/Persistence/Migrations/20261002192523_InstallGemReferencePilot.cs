// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InstallGemReferencePilot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            using var stream = typeof(InstallGemReferencePilot).Assembly.GetManifestResourceStream("Workbench.GemReferencePilot.sql")
                ?? throw new InvalidOperationException("The frozen gem reference pilot is missing from the migration assembly.");
            using var reader = new StreamReader(stream);
            migrationBuilder.Sql(reader.ReadToEnd());
            migrationBuilder.Sql("""
                DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness'));
                IF @Definition IS NULL OR CHARINDEX(N'20261001072507_AddServiceAdminIdentity',@Definition)=0
                    THROW 50020,'Unsupported pilot catalog readiness predecessor.',1;
                SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
                SET @Definition=REPLACE(@Definition,N'20261001072507_AddServiceAdminIdentity',N'20261002192523_InstallGemReferencePilot');
                EXEC sys.sp_executesql @Definition;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("THROW 50020, 'Pilot catalog rollback is blocked to preserve published identities and provenance.', 1;");
        }
    }
}
