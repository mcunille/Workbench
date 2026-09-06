// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence.Migrations;

public partial class AddDeploymentQueueTelemetry : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE PROCEDURE [Operations].[ReadWorkQueueStatus]
            WITH EXECUTE AS OWNER AS
            BEGIN
                SET NOCOUNT ON;
                DECLARE @Now datetimeoffset = SYSUTCDATETIME();
                SELECT COUNT_BIG(*) AS [PendingCount],
                    CASE WHEN MIN([AvailableAtUtc]) < @Now
                        THEN DATEDIFF_BIG(second, MIN([AvailableAtUtc]), @Now)
                        ELSE CONVERT(bigint, 0) END AS [OldestPendingAgeSeconds]
                FROM [Operations].[WorkItems]
                WHERE [State] IN (0, 1);
            END;
            """);
        migrationBuilder.Sql("GRANT EXECUTE ON [Operations].[ReadWorkQueueStatus] TO [workbench_worker];");
        migrationBuilder.Sql("""
            CREATE PROCEDURE [Security].[ReadDeploymentReadiness]
            WITH EXECUTE AS OWNER AS
            BEGIN
                SET NOCOUNT ON;
                SELECT CONVERT(bit, CASE WHEN OBJECT_ID(N'[Operations].[ReadWorkQueueStatus]', N'P') IS NOT NULL
                    AND EXISTS (SELECT 1 FROM sys.database_permissions
                        WHERE [class] = 1 AND [major_id] = OBJECT_ID(N'[Operations].[ReadWorkQueueStatus]')
                            AND [grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'workbench_worker')
                            AND [permission_name] = N'EXECUTE' AND [state] IN (N'G', N'W'))
                    THEN 1 ELSE 0 END);
            END;
            """);
        migrationBuilder.Sql("GRANT EXECUTE ON [Security].[ReadDeploymentReadiness] TO [workbench_web];");
        SetCompatibleMigration(migrationBuilder, "20260905222755_AddBlobAndOperationalProviders", "20260906031109_AddDeploymentQueueTelemetry");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP PROCEDURE [Security].[ReadDeploymentReadiness];");
        migrationBuilder.Sql("DROP PROCEDURE [Operations].[ReadWorkQueueStatus];");
        SetCompatibleMigration(migrationBuilder, "20260906031109_AddDeploymentQueueTelemetry", "20260905222755_AddBlobAndOperationalProviders");
    }

    private static void SetCompatibleMigration(MigrationBuilder migrationBuilder, string previous, string current) =>
        migrationBuilder.Sql($"""
            DECLARE @Readiness nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness = REPLACE(@Readiness, N'CREATE PROCEDURE', N'ALTER PROCEDURE');
            SET @Readiness = REPLACE(@Readiness, N'{previous}', N'{current}');
            EXEC sys.sp_executesql @Readiness;
            """);
}
