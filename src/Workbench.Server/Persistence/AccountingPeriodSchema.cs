// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class AccountingPeriodSchema
{
    internal static void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[] { "Periods", "PeriodClosures", "PeriodCloseReceipts" })
            migrationBuilder.Sql($"""
                ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                    ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Accounting].[{table}],
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Accounting].[{table}] AFTER INSERT,
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Accounting].[{table}] AFTER UPDATE;
                GRANT SELECT ON [Accounting].[{table}] TO [workbench_web];
                DENY INSERT,UPDATE,DELETE ON [Accounting].[{table}] TO [workbench_web];
                """);
        migrationBuilder.Sql(Backfill);
        migrationBuilder.Sql(EnsureOpenPeriod);
        migrationBuilder.Sql(ClosePeriod);
        migrationBuilder.Sql(AccountingPeriodPostJournal.Sql);
        migrationBuilder.Sql(AlterSave);
        migrationBuilder.Sql("""
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            IF @Readiness IS NULL OR CHARINDEX(N'20260923010000_AddAtomicJournal',@Readiness)=0
              THROW 50020,'Unsupported period readiness predecessor.',1;
            SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Readiness=REPLACE(@Readiness,N'20260923010000_AddAtomicJournal',N'20260925044758_AddAccountingPeriodControls');
            EXEC sys.sp_executesql @Readiness;
            """);
    }

    private const string Backfill = """
        IF EXISTS(SELECT 1 FROM Accounting.JournalEntries j
          JOIN Accounting.PolicyFreezes f ON f.TenantId=j.TenantId
          WHERE YEAR(j.PostingDate)=1 AND MONTH(j.PostingDate)<f.FiscalStartMonth)
            THROW 50020,'Historical posting has an unrepresentable fiscal year.',1;
        INSERT Accounting.Periods(TenantId,PeriodStart,PeriodEnd,FiscalYearStart,ConfigurationVersion,
            Currency,Scale,FiscalStartMonth,StartApproach,AccountingStartDate,CreatedAtUtc)
          SELECT f.TenantId,m.PeriodStart,EOMONTH(m.PeriodStart),
            DATEFROMPARTS(YEAR(m.PeriodStart)-CASE WHEN MONTH(m.PeriodStart)<f.FiscalStartMonth THEN 1 ELSE 0 END,
                f.FiscalStartMonth,1),f.ConfigurationVersion,f.Currency,f.Scale,f.FiscalStartMonth,
            f.StartApproach,f.StartDate,SYSUTCDATETIME()
          FROM (SELECT DISTINCT TenantId,DATEFROMPARTS(YEAR(PostingDate),MONTH(PostingDate),1) PeriodStart
                FROM Accounting.JournalEntries) m
          JOIN Accounting.PolicyFreezes f ON f.TenantId=m.TenantId;
        """;

    private const string EnsureOpenPeriod = """
        CREATE PROCEDURE Accounting.EnsureOpenPeriod
          @PostingDate date,@ExpectedConfigurationVersion uniqueidentifier
        AS
        BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          IF @@TRANCOUNT=0 THROW 51000,'An outer source transaction is required.',1;
          DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
          IF @TenantId IS NULL OR @PostingDate IS NULL OR @ExpectedConfigurationVersion IS NULL
            THROW 51000,'A tenant, posting date and expected configuration are required.',1;
          DECLARE @Resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),@TenantId),@LockResult int;
          EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
          IF @LockResult<0 THROW 51009,'Accounting is being changed. Retry the request.',1;
          DECLARE @Payload nvarchar(max),@Version uniqueidentifier;
          SELECT @Payload=Payload,@Version=Version FROM Accounting.Configurations WITH(UPDLOCK,HOLDLOCK)
            WHERE TenantId=@TenantId;
          IF @Payload IS NULL THROW 51000,'Accounting configuration is incomplete.',1;
          IF @Version<>@ExpectedConfigurationVersion THROW 51009,'Accounting configuration changed.',1;
          DECLARE @MonthStart date=DATEFROMPARTS(YEAR(@PostingDate),MONTH(@PostingDate),1),
            @MonthEnd date=EOMONTH(@PostingDate),
            @FiscalMonth int=TRY_CONVERT(int,JSON_VALUE(@Payload,'$.policies.fiscalStartMonth')),
            @Scale int=TRY_CONVERT(int,JSON_VALUE(@Payload,'$.policies.scale')),
            @Currency nvarchar(3)=JSON_VALUE(@Payload,'$.policies.currency'),
            @StartApproach nvarchar(40)=JSON_VALUE(@Payload,'$.policies.startApproach'),
            @StartDate date=TRY_CONVERT(date,JSON_VALUE(@Payload,'$.policies.plannedStartDate'),23);
          IF @FiscalMonth NOT BETWEEN 1 AND 12 OR @Scale NOT BETWEEN 0 AND 4
            OR @Currency IS NULL OR @StartApproach IS NULL OR @StartDate IS NULL
            THROW 51000,'Accounting calendar is incomplete.',1;
          IF @MonthEnd<@StartDate OR @PostingDate<@StartDate
            THROW 51000,'The posting month precedes accounting start.',1;
          DECLARE @FiscalYear int=YEAR(@MonthStart)-CASE WHEN MONTH(@MonthStart)<@FiscalMonth THEN 1 ELSE 0 END;
          IF @FiscalYear<1 THROW 51000,'Fiscal-year start cannot be represented.',1;
          IF EXISTS(SELECT 1 FROM Accounting.PeriodClosures
                WHERE TenantId=@TenantId AND PeriodStart=@MonthStart)
            THROW 51009,'The posting period is closed.',1;
          IF NOT EXISTS(SELECT 1 FROM Accounting.Periods WITH(UPDLOCK,HOLDLOCK)
                WHERE TenantId=@TenantId AND PeriodStart=@MonthStart)
            INSERT Accounting.Periods(TenantId,PeriodStart,PeriodEnd,FiscalYearStart,ConfigurationVersion,
                Currency,Scale,FiscalStartMonth,StartApproach,AccountingStartDate,CreatedAtUtc)
              VALUES(@TenantId,@MonthStart,@MonthEnd,DATEFROMPARTS(@FiscalYear,@FiscalMonth,1),@Version,
                @Currency,@Scale,@FiscalMonth,@StartApproach,@StartDate,SYSUTCDATETIME());
        END;
        """;

    private const string ClosePeriod = """
        CREATE PROCEDURE Accounting.ClosePeriod
          @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,
          @ExpectedConfigurationVersion uniqueidentifier,@PeriodStart date,
          @Reason nvarchar(max),@Evidence nvarchar(max),@CanonicalInput nvarchar(max)
        AS
        BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          IF @@TRANCOUNT=0 THROW 51000,'An outer source transaction is required.',1;
          DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
          IF @TenantId IS NULL OR @ActorId IS NULL OR @SessionId IS NULL
            THROW 51003,'Current tenant and actor authority are required.',1;
          DECLARE @Resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),@TenantId),@LockResult int;
          EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
          IF @LockResult<0 THROW 51009,'Accounting is being changed. Retry the request.',1;
          BEGIN TRY
            EXEC Accounting.RequirePermission @ActorId,@SessionId,N'AccountingPeriodsClose';
          END TRY
          BEGIN CATCH
            IF ERROR_NUMBER()=50903 THROW 51003,'Current close authority is required.',1;
            THROW;
          END CATCH;
          IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000'
            OR @ExpectedConfigurationVersion IS NULL OR @PeriodStart IS NULL OR DAY(@PeriodStart)<>1
            OR @Reason IS NULL OR DATALENGTH(@Reason)>4000 OR LEN(TRIM(NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+NCHAR(8239)+NCHAR(8287)+NCHAR(12288) FROM @Reason))=0
            OR @Evidence IS NULL OR DATALENGTH(@Evidence)>262144 OR ISJSON(@Evidence,OBJECT)<>1
            OR @CanonicalInput IS NULL OR DATALENGTH(@CanonicalInput)>262144 OR ISJSON(@CanonicalInput,OBJECT)<>1
            THROW 51000,'Invalid period closure command.',1;
          IF EXISTS(SELECT 1 FROM OPENJSON(@Evidence) GROUP BY [key] HAVING COUNT(*)>1)
            OR EXISTS(SELECT 1 FROM OPENJSON(@CanonicalInput) GROUP BY [key] HAVING COUNT(*)>1)
            THROW 51000,'Duplicate closure fields are not allowed.',1;
          IF (SELECT COUNT(*) FROM OPENJSON(@Evidence))<>3
            OR EXISTS(SELECT 1 FROM OPENJSON(@Evidence) WHERE
                ([key] COLLATE Latin1_General_100_BIN2='schemaVersion' AND ([type]<>2 OR [value]<>'1')) OR
                ([key] COLLATE Latin1_General_100_BIN2='kind' AND [type]<>1) OR
                ([key] COLLATE Latin1_General_100_BIN2='periodStart' AND [type]<>1) OR
                [key] COLLATE Latin1_General_100_BIN2 NOT IN ('schemaVersion','kind','periodStart'))
            OR NOT EXISTS(SELECT 1 FROM OPENJSON(@Evidence) WHERE [key] COLLATE Latin1_General_100_BIN2='schemaVersion')
            OR NOT EXISTS(SELECT 1 FROM OPENJSON(@Evidence) WHERE [key] COLLATE Latin1_General_100_BIN2='kind')
            OR NOT EXISTS(SELECT 1 FROM OPENJSON(@Evidence) WHERE [key] COLLATE Latin1_General_100_BIN2='periodStart')
            OR JSON_VALUE(@Evidence,'$.periodStart')<>CONVERT(nvarchar(10),@PeriodStart,23)
            OR DATALENGTH(JSON_VALUE(@Evidence,'$.kind')) NOT BETWEEN 2 AND 128
            OR JSON_VALUE(@Evidence,'$.kind') COLLATE Latin1_General_100_BIN2 LIKE '%[^A-Za-z0-9._-]%'
            THROW 51000,'Invalid versioned closure evidence.',1;
          DECLARE @ExpectedInput nvarchar(max)=(SELECT @PeriodStart periodStart,
            @ExpectedConfigurationVersion expectedConfigurationVersion,@Reason reason,@Evidence evidence
            FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
          IF CONVERT(varbinary(max),@CanonicalInput)<>CONVERT(varbinary(max),@ExpectedInput)
            THROW 51000,'Invalid canonical closure input.',1;
          IF EXISTS(SELECT 1 FROM Accounting.PeriodCloseReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
          BEGIN
            IF NOT EXISTS(SELECT 1 FROM Accounting.PeriodCloseReceipts WHERE TenantId=@TenantId
              AND RequestId=@RequestId AND ActorId=@ActorId AND CommandKind=N'Period.Close'
              AND CommandVersion=1 AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@CanonicalInput))
              THROW 51009,'This closure request was already used with different content.',1;
            SELECT ClosureId,PeriodStart,RecordedAtUtc FROM Accounting.PeriodCloseReceipts
              WHERE TenantId=@TenantId AND RequestId=@RequestId;
            RETURN;
          END;
          IF EOMONTH(@PeriodStart)<(SELECT TRY_CONVERT(date,JSON_VALUE(Payload,'$.policies.plannedStartDate'),23)
                FROM Accounting.Configurations WHERE TenantId=@TenantId)
            THROW 51000,'The month precedes accounting start.',1;
          -- Use the final day when the configured first month starts partway through.
          DECLARE @PeriodEnd date=EOMONTH(@PeriodStart);
          EXEC Accounting.EnsureOpenPeriod @PostingDate=@PeriodEnd,
            @ExpectedConfigurationVersion=@ExpectedConfigurationVersion;
          IF EXISTS(SELECT 1 FROM Accounting.PeriodClosures WHERE TenantId=@TenantId AND PeriodStart=@PeriodStart)
            THROW 51009,'The accounting period is already closed.',1;
          DECLARE @Now datetimeoffset=SYSUTCDATETIME(),@ClosureId uniqueidentifier=NEWID();
          INSERT Accounting.PeriodClosures(TenantId,PeriodStart,Id,ActorId,Reason,EvidenceJson,EvidenceSha256,RecordedAtUtc)
            VALUES(@TenantId,@PeriodStart,@ClosureId,@ActorId,@Reason,@Evidence,
              HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Evidence)),@Now);
          INSERT Accounting.PeriodCloseReceipts(TenantId,RequestId,ActorId,CommandKind,CommandVersion,
            CanonicalInput,InputSha256,ClosureId,PeriodStart,RecordedAtUtc)
            VALUES(@TenantId,@RequestId,@ActorId,N'Period.Close',1,@CanonicalInput,
              HASHBYTES('SHA2_256',CONVERT(varbinary(max),@CanonicalInput)),@ClosureId,@PeriodStart,@Now);
          INSERT Security.TenantSecurityAuditEvents(Id,TenantId,ActorUserId,Action,TargetType,TargetId,OccurredAtUtc)
            VALUES(NEWID(),@TenantId,@ActorId,N'Accounting.ClosePeriod',N'PeriodClosure',@ClosureId,@Now);
          SELECT @ClosureId ClosureId,@PeriodStart PeriodStart,@Now RecordedAtUtc;
        END;
        """;

    private const string AlterSave = """
        DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Accounting].[Save]'));
        IF @Definition IS NULL OR CHARINDEX(N'IF EXISTS(SELECT 1 FROM Accounting.PolicyFreezes f WHERE f.TenantId=@TenantId AND (',@Definition)=0
          THROW 50020,'Unsupported Accounting.Save predecessor.',1;
        SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
        SET @Definition=REPLACE(@Definition,
          N'IF EXISTS(SELECT 1 FROM Accounting.PolicyFreezes f WHERE f.TenantId=@TenantId AND (',
          N'IF EXISTS(SELECT 1 FROM Accounting.Periods p WHERE p.TenantId=@TenantId AND (
                JSON_VALUE(@Payload,''$.policies.currency'') IS NULL OR
                JSON_VALUE(@Payload,''$.policies.currency'') COLLATE Latin1_General_100_BIN2<>p.Currency COLLATE Latin1_General_100_BIN2 OR
                TRY_CONVERT(int,JSON_VALUE(@Payload,''$.policies.scale'')) IS NULL OR
                TRY_CONVERT(int,JSON_VALUE(@Payload,''$.policies.scale''))<>p.Scale OR
                TRY_CONVERT(int,JSON_VALUE(@Payload,''$.policies.fiscalStartMonth'')) IS NULL OR
                TRY_CONVERT(int,JSON_VALUE(@Payload,''$.policies.fiscalStartMonth''))<>p.FiscalStartMonth OR
                JSON_VALUE(@Payload,''$.policies.startApproach'') IS NULL OR
                JSON_VALUE(@Payload,''$.policies.startApproach'') COLLATE Latin1_General_100_BIN2<>p.StartApproach COLLATE Latin1_General_100_BIN2 OR
                TRY_CONVERT(date,JSON_VALUE(@Payload,''$.policies.plannedStartDate''),23) IS NULL OR
                TRY_CONVERT(date,JSON_VALUE(@Payload,''$.policies.plannedStartDate''),23)<>p.AccountingStartDate))
                THROW 50909,''Materialized accounting calendar is frozen.'',1;
              IF EXISTS(SELECT 1 FROM Accounting.PolicyFreezes f WHERE f.TenantId=@TenantId AND (');
        EXEC sys.sp_executesql @Definition;
        """;
}
