// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence.Migrations;

public partial class AddProviderRetryDelay : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER PROCEDURE [Operations].[RetryWork]
                @Id uniqueidentifier, @Owner uniqueidentifier, @Generation bigint, @Transient bit,
                @RetryAfterSeconds int = 0
            AS
            BEGIN
                SET NOCOUNT ON;
                DECLARE @Now datetimeoffset = SYSUTCDATETIME();
                DECLARE @Jitter int = ABS(CHECKSUM(NEWID()) % 5);
                IF @RetryAfterSeconds IS NULL OR @RetryAfterSeconds < 0 SET @RetryAfterSeconds = 0;
                IF @RetryAfterSeconds > 3600 SET @RetryAfterSeconds = 3600;
                UPDATE [Operations].[WorkItems]
                SET [State] = CASE WHEN @Transient = 1 AND [Attempts] < 5 THEN 0 ELSE 3 END,
                    [ProtectedPayload] = CASE WHEN @Transient = 1 AND [Attempts] < 5 THEN [ProtectedPayload] ELSE NULL END,
                    [Outcome] = CASE WHEN @Transient = 1 THEN N'ProviderUnavailable' ELSE N'Rejected' END,
                    [AvailableAtUtc] = DATEADD(second,
                        CASE WHEN @Transient = 1 AND @RetryAfterSeconds > CONVERT(int, POWER(2, [Attempts])) + @Jitter
                            THEN @RetryAfterSeconds
                            ELSE CONVERT(int, POWER(2, [Attempts])) + @Jitter END, @Now),
                    [LeaseOwner] = NULL, [LeaseExpiresAtUtc] = NULL
                WHERE [Id] = @Id AND [LeaseOwner] = @Owner AND [Generation] = @Generation
                    AND [State] = 1 AND [LeaseExpiresAtUtc] > @Now;
                SELECT @@ROWCOUNT;
            END;
            """);
        migrationBuilder.Sql("""
            CREATE PROCEDURE [Security].[ReadProviderRetryReadiness]
            WITH EXECUTE AS OWNER AS
            BEGIN
                SET NOCOUNT ON;
                SELECT CONVERT(bit, CASE WHEN EXISTS (
                    SELECT 1 FROM sys.parameters
                    WHERE [object_id] = OBJECT_ID(N'[Operations].[RetryWork]')
                        AND [name] = N'@RetryAfterSeconds' AND [system_type_id] = TYPE_ID(N'int')
                ) AND EXISTS (
                    SELECT 1 FROM sys.database_permissions
                    WHERE [class] = 1 AND [major_id] = OBJECT_ID(N'[Operations].[RetryWork]')
                        AND [grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'workbench_worker')
                        AND [permission_name] = N'EXECUTE' AND [state] IN (N'G', N'W')
                ) THEN 1 ELSE 0 END);
            END;
            """);
        migrationBuilder.Sql("GRANT EXECUTE ON [Security].[ReadProviderRetryReadiness] TO [workbench_web];");
        migrationBuilder.Sql("""
            DECLARE @Readiness nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness = REPLACE(@Readiness, N'CREATE PROCEDURE', N'ALTER PROCEDURE');
            SET @Readiness = REPLACE(@Readiness, N'20260906092000_DeferInvitationIdentityClaim', N'20260907054000_AddProviderRetryDelay');
            EXEC sys.sp_executesql @Readiness;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("THROW 50020, 'Provider retry scheduling cannot be rolled back; restore a verified backup instead.', 1;");
}
