// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class GemReferenceSchema
{
    internal static void Up(MigrationBuilder builder, string migrationId)
    {
        foreach (var table in new[] { "Entries", "Aliases", "SourceAssertions", "LocalityAssertions" })
            builder.Sql($"""
                GRANT SELECT ON [Gemology].[{table}] TO [workbench_web];
                DENY INSERT, UPDATE, DELETE ON [Gemology].[{table}] TO [workbench_web];
                DENY INSERT, UPDATE, DELETE ON [Gemology].[{table}] TO [workbench_worker];
                """);
        builder.Sql($"""
            DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness'));
            IF @Definition IS NULL OR CHARINDEX(N'20260928071548_AddSupplierOpenItems',@Definition)=0
                THROW 50020,'Unsupported shared gem reference readiness predecessor.',1;
            SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Definition=REPLACE(@Definition,N'20260928071548_AddSupplierOpenItems',N'{migrationId}');
            EXEC sys.sp_executesql @Definition;
            """);
    }
}
