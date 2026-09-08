// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class FileRecoverySchema
{
    public static void Create(MigrationBuilder migration)
    {
        migration.Sql("""
            CREATE TABLE [Storage].[RecoveryFiles] (
                TenantId uniqueidentifier NOT NULL, RevisionId uniqueidentifier NOT NULL,
                ReportId uniqueidentifier NOT NULL, Generation bigint NOT NULL,
                Reason varchar(16) NOT NULL CHECK (Reason IN ('Missing','Corrupt')),
                AcceptedAtUtc datetimeoffset NOT NULL,
                CONSTRAINT PK_RecoveryFiles PRIMARY KEY (TenantId,RevisionId),
                CONSTRAINT FK_RecoveryFiles_Revision FOREIGN KEY (RevisionId) REFERENCES [Storage].[Revisions](Id)
            );
            CREATE TABLE [Security].[FileRecoveryReports] (
                ReportId uniqueidentifier NOT NULL PRIMARY KEY, Generation bigint NOT NULL,
                Fingerprint varchar(64) NOT NULL, TargetAlias nvarchar(64) NOT NULL, CompletedAtUtc datetimeoffset NOT NULL
            );
            """);
        migration.Sql("""
            ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                ADD FILTER PREDICATE [Security].[fn_tenant_access](TenantId) ON [Storage].[RecoveryFiles],
                ADD BLOCK PREDICATE [Security].[fn_tenant_access](TenantId) ON [Storage].[RecoveryFiles] AFTER INSERT,
                ADD BLOCK PREDICATE [Security].[fn_tenant_access](TenantId) ON [Storage].[RecoveryFiles] AFTER UPDATE;
            GRANT SELECT ON [Storage].[RecoveryFiles] TO [workbench_web];
            DENY INSERT, UPDATE, DELETE ON [Storage].[RecoveryFiles] TO [workbench_web];
            """);
        migration.Sql("""
            CREATE PROCEDURE [Storage].[ReadRecoveryInventory] @Inventory nvarchar(max) = NULL OUTPUT
            WITH EXECUTE AS OWNER AS
            BEGIN
                SET NOCOUNT ON;
                DECLARE @Generation bigint;
                SELECT @Generation=RestoreGeneration FROM [Security].[DatabaseSecurityState] WITH (HOLDLOCK)
                    WHERE Id=1 AND RestoreGeneration>0 AND RestoreGeneration=RestoreSanitizedGeneration;
                IF @Generation IS NULL OR EXISTS(SELECT 1 FROM [Security].[WorkbenchRestorePending] WHERE IsPending=1)
                    OR NOT EXISTS(SELECT 1 FROM [Security].[BlobRecoveryState] WHERE Id=1 AND IsPending=1)
                    THROW 50043, 'Sanitized isolated recovery is required.', 1;
                DECLARE @Rows nvarchar(max) = (SELECT TenantId, Id AS RevisionId, ProviderAlias,
                    [Length], Sha256, [State], RowVersion FROM [Storage].[Revisions] WITH (HOLDLOCK)
                    ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES);
                SET @Inventory=(SELECT DB_NAME() AS [Database], CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')) AS [Server],
                    @Generation AS Generation, JSON_QUERY(@Rows) AS [Rows] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
            END;
            """);
        migration.Sql("""
            CREATE PROCEDURE [Storage].[AcceptFileRecovery]
                @ReportId uniqueidentifier, @Generation bigint, @Fingerprint varchar(64),
                @TargetAlias nvarchar(64), @Missing nvarchar(max)
            WITH EXECUTE AS OWNER AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                IF @@TRANCOUNT=0 THROW 50043, 'Recovery requires a guarded transaction.', 1;
                IF @ReportId IS NULL OR @TargetAlias IS NULL OR LEN(@TargetAlias)=0 OR ISJSON(@Missing)<>1
                    THROW 50043, 'Invalid recovery acceptance.', 1;
                IF EXISTS (SELECT 1 FROM [Security].[FileRecoveryReports] WITH (UPDLOCK,HOLDLOCK)
                    WHERE ReportId=@ReportId AND Generation=@Generation AND Fingerprint=@Fingerprint AND TargetAlias=@TargetAlias)
                    RETURN;
                DECLARE @Inventory nvarchar(max);
                EXEC [Storage].[ReadRecoveryInventory] @Inventory OUTPUT;
                IF JSON_VALUE(@Inventory,'$.Generation')<>CONVERT(nvarchar(20),@Generation)
                    OR @Fingerprint IS NULL OR CONVERT(varchar(64),HASHBYTES('SHA2_256',@Inventory),2)<>@Fingerprint
                    THROW 50043, 'Recovery inventory changed.', 1;
                IF EXISTS (SELECT 1 FROM [Storage].[Revisions] WHERE State=0)
                    THROW 50043, 'Resolve pending revisions before completing recovery.', 1;
                DECLARE @Files TABLE (TenantId uniqueidentifier, RevisionId uniqueidentifier PRIMARY KEY, Reason varchar(16));
                INSERT @Files SELECT TenantId,RevisionId,Reason FROM OPENJSON(@Missing)
                    WITH (TenantId uniqueidentifier,RevisionId uniqueidentifier,Reason varchar(16));
                IF EXISTS(SELECT 1 FROM @Files f LEFT JOIN [Storage].[Revisions] r ON r.Id=f.RevisionId AND r.TenantId=f.TenantId
                    WHERE r.Id IS NULL OR r.State<>1 OR f.Reason IS NULL OR f.Reason NOT IN ('Missing','Corrupt'))
                    THROW 50043, 'Invalid missing-file disposition.', 1;
                DELETE FROM [Storage].[RecoveryFiles];
                INSERT [Storage].[RecoveryFiles] SELECT TenantId,RevisionId,@ReportId,@Generation,Reason,SYSUTCDATETIME() FROM @Files;
                UPDATE [Storage].[Revisions] SET ProviderAlias=@TargetAlias WHERE State=1;
                INSERT [Security].[FileRecoveryReports] VALUES(@ReportId,@Generation,@Fingerprint,@TargetAlias,SYSUTCDATETIME());
                UPDATE [Security].[BlobRecoveryState] SET IsPending=0 WHERE Id=1;
                INSERT [Security].[SystemSecurityAuditEvents] (Id,Action,Outcome,OccurredAtUtc)
                    VALUES(NEWID(),N'storage.recovery-accepted',
                        CASE WHEN EXISTS(SELECT 1 FROM @Files) THEN N'WithMissingFiles' ELSE N'Succeeded' END,SYSUTCDATETIME());
            END;
            """);
        migration.Sql("""
            CREATE PROCEDURE [Storage].[ReadFileRecoveryCompletion]
                @ReportId uniqueidentifier, @Generation bigint, @Fingerprint varchar(64), @TargetAlias nvarchar(64)
            WITH EXECUTE AS OWNER AS
            BEGIN
                SET NOCOUNT ON;
                SELECT COUNT(*) FROM [Security].[FileRecoveryReports] r
                    JOIN [Security].[DatabaseSecurityState] s ON s.Id=1 AND s.RestoreGeneration=r.Generation
                    WHERE r.ReportId=@ReportId AND r.Generation=@Generation AND r.Fingerprint=@Fingerprint AND r.TargetAlias=@TargetAlias
                        AND NOT EXISTS(SELECT 1 FROM [Security].[WorkbenchRestorePending] WHERE IsPending=1)
                        AND EXISTS(SELECT 1 FROM [Security].[BlobRecoveryState] WHERE Id=1 AND IsPending=0);
            END;
            """);
        migration.Sql("""
            CREATE PROCEDURE [Security].[ReadFileRecoveryReadiness]
            WITH EXECUTE AS OWNER AS
            BEGIN
                SET NOCOUNT ON;
                SELECT CONVERT(bit, CASE WHEN (SELECT COUNT(*) FROM sys.security_predicates
                    WHERE object_id=OBJECT_ID(N'[Security].[TenantIsolationPolicy]')
                        AND target_object_id=OBJECT_ID(N'[Storage].[RecoveryFiles]'))=3 THEN 1 ELSE 0 END);
            END;
            """);
        migration.Sql("""
            GRANT EXECUTE ON [Security].[ReadFileRecoveryReadiness] TO [workbench_web];
            GRANT EXECUTE ON [Storage].[ReadFileRecoveryCompletion] TO [workbench_storage_maintenance];
            GRANT EXECUTE ON [Storage].[ReadRecoveryInventory] TO [workbench_storage_maintenance];
            GRANT EXECUTE ON [Storage].[AcceptFileRecovery] TO [workbench_storage_maintenance];
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Readiness=REPLACE(@Readiness,N'20260907224158_AddItemArchiving',N'20260907225320_AddOnlineRecovery');
            EXEC sys.sp_executesql @Readiness;
            """);
    }
}
