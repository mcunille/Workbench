// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class JournalSchema
{
    internal static void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[] { "PolicyFreezes", "SourceEvents", "JournalEntries", "JournalLines", "PostingReceipts" })
            migrationBuilder.Sql($"""
                ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                    ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Accounting].[{table}],
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Accounting].[{table}] AFTER INSERT,
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Accounting].[{table}] AFTER UPDATE;
                GRANT SELECT ON [Accounting].[{table}] TO [workbench_web];
                DENY INSERT,UPDATE,DELETE ON [Accounting].[{table}] TO [workbench_web];
                """);
        migrationBuilder.Sql(PostJournal);
        migrationBuilder.Sql(AlterSave);
        migrationBuilder.Sql("""
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            IF @Readiness IS NULL OR CHARINDEX(N'20260921051843_AddAccountingFoundation',@Readiness)=0
              THROW 50020,'Unsupported journal readiness predecessor.',1;
            SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Readiness=REPLACE(@Readiness,N'20260921051843_AddAccountingFoundation',N'20260923010000_AddAtomicJournal');
            EXEC sys.sp_executesql @Readiness;
            """);
    }

    private const string PostJournal = """
        CREATE PROCEDURE [Accounting].[PostJournal]
          @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,
          @RequiredPermission nvarchar(max),@SourceCommandKind nvarchar(max),@SourceCommandVersion int,
          @CanonicalInput nvarchar(max),@SourceKind nvarchar(max),@SourceId uniqueidentifier,
          @SourceRevision uniqueidentifier,@EventKind nvarchar(max),@RuleVersion int,
          @ExpectedConfigurationVersion uniqueidentifier,@Currency nvarchar(max),
          @DocumentDate date,@EffectiveDate date,@PostingDate date,
          @Reference nvarchar(max)=NULL,@Reason nvarchar(max)=NULL,
          @SourceSnapshot nvarchar(max),@Lines nvarchar(max)
        AS
        BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          IF @@TRANCOUNT=0 THROW 51000,'An outer source transaction is required.',1;
          DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
          IF @TenantId IS NULL OR @ActorId IS NULL OR @SessionId IS NULL
            THROW 51003,'Current tenant and actor authority are required.',1;
          IF @RequiredPermission IS NULL OR DATALENGTH(@RequiredPermission) NOT BETWEEN 2 AND 200
            THROW 51000,'A bounded source permission is required.',1;
          -- Callers acquire this same lock before their source-state checks. Reacquiring is transaction-owned.
          DECLARE @Resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),@TenantId),@LockResult int;
          EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
          IF @LockResult<0 THROW 51009,'Accounting is being changed. Retry the request.',1;
          BEGIN TRY
            EXEC Accounting.RequirePermission @ActorId,@SessionId,@RequiredPermission;
          END TRY
          BEGIN CATCH
            IF ERROR_NUMBER()=50903 THROW 51003,'Current source authority is required.',1;
            THROW;
          END CATCH;
          IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000'
            OR @SourceCommandKind IS NULL OR DATALENGTH(@SourceCommandKind) NOT BETWEEN 2 AND 128
            OR DATALENGTH(@SourceCommandKind)<>DATALENGTH(RTRIM(@SourceCommandKind))
            OR @SourceCommandKind COLLATE Latin1_General_100_BIN2 LIKE '%[^A-Za-z0-9._-]%'
            OR @SourceCommandVersion IS NULL OR @SourceCommandVersion<1
            OR @CanonicalInput IS NULL OR DATALENGTH(@CanonicalInput)>262144 OR ISJSON(@CanonicalInput,OBJECT)<>1
            THROW 51000,'Invalid source command.',1;
          IF EXISTS(SELECT 1 FROM Accounting.PostingReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
          BEGIN
            IF NOT EXISTS(SELECT 1 FROM Accounting.PostingReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId
              AND ActorId=@ActorId AND CONVERT(varbinary(max),SourceCommandKind)=CONVERT(varbinary(max),@SourceCommandKind)
              AND SourceCommandVersion=@SourceCommandVersion AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@CanonicalInput))
              THROW 51009,'This request identifier was already used with different content.',1;
            SELECT SourceEventId,JournalId,Sequence,RecordedAtUtc
              FROM Accounting.PostingReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId;
            RETURN;
          END;
          IF @SourceKind IS NULL OR DATALENGTH(@SourceKind) NOT BETWEEN 2 AND 128
            OR @EventKind IS NULL OR DATALENGTH(@EventKind) NOT BETWEEN 2 AND 128
            OR DATALENGTH(@SourceKind)<>DATALENGTH(RTRIM(@SourceKind))
            OR DATALENGTH(@EventKind)<>DATALENGTH(RTRIM(@EventKind))
            OR @SourceKind COLLATE Latin1_General_100_BIN2 LIKE '%[^A-Za-z0-9._-]%'
            OR @EventKind COLLATE Latin1_General_100_BIN2 LIKE '%[^A-Za-z0-9._-]%'
            OR @SourceId IS NULL OR @SourceId='00000000-0000-0000-0000-000000000000'
            OR @SourceRevision IS NULL OR @SourceRevision='00000000-0000-0000-0000-000000000000'
            OR @RuleVersion IS NULL OR @RuleVersion<1
            OR @ExpectedConfigurationVersion IS NULL OR @DocumentDate IS NULL OR @EffectiveDate IS NULL OR @PostingDate IS NULL
            OR @Currency IS NULL OR DATALENGTH(@Currency)<>6
            OR DATALENGTH(@Reference)>400 OR DATALENGTH(@Reason)>4000
            OR @SourceSnapshot IS NULL OR DATALENGTH(@SourceSnapshot)>262144 OR ISJSON(@SourceSnapshot,OBJECT)<>1
            OR @Lines IS NULL OR DATALENGTH(@Lines)>1048576 OR ISJSON(@Lines,ARRAY)<>1
            THROW 51000,'Invalid journal evidence or dates.',1;
          DECLARE @ExistingSourceEventId uniqueidentifier;
          SELECT @ExistingSourceEventId=Id FROM Accounting.SourceEvents WITH(UPDLOCK,HOLDLOCK)
            WHERE TenantId=@TenantId AND SourceKind=@SourceKind COLLATE Latin1_General_100_BIN2
              AND SourceId=@SourceId AND SourceRevision=@SourceRevision
              AND EventKind=@EventKind COLLATE Latin1_General_100_BIN2;
          IF @ExistingSourceEventId IS NOT NULL
          BEGIN
            DECLARE @SourceConflictMessage nvarchar(2048)=N'Source event already posted: '
              +CONVERT(nvarchar(36),@ExistingSourceEventId)+N'.';
            THROW 51009,@SourceConflictMessage,1;
          END;
          DECLARE @ConfigPayload nvarchar(max),@ConfigVersion uniqueidentifier;
          SELECT @ConfigPayload=Payload,@ConfigVersion=Version FROM Accounting.Configurations WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId;
          IF @ConfigPayload IS NULL THROW 51000,'Accounting configuration is incomplete.',1;
          IF @ConfigVersion<>@ExpectedConfigurationVersion
            THROW 51009,'Accounting configuration changed.',1;
          DECLARE @ConfiguredCurrency nvarchar(max)=JSON_VALUE(@ConfigPayload,'$.policies.currency'),
            @Scale int=TRY_CONVERT(int,JSON_VALUE(@ConfigPayload,'$.policies.scale')),
            @FiscalMonth int=TRY_CONVERT(int,JSON_VALUE(@ConfigPayload,'$.policies.fiscalStartMonth')),
            @StartApproach nvarchar(max)=JSON_VALUE(@ConfigPayload,'$.policies.startApproach'),
            @StartDate date=TRY_CONVERT(date,JSON_VALUE(@ConfigPayload,'$.policies.plannedStartDate'),23);
          IF @ConfiguredCurrency IS NULL OR @ConfiguredCurrency COLLATE Latin1_General_100_BIN2<>@Currency COLLATE Latin1_General_100_BIN2
            OR @Scale IS NULL OR @Scale NOT BETWEEN 0 AND 4 OR @FiscalMonth IS NULL OR @FiscalMonth NOT BETWEEN 1 AND 12
            OR @StartApproach IS NULL OR @StartDate IS NULL OR @PostingDate<@StartDate
            THROW 51000,'Accounting policy is incomplete or the posting is before its start date.',1;
          IF EXISTS(SELECT 1 FROM OPENJSON(@SourceSnapshot) GROUP BY [key] HAVING COUNT(*)>1)
             OR EXISTS(SELECT 1 FROM OPENJSON(@CanonicalInput) GROUP BY [key] HAVING COUNT(*)>1)
             THROW 51000,'Duplicate evidence fields are not allowed.',1;
          DECLARE @Count int=(SELECT COUNT(*) FROM OPENJSON(@Lines));
          IF @Count NOT BETWEEN 2 AND 1000 OR EXISTS(SELECT 1 FROM OPENJSON(@Lines) WHERE [type]<>5)
             THROW 51000,'A journal requires two to 1000 lines.',1;
          IF EXISTS(SELECT 1 FROM OPENJSON(@Lines) line WHERE
              (SELECT COUNT(*) FROM OPENJSON(line.value))<>5 OR
              EXISTS(SELECT [key] FROM OPENJSON(line.value) GROUP BY [key] HAVING COUNT(*)>1) OR
              EXISTS(SELECT 1 FROM OPENJSON(line.value) p WHERE DATALENGTH(p.[key])<>DATALENGTH(RTRIM(p.[key]))
                OR p.[key] COLLATE Latin1_General_100_BIN2 NOT IN ('ordinal','accountId','accountVersion','debit','credit')
                OR (p.[key] COLLATE Latin1_General_100_BIN2='ordinal' AND p.[type]<>2)
                OR (p.[key] COLLATE Latin1_General_100_BIN2<>'ordinal' AND p.[type]<>1)))
             THROW 51000,'Invalid journal line shape.',1;
          IF EXISTS(SELECT 1 FROM OPENJSON(@Lines) line CROSS APPLY OPENJSON(line.value) p
             WHERE p.[key] COLLATE Latin1_General_100_BIN2 IN ('accountId','accountVersion')
               AND (DATALENGTH(p.[value])<>72 OR TRY_CONVERT(uniqueidentifier,p.[value]) IS NULL))
             THROW 51000,'Invalid account identity.',1;
          DECLARE @Input TABLE(Ordinal int NULL,AccountId uniqueidentifier,AccountVersion uniqueidentifier,
             DebitText nvarchar(max),CreditText nvarchar(max),Debit decimal(28,4),Credit decimal(28,4));
          INSERT @Input(Ordinal,AccountId,AccountVersion,DebitText,CreditText)
            SELECT TRY_CONVERT(int,JSON_VALUE(value,'$.ordinal')),
              TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.accountId')),
              TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.accountVersion')),
              JSON_VALUE(value,'$.debit'),JSON_VALUE(value,'$.credit') FROM OPENJSON(@Lines);
          IF EXISTS(SELECT 1 FROM @Input WHERE Ordinal IS NULL OR Ordinal NOT BETWEEN 1 AND @Count OR AccountId IS NULL OR AccountVersion IS NULL
              OR DebitText IS NULL OR CreditText IS NULL OR LEN(DebitText) NOT BETWEEN 1 AND 29 OR LEN(CreditText) NOT BETWEEN 1 AND 29
              OR DATALENGTH(DebitText)<>DATALENGTH(RTRIM(DebitText))
              OR DATALENGTH(CreditText)<>DATALENGTH(RTRIM(CreditText))
              OR DebitText COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9.]%'
              OR CreditText COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9.]%'
              OR LEN(DebitText)-LEN(REPLACE(DebitText,'.',''))>1 OR LEN(CreditText)-LEN(REPLACE(CreditText,'.',''))>1
              OR LEFT(DebitText,1)='.' OR LEFT(CreditText,1)='.' OR RIGHT(DebitText,1)='.' OR RIGHT(CreditText,1)='.'
              OR LEN(LEFT(DebitText,CHARINDEX('.',DebitText+'.')-1))>24
              OR LEN(LEFT(CreditText,CHARINDEX('.',CreditText+'.')-1))>24
              OR LEN(SUBSTRING(DebitText,CHARINDEX('.',DebitText+'.')+1,50))>4
              OR LEN(SUBSTRING(CreditText,CHARINDEX('.',CreditText+'.')+1,50))>4
              OR TRY_CONVERT(decimal(28,4),DebitText) IS NULL OR TRY_CONVERT(decimal(28,4),CreditText) IS NULL
              OR TRY_CONVERT(decimal(28,4),DebitText)<>ROUND(TRY_CONVERT(decimal(28,4),DebitText),@Scale,1)
              OR TRY_CONVERT(decimal(28,4),CreditText)<>ROUND(TRY_CONVERT(decimal(28,4),CreditText),@Scale,1))
              OR EXISTS(SELECT Ordinal FROM @Input GROUP BY Ordinal HAVING COUNT(*)>1)
              THROW 51000,'Journal amounts or ordinals are invalid.',1;
          UPDATE @Input SET Debit=CONVERT(decimal(28,4),DebitText),Credit=CONVERT(decimal(28,4),CreditText);
          IF EXISTS(SELECT 1 FROM @Input WHERE (Debit>0 AND Credit>0) OR (Debit=0 AND Credit=0) OR Debit<0 OR Credit<0)
             THROW 51000,'Exactly one positive amount is required on each line.',1;
          IF EXISTS(SELECT 1 FROM @Input i LEFT JOIN Accounting.Accounts a WITH(UPDLOCK,HOLDLOCK)
            ON a.TenantId=@TenantId AND a.Id=i.AccountId
            WHERE a.Id IS NULL OR a.ArchivedAtUtc IS NOT NULL)
             THROW 51004,'An account is unavailable.',1;
          IF EXISTS(SELECT 1 FROM @Input i JOIN Accounting.Accounts a WITH(UPDLOCK,HOLDLOCK)
            ON a.TenantId=@TenantId AND a.Id=i.AccountId WHERE a.Version<>i.AccountVersion)
             THROW 51009,'An account revision changed.',1;
          DECLARE @DebitWide decimal(38,4),@CreditWide decimal(38,4),@Maximum decimal(38,4)=999999999999999999999999.9999;
          SELECT @DebitWide=SUM(CONVERT(decimal(38,4),Debit)),@CreditWide=SUM(CONVERT(decimal(38,4),Credit)) FROM @Input;
          IF @DebitWide<>@CreditWide OR @DebitWide<=0 OR @DebitWide>@Maximum OR @CreditWide>@Maximum
             THROW 51000,'Journal must balance within supported totals.',1;
          DECLARE @Now datetimeoffset=SYSUTCDATETIME(),@SourceEventId uniqueidentifier=NEWID(),@JournalId uniqueidentifier=NEWID(),@Sequence bigint;
          INSERT Accounting.SourceEvents(Id,TenantId,SourceKind,SourceId,SourceRevision,EventKind,RuleVersion,ActorId,
            DocumentDate,EffectiveDate,PostingDate,Reference,Reason,SnapshotJson,SnapshotSha256,RecordedAtUtc)
            VALUES(@SourceEventId,@TenantId,@SourceKind,@SourceId,@SourceRevision,@EventKind,@RuleVersion,@ActorId,
              @DocumentDate,@EffectiveDate,@PostingDate,@Reference,@Reason,@SourceSnapshot,
              HASHBYTES('SHA2_256',CONVERT(varbinary(max),@SourceSnapshot)),@Now);
          INSERT Accounting.JournalEntries(Id,TenantId,SourceEventId,ConfigurationVersion,Currency,Scale,
            DocumentDate,EffectiveDate,PostingDate,RecordedAtUtc,ActorId,Reference,Reason,DebitTotal,CreditTotal)
            VALUES(@JournalId,@TenantId,@SourceEventId,@ConfigVersion,@Currency,@Scale,
              @DocumentDate,@EffectiveDate,@PostingDate,@Now,@ActorId,@Reference,@Reason,
              CONVERT(decimal(28,4),@DebitWide),CONVERT(decimal(28,4),@CreditWide));
          SELECT @Sequence=Sequence FROM Accounting.JournalEntries WHERE TenantId=@TenantId AND Id=@JournalId;
          INSERT Accounting.JournalLines(TenantId,JournalId,Ordinal,AccountId,AccountVersion,
            AccountCode,AccountName,AccountType,AccountPurpose,Debit,Credit)
            SELECT @TenantId,@JournalId,i.Ordinal,a.Id,a.Version,a.Code,a.Name,a.Type,a.Purpose,i.Debit,i.Credit
              FROM @Input i JOIN Accounting.Accounts a ON a.TenantId=@TenantId AND a.Id=i.AccountId;
          IF NOT EXISTS(SELECT 1 FROM Accounting.PolicyFreezes WHERE TenantId=@TenantId)
            INSERT Accounting.PolicyFreezes(TenantId,ConfigurationVersion,Currency,Scale,FiscalStartMonth,StartApproach,
              StartDate,FirstJournalId,RecordedAtUtc)
              VALUES(@TenantId,@ConfigVersion,@Currency,@Scale,@FiscalMonth,@StartApproach,@StartDate,@JournalId,@Now);
          INSERT Accounting.PostingReceipts(TenantId,RequestId,ActorId,SourceCommandKind,SourceCommandVersion,
            CanonicalInput,InputSha256,SourceEventId,JournalId,Sequence,RecordedAtUtc)
            VALUES(@TenantId,@RequestId,@ActorId,@SourceCommandKind,@SourceCommandVersion,@CanonicalInput,
              HASHBYTES('SHA2_256',CONVERT(varbinary(max),@CanonicalInput)),@SourceEventId,@JournalId,@Sequence,@Now);
          INSERT Security.TenantSecurityAuditEvents(Id,TenantId,ActorUserId,Action,TargetType,TargetId,OccurredAtUtc)
            VALUES(NEWID(),@TenantId,@ActorId,N'Accounting.PostJournal',N'JournalEntry',@JournalId,@Now);
          SELECT @SourceEventId SourceEventId,@JournalId JournalId,@Sequence Sequence,@Now RecordedAtUtc;
        END;
        """;

    private const string AlterSave = """
        DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Accounting].[Save]'));
        IF @Definition IS NULL OR CHARINDEX(N'EXEC Accounting.ValidateConfiguration @TenantId,@Payload;',@Definition)=0
          OR CHARINDEX(N'UPDATE Accounting.Accounts SET ArchivedAtUtc=',@Definition)=0
          THROW 50020,'Unsupported Accounting.Save predecessor.',1;
        SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
        SET @Definition=REPLACE(@Definition,N'EXEC Accounting.ValidateConfiguration @TenantId,@Payload;',N'EXEC Accounting.ValidateConfiguration @TenantId,@Payload;
              IF EXISTS(SELECT 1 FROM Accounting.PolicyFreezes f WHERE f.TenantId=@TenantId AND (
                JSON_VALUE(@Payload,''$.policies.currency'') IS NULL OR
                JSON_VALUE(@Payload,''$.policies.currency'') COLLATE Latin1_General_100_BIN2<>f.Currency COLLATE Latin1_General_100_BIN2 OR
                JSON_VALUE(@Payload,''$.policies.scale'') IS NULL OR
                TRY_CONVERT(int,JSON_VALUE(@Payload,''$.policies.scale'')) IS NULL OR
                TRY_CONVERT(int,JSON_VALUE(@Payload,''$.policies.scale''))<>f.Scale OR
                JSON_VALUE(@Payload,''$.policies.fiscalStartMonth'') IS NULL OR
                TRY_CONVERT(int,JSON_VALUE(@Payload,''$.policies.fiscalStartMonth'')) IS NULL OR
                TRY_CONVERT(int,JSON_VALUE(@Payload,''$.policies.fiscalStartMonth''))<>f.FiscalStartMonth OR
                JSON_VALUE(@Payload,''$.policies.startApproach'') IS NULL OR
                JSON_VALUE(@Payload,''$.policies.startApproach'') COLLATE Latin1_General_100_BIN2<>f.StartApproach COLLATE Latin1_General_100_BIN2 OR
                JSON_VALUE(@Payload,''$.policies.plannedStartDate'') IS NULL OR
                TRY_CONVERT(date,JSON_VALUE(@Payload,''$.policies.plannedStartDate''),23) IS NULL OR
                TRY_CONVERT(date,JSON_VALUE(@Payload,''$.policies.plannedStartDate''),23)<>f.StartDate))
                THROW 50909,''Posted accounting policy is frozen.'',1;');
        SET @Definition=REPLACE(@Definition,N'UPDATE Accounting.Accounts SET ArchivedAtUtc=',N'IF JSON_VALUE(@Payload,''$.isArchived'')=''true'' AND EXISTS(SELECT 1 FROM Accounting.JournalLines WHERE TenantId=@TenantId AND AccountId=@Id)
                  THROW 50909,''Posted accounts cannot be archived.'',1;
                UPDATE Accounting.Accounts SET ArchivedAtUtc=');
        EXEC sys.sp_executesql @Definition;
        """;
}
