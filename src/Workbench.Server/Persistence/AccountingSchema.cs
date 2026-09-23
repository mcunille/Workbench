// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;
namespace Workbench.Server.Persistence;

internal static class AccountingSchema
{
    internal static void Up(MigrationBuilder migrationBuilder, string migrationId)
    {
        foreach (var table in new[] { "Accounts", "Configurations", "Revisions", "Receipts" })
            migrationBuilder.Sql($"""
                ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                    ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Accounting].[{table}],
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Accounting].[{table}] AFTER INSERT,
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Accounting].[{table}] AFTER UPDATE;
                GRANT SELECT ON [Accounting].[{table}] TO [workbench_web];
                DENY INSERT,UPDATE,DELETE ON [Accounting].[{table}] TO [workbench_web];
                """);
        migrationBuilder.Sql(ValidateConfiguration);
        migrationBuilder.Sql(Save);
        migrationBuilder.Sql($"""
            GRANT EXECUTE ON [Accounting].[Save] TO [workbench_web];
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            IF @Readiness IS NULL OR CHARINDEX(N'20260921041331_MakeSupplierProfilesCustom',@Readiness)=0
                THROW 50020,'Unsupported accounting readiness predecessor.',1;
            SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Readiness=REPLACE(@Readiness,N'20260921041331_MakeSupplierProfilesCustom',N'{migrationId}');
            EXEC sys.sp_executesql @Readiness;
            """);
    }

    private const string ValidateConfiguration = """
        CREATE PROCEDURE Accounting.ValidateConfiguration @TenantId uniqueidentifier, @Payload nvarchar(max)
        AS
        BEGIN
          SET NOCOUNT ON;
          IF ISJSON(@Payload,OBJECT)<>1 OR EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE DATALENGTH([key])<>DATALENGTH(RTRIM([key])) OR [key] COLLATE Latin1_General_100_BIN2 NOT IN ('policies','mappings','coverage'))
            OR (SELECT COUNT(*) FROM OPENJSON(@Payload))<>3
            OR ISJSON(JSON_QUERY(@Payload,'$.policies'),OBJECT)<>1 OR JSON_QUERY(@Payload,'$.policies') IS NULL
            OR ISJSON(JSON_QUERY(@Payload,'$.mappings'),ARRAY)<>1 OR JSON_QUERY(@Payload,'$.mappings') IS NULL
            OR ISJSON(JSON_QUERY(@Payload,'$.coverage'),ARRAY)<>1 OR JSON_QUERY(@Payload,'$.coverage') IS NULL
            THROW 50900,'Policies, mappings and coverage are required.',1;
          DECLARE @Policies nvarchar(max)=JSON_QUERY(@Payload,'$.policies');
          IF EXISTS(SELECT 1 FROM OPENJSON(@Policies) WHERE type=1 AND DATALENGTH(value)>4000) THROW 50900,'Policy text exceeds its limit.',1;
          IF EXISTS(SELECT 1 FROM OPENJSON(@Policies) WHERE DATALENGTH([key])<>DATALENGTH(RTRIM([key])) OR [key] COLLATE Latin1_General_100_BIN2 NOT IN ('country','region','currency','scale','fiscalStartMonth','startApproach','plannedStartDate','retentionYears','retentionRationale','frameworkNotes'))
            OR EXISTS(SELECT [key] FROM OPENJSON(@Policies) GROUP BY [key] HAVING COUNT(*)>1)
            OR EXISTS(SELECT 1 FROM OPENJSON(@Policies) WHERE [type]<>0 AND (([key] COLLATE Latin1_General_100_BIN2 IN ('scale','fiscalStartMonth','retentionYears') AND ([type]<>2 OR TRY_CONVERT(int,[value]) IS NULL)) OR ([key] COLLATE Latin1_General_100_BIN2 NOT IN ('scale','fiscalStartMonth','retentionYears') AND [type]<>1)))
            THROW 50900,'Invalid accounting policies.',1;
          DECLARE @Country nvarchar(max)=JSON_VALUE(@Policies,'$.country'),@Region nvarchar(max)=JSON_VALUE(@Policies,'$.region'),
            @Currency nvarchar(max)=JSON_VALUE(@Policies,'$.currency'),@Start nvarchar(max)=JSON_VALUE(@Policies,'$.startApproach'),
            @Date nvarchar(max)=JSON_VALUE(@Policies,'$.plannedStartDate');
          IF (@Country IS NOT NULL AND (DATALENGTH(@Country)<>4 OR @Country COLLATE Latin1_General_100_BIN2 NOT IN ('US','CA','AU','GB','NZ','DE','FR','JP','CH','IN')))
            OR (@Currency IS NOT NULL AND (DATALENGTH(@Currency)<>6 OR @Currency COLLATE Latin1_General_100_BIN2 NOT IN ('USD','CAD','GBP','EUR','AUD','NZD','JPY','CHF','INR','CNY','KWD')))
            OR (JSON_VALUE(@Policies,'$.scale') IS NOT NULL AND TRY_CONVERT(int,JSON_VALUE(@Policies,'$.scale')) NOT BETWEEN 0 AND 4)
            OR (JSON_VALUE(@Policies,'$.fiscalStartMonth') IS NOT NULL AND TRY_CONVERT(int,JSON_VALUE(@Policies,'$.fiscalStartMonth')) NOT BETWEEN 1 AND 12)
            OR (JSON_VALUE(@Policies,'$.retentionYears') IS NOT NULL AND TRY_CONVERT(int,JSON_VALUE(@Policies,'$.retentionYears')) NOT BETWEEN 1 AND 1000)
            OR (@Start IS NOT NULL AND (DATALENGTH(@Start)<>DATALENGTH(RTRIM(@Start)) OR @Start COLLATE Latin1_General_100_BIN2 NOT IN ('FromBeginning','OpeningBalances')))
            OR (@Date IS NOT NULL AND (TRY_CONVERT(date,@Date,23) IS NULL OR DATALENGTH(@Date)<>20 OR CONVERT(nvarchar(10),TRY_CONVERT(date,@Date,23),23)<>@Date))
            OR EXISTS(SELECT 1 FROM OPENJSON(@Policies) WHERE [key] COLLATE Latin1_General_100_BIN2 IN ('retentionRationale','frameworkNotes') AND DATALENGTH([value])>4000)
            THROW 50900,'An accounting policy is outside the supported range.',1;
          IF @Region IS NOT NULL AND (@Country IS NULL OR DATALENGTH(@Region)<>DATALENGTH(RTRIM(@Region)) OR
            (@Country='US' AND @Region COLLATE Latin1_General_100_BIN2 NOT IN ('AL','AK','AZ','AR','CA','CO','CT','DE','DC','FL','GA','HI','ID','IL','IN','IA','KS','KY','LA','ME','MD','MA','MI','MN','MS','MO','MT','NE','NV','NH','NJ','NM','NY','NC','ND','OH','OK','OR','PA','RI','SC','SD','TN','TX','UT','VT','VA','WA','WV','WI','WY','AS','GU','MP','PR','VI')) OR
            (@Country='CA' AND @Region COLLATE Latin1_General_100_BIN2 NOT IN ('AB','BC','MB','NB','NL','NS','NT','NU','ON','PE','QC','SK','YT')) OR
            (@Country='AU' AND @Region COLLATE Latin1_General_100_BIN2 NOT IN ('ACT','NSW','NT','QLD','SA','TAS','VIC','WA')) OR @Country NOT IN ('US','CA','AU'))
            THROW 50900,'The selected region does not belong to the country.',1;
          IF (SELECT COUNT(*) FROM OPENJSON(@Payload,'$.mappings'))>8 OR (SELECT COUNT(*) FROM OPENJSON(@Payload,'$.coverage'))>200
            THROW 50900,'The configuration exceeds the supported size.',1;
          IF EXISTS(SELECT 1 FROM OPENJSON(@Payload,'$.mappings') WHERE type<>5) THROW 50900,'Invalid mapping structure.',1;
          IF EXISTS(SELECT 1 FROM OPENJSON(@Payload,'$.mappings') m WHERE m.[type]<>5 OR
            (SELECT COUNT(*) FROM OPENJSON(m.value))<>2 OR EXISTS(SELECT 1 FROM OPENJSON(m.value) WHERE DATALENGTH([key])<>DATALENGTH(RTRIM([key])) OR [key] COLLATE Latin1_General_100_BIN2 NOT IN ('slot','accountId') OR [type]<>1 OR ([key] COLLATE Latin1_General_100_BIN2='slot' AND DATALENGTH(value)>80) OR ([key] COLLATE Latin1_General_100_BIN2='accountId' AND DATALENGTH(value)<>72)))
            THROW 50900,'Invalid account mapping.',1;
          DECLARE @Mappings TABLE(Slot nvarchar(100),AccountId uniqueidentifier);
          INSERT @Mappings SELECT JSON_VALUE(value,'$.slot'),TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.accountId')) FROM OPENJSON(@Payload,'$.mappings');
          IF EXISTS(SELECT Slot FROM @Mappings GROUP BY Slot HAVING COUNT(*)>1) OR EXISTS(SELECT 1 FROM @Mappings WHERE AccountId IS NULL OR Slot IS NULL OR DATALENGTH(Slot)<>DATALENGTH(RTRIM(Slot)))
            THROW 50900,'Mapping slots must be unique and reference an account.',1;
          IF EXISTS(SELECT 1 FROM @Mappings m LEFT JOIN Accounting.Accounts a ON a.TenantId=@TenantId AND a.Id=m.AccountId WHERE a.Id IS NULL OR a.ArchivedAtUtc IS NOT NULL OR NOT (
            (m.Slot COLLATE Latin1_General_100_BIN2='SupplierPayable' AND a.Type='Liability' AND a.Purpose='SupplierPayable') OR
            (m.Slot COLLATE Latin1_General_100_BIN2='SupplierAdvance' AND a.Type='Asset' AND a.Purpose='SupplierAdvance') OR
            (m.Slot COLLATE Latin1_General_100_BIN2='SupplierCreditReceivable' AND a.Type='Asset' AND a.Purpose='SupplierCreditReceivable') OR
            (m.Slot COLLATE Latin1_General_100_BIN2='SupplierRefundClearing' AND a.Type='Liability' AND a.Purpose='SupplierRefundClearing') OR
            (m.Slot COLLATE Latin1_General_100_BIN2 IN ('Inventory','Prepayment','RecoverableTax') AND a.Type='Asset' AND a.Purpose='General') OR
            (m.Slot COLLATE Latin1_General_100_BIN2='Expense' AND a.Type='Expense' AND a.Purpose='General')))
            THROW 50900,'Mappings require active accounts with the designated type and purpose.',1;
          IF EXISTS(SELECT 1 FROM OPENJSON(@Payload,'$.coverage') WHERE type<>5) THROW 50900,'Invalid coverage structure.',1;
          IF EXISTS(SELECT 1 FROM OPENJSON(@Payload,'$.coverage') c WHERE c.type<>5 OR
            EXISTS(SELECT [key] FROM OPENJSON(c.value) GROUP BY [key] HAVING COUNT(*)>1) OR
            EXISTS(SELECT 1 FROM OPENJSON(c.value) WHERE DATALENGTH([key])<>DATALENGTH(RTRIM([key])) OR [key] COLLATE Latin1_General_100_BIN2 NOT IN ('accountId','included','exclusionRationale','evidenceKind','fromDate','toDate','evidenceReference','rationale','attestedComplete','classes')) OR
            NOT EXISTS(SELECT 1 FROM OPENJSON(c.value) WHERE [key] COLLATE Latin1_General_100_BIN2='accountId' AND [type]=1 AND DATALENGTH(value)=72) OR
            NOT EXISTS(SELECT 1 FROM OPENJSON(c.value) WHERE [key] COLLATE Latin1_General_100_BIN2='included' AND [type]=3) OR
            NOT EXISTS(SELECT 1 FROM OPENJSON(c.value) WHERE [key] COLLATE Latin1_General_100_BIN2='attestedComplete' AND [type]=3) OR
            NOT EXISTS(SELECT 1 FROM OPENJSON(c.value) WHERE [key] COLLATE Latin1_General_100_BIN2='classes' AND [type]=4) OR
            EXISTS(SELECT 1 FROM OPENJSON(c.value) WHERE [key] COLLATE Latin1_General_100_BIN2 IN ('exclusionRationale','evidenceKind','fromDate','toDate','evidenceReference','rationale') AND [type] NOT IN (0,1)))
            THROW 50900,'Invalid coverage inventory.',1;
          DECLARE @Coverage TABLE(AccountId uniqueidentifier,Included bit,Payload nvarchar(max));
          INSERT @Coverage SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.accountId')),CASE JSON_VALUE(value,'$.included') WHEN 'true' THEN 1 ELSE 0 END,value FROM OPENJSON(@Payload,'$.coverage');
          IF EXISTS(SELECT AccountId FROM @Coverage GROUP BY AccountId HAVING COUNT(*)>1) OR
            EXISTS(SELECT 1 FROM @Coverage c LEFT JOIN Accounting.Accounts a ON a.TenantId=@TenantId AND a.Id=c.AccountId WHERE a.Id IS NULL OR a.Purpose NOT IN ('Bank','Cash','CardLiability') OR (c.Included=1 AND a.ArchivedAtUtc IS NOT NULL))
            THROW 50900,'Coverage must reference distinct funding accounts in this business.',1;
          IF EXISTS(SELECT 1 FROM @Coverage c WHERE
            (c.Included=0 AND NULLIF(LTRIM(RTRIM(JSON_VALUE(c.Payload,'$.exclusionRationale'))),'') IS NULL) OR
            (JSON_VALUE(c.Payload,'$.evidenceKind') IS NOT NULL AND (DATALENGTH(JSON_VALUE(c.Payload,'$.evidenceKind'))<>DATALENGTH(RTRIM(JSON_VALUE(c.Payload,'$.evidenceKind'))) OR JSON_VALUE(c.Payload,'$.evidenceKind') COLLATE Latin1_General_100_BIN2 NOT IN ('Statement','NoPriorActivity'))) OR
            EXISTS(SELECT 1 FROM OPENJSON(c.Payload) WHERE (([key] COLLATE Latin1_General_100_BIN2 IN ('exclusionRationale','rationale') AND DATALENGTH(value)>4000) OR ([key] COLLATE Latin1_General_100_BIN2='evidenceReference' AND DATALENGTH(value)>1000))) OR
            EXISTS(SELECT 1 FROM OPENJSON(c.Payload) WHERE [key] COLLATE Latin1_General_100_BIN2 IN ('fromDate','toDate') AND [type]<>0 AND (DATALENGTH(value)<>20 OR TRY_CONVERT(date,value,23) IS NULL OR CONVERT(nvarchar(10),TRY_CONVERT(date,value,23),23)<>value)) OR
            TRY_CONVERT(date,JSON_VALUE(c.Payload,'$.fromDate'))>TRY_CONVERT(date,JSON_VALUE(c.Payload,'$.toDate')) OR
            (SELECT COUNT(*) FROM OPENJSON(c.Payload,'$.classes'))>100 OR
            (c.Included=1 AND (JSON_VALUE(c.Payload,'$.evidenceKind') IS NULL OR JSON_VALUE(c.Payload,'$.toDate') IS NULL OR
              (JSON_VALUE(c.Payload,'$.evidenceKind')='Statement' AND (JSON_VALUE(c.Payload,'$.fromDate') IS NULL OR NULLIF(LTRIM(RTRIM(JSON_VALUE(c.Payload,'$.evidenceReference'))),'') IS NULL)) OR
              (JSON_VALUE(c.Payload,'$.evidenceKind')='NoPriorActivity' AND (JSON_VALUE(c.Payload,'$.fromDate') IS NOT NULL OR NULLIF(LTRIM(RTRIM(JSON_VALUE(c.Payload,'$.rationale'))),'') IS NULL)) OR
              (JSON_VALUE(c.Payload,'$.attestedComplete')='true' AND NOT EXISTS(SELECT 1 FROM OPENJSON(c.Payload,'$.classes'))))))
            THROW 50900,'Invalid coverage evidence or date range.',1;
          IF EXISTS(SELECT 1 FROM @Coverage c CROSS APPLY OPENJSON(c.Payload,'$.classes') cl WHERE cl.type<>5) THROW 50900,'Invalid transaction class structure.',1;
          IF EXISTS(SELECT 1 FROM @Coverage c CROSS APPLY OPENJSON(c.Payload,'$.classes') cl WHERE cl.type<>5 OR
            NOT EXISTS(SELECT 1 FROM OPENJSON(cl.value) WHERE [key] COLLATE Latin1_General_100_BIN2='label' AND [type]=1 AND DATALENGTH(value) BETWEEN 2 AND 320 AND LEN(LTRIM(RTRIM(value)))>0) OR
            EXISTS(SELECT [key] FROM OPENJSON(cl.value) GROUP BY [key] HAVING COUNT(*)>1) OR
            EXISTS(SELECT 1 FROM OPENJSON(cl.value) WHERE DATALENGTH([key])<>DATALENGTH(RTRIM([key])) OR [key] COLLATE Latin1_General_100_BIN2 NOT IN ('label','sourceReference','policyReference','reconciliationReference','prerequisiteReference') OR ([key] COLLATE Latin1_General_100_BIN2<>'label' AND ([type] NOT IN (0,1) OR DATALENGTH(value)>1000))))
            THROW 50900,'Invalid transaction class.',1;
        END;
        """;

    private const string Save = """
        CREATE PROCEDURE [Accounting].[Save]
          @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Operation nvarchar(20),
          @Id uniqueidentifier=NULL,@ExpectedVersion uniqueidentifier=NULL,@Payload nvarchar(max)
        AS
        BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          BEGIN TRY
            BEGIN TRANSACTION;
            EXEC Accounting.RequirePermission @ActorId,@SessionId,N'AccountingConfigurationManage';
            DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
            IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000' OR @Payload IS NULL OR DATALENGTH(@Payload)>1048576 OR ISJSON(@Payload)<>1
              OR @Operation IS NULL OR DATALENGTH(@Operation)<>DATALENGTH(RTRIM(@Operation)) OR @Operation COLLATE Latin1_General_100_BIN2 NOT IN ('Configure','CreateAccounts','UpdateAccount','ArchiveAccount')
              THROW 50900,'A supported accounting command and request identifier are required.',1;
            DECLARE @LockResult int,@Resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),@TenantId);
            EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
            IF @LockResult<0 THROW 50909,'Accounting is being changed. Retry the request.',1;
            IF EXISTS(SELECT 1 FROM Accounting.Receipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
            BEGIN
              IF NOT EXISTS(SELECT 1 FROM Accounting.Receipts WHERE TenantId=@TenantId AND RequestId=@RequestId AND ActorId=@ActorId
                AND CONVERT(varbinary(max),Operation)=CONVERT(varbinary(max),@Operation)
                AND ((AccountId IS NULL AND @Id IS NULL) OR AccountId=@Id)
                AND ((ExpectedVersion IS NULL AND @ExpectedVersion IS NULL) OR ExpectedVersion=@ExpectedVersion)
                AND CONVERT(varbinary(max),Payload)=CONVERT(varbinary(max),@Payload))
                THROW 50909,'This request identifier was already used with different content.',1;
              COMMIT;
              SELECT SavedVersion,AccountIdsJson FROM Accounting.Receipts WHERE TenantId=@TenantId AND RequestId=@RequestId;
              RETURN;
            END;
            DECLARE @SavedVersion uniqueidentifier=NEWID(),@AccountIdsJson nvarchar(max)=N'[]',@Now datetimeoffset=SYSUTCDATETIME(),@CurrentVersion uniqueidentifier;
            IF @Operation='Configure'
            BEGIN
              IF @Id IS NOT NULL OR @ExpectedVersion IS NULL THROW 50900,'Configuration requires its expected version.',1;
              SELECT @CurrentVersion=Version FROM Accounting.Configurations WHERE TenantId=@TenantId;
              IF COALESCE(@CurrentVersion,'00000000-0000-0000-0000-000000000000')<>@ExpectedVersion THROW 50909,'Accounting configuration changed. Reload before saving.',1;
              EXEC Accounting.ValidateConfiguration @TenantId,@Payload;
              IF @CurrentVersion IS NULL INSERT Accounting.Configurations(TenantId,Payload,Version) VALUES(@TenantId,@Payload,@SavedVersion);
              ELSE UPDATE Accounting.Configurations SET Payload=@Payload,Version=@SavedVersion WHERE TenantId=@TenantId;
              INSERT Accounting.Revisions(Id,TenantId,AccountId,ActorId,RequestId,Operation,Payload,Version,RecordedAtUtc)
                VALUES(NEWID(),@TenantId,NULL,@ActorId,@RequestId,@Operation,@Payload,@SavedVersion,@Now);
            END
            ELSE IF @Operation='CreateAccounts'
            BEGIN
              IF @Id IS NOT NULL OR @ExpectedVersion IS NOT NULL OR ISJSON(@Payload,ARRAY)<>1 OR (SELECT COUNT(*) FROM OPENJSON(@Payload)) NOT BETWEEN 1 AND 100
                THROW 50900,'Supply between one and 100 accounts.',1;
              IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE type<>5) THROW 50900,'Invalid account structure.',1;
              IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) a WHERE a.type<>5 OR EXISTS(SELECT 1 FROM OPENJSON(a.value) WHERE DATALENGTH([key])<>DATALENGTH(RTRIM([key])) OR [key] COLLATE Latin1_General_100_BIN2 NOT IN ('code','name','type','purpose','description'))
                OR EXISTS(SELECT [key] FROM OPENJSON(a.value) GROUP BY [key] HAVING COUNT(*)>1)
                OR (SELECT COUNT(*) FROM OPENJSON(a.value) WHERE [key] COLLATE Latin1_General_100_BIN2 IN ('code','name','type','purpose') AND type=1)<>4
                OR EXISTS(SELECT 1 FROM OPENJSON(a.value) WHERE [key] COLLATE Latin1_General_100_BIN2='description' AND (type NOT IN (0,1) OR DATALENGTH(value)>4000)))
                THROW 50900,'Invalid account content.',1;
              DECLARE @Accounts TABLE(Ordinal int,Id uniqueidentifier,Code nvarchar(max),Name nvarchar(max),Type nvarchar(max),Purpose nvarchar(max),Description nvarchar(max));
              INSERT @Accounts SELECT CONVERT(int,[key]),NEWID(),JSON_VALUE(value,'$.code'),JSON_VALUE(value,'$.name'),JSON_VALUE(value,'$.type'),JSON_VALUE(value,'$.purpose'),JSON_VALUE(value,'$.description') FROM OPENJSON(@Payload);
              IF EXISTS(SELECT 1 FROM @Accounts WHERE DATALENGTH(Code) NOT BETWEEN 2 AND 64 OR Code COLLATE Latin1_General_100_BIN2 LIKE '%[^A-Z0-9._-]%'
                OR DATALENGTH(Name) NOT BETWEEN 2 AND 320 OR LEN(LTRIM(RTRIM(Name)))=0 OR DATALENGTH(Description)>4000 OR Code IS NULL OR Name IS NULL OR Type IS NULL OR Purpose IS NULL OR DATALENGTH(Type)<>DATALENGTH(RTRIM(Type)) OR DATALENGTH(Purpose)<>DATALENGTH(RTRIM(Purpose))
                OR Type COLLATE Latin1_General_100_BIN2 NOT IN ('Asset','Liability','Equity','Income','Expense') OR NOT (
                  Purpose COLLATE Latin1_General_100_BIN2='General' OR
                  (Purpose COLLATE Latin1_General_100_BIN2 IN ('Bank','Cash','SupplierAdvance','SupplierCreditReceivable') AND Type COLLATE Latin1_General_100_BIN2='Asset') OR
                  (Purpose COLLATE Latin1_General_100_BIN2 IN ('CardLiability','SupplierPayable','SupplierRefundClearing') AND Type COLLATE Latin1_General_100_BIN2='Liability')))
                THROW 50900,'Account code, name, type or purpose is invalid.',1;
              IF EXISTS(SELECT Code COLLATE Latin1_General_100_BIN2 FROM @Accounts GROUP BY Code COLLATE Latin1_General_100_BIN2 HAVING COUNT(*)>1)
                OR EXISTS(SELECT 1 FROM @Accounts n JOIN Accounting.Accounts a ON a.TenantId=@TenantId AND a.Code=n.Code COLLATE Latin1_General_100_BIN2)
                THROW 50909,'An account code is already reserved.',1;
              INSERT Accounting.Accounts(Id,TenantId,Code,Name,Type,Purpose,Description,ArchivedAtUtc,Version)
                SELECT Id,@TenantId,Code,Name,Type,Purpose,Description,NULL,@SavedVersion FROM @Accounts;
              INSERT Accounting.Revisions(Id,TenantId,AccountId,ActorId,RequestId,Operation,Payload,Version,RecordedAtUtc)
                SELECT NEWID(),@TenantId,a.Id,@ActorId,@RequestId,@Operation,(SELECT a.Code code,a.Name name,a.Type type,a.Purpose purpose,a.Description description FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES),@SavedVersion,@Now FROM @Accounts a;
              SELECT @AccountIdsJson=N'['+STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),Id)+N'"'),N',') WITHIN GROUP(ORDER BY Ordinal)+N']' FROM @Accounts;
            END
            ELSE
            BEGIN
              IF @Id IS NULL OR @ExpectedVersion IS NULL THROW 50900,'The account and its expected version are required.',1;
              SELECT @CurrentVersion=Version FROM Accounting.Accounts WHERE TenantId=@TenantId AND Id=@Id;
              IF @CurrentVersion IS NULL THROW 50904,'Account not found.',1;
              IF @CurrentVersion<>@ExpectedVersion THROW 50909,'The account changed. Reload before saving.',1;
              IF ISJSON(@Payload,OBJECT)<>1 THROW 50900,'Invalid account update.',1;
              IF @Operation='UpdateAccount'
              BEGIN
                IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE DATALENGTH([key])<>DATALENGTH(RTRIM([key])) OR [key] COLLATE Latin1_General_100_BIN2 NOT IN ('code','name','description')) OR EXISTS(SELECT [key] FROM OPENJSON(@Payload) GROUP BY [key] HAVING COUNT(*)>1)
                  OR (SELECT COUNT(*) FROM OPENJSON(@Payload) WHERE [key] COLLATE Latin1_General_100_BIN2 IN ('code','name') AND type=1)<>2
                  OR EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key] COLLATE Latin1_General_100_BIN2='description' AND (type NOT IN (0,1) OR DATALENGTH(value)>4000))
                  THROW 50900,'Only descriptive account fields can be updated.',1;
                DECLARE @Code nvarchar(max)=JSON_VALUE(@Payload,'$.code'),@Name nvarchar(max)=JSON_VALUE(@Payload,'$.name'),@Description nvarchar(max)=JSON_VALUE(@Payload,'$.description');
                IF DATALENGTH(@Code) NOT BETWEEN 2 AND 64 OR @Code COLLATE Latin1_General_100_BIN2 LIKE '%[^A-Z0-9._-]%'
                  OR DATALENGTH(@Name) NOT BETWEEN 2 AND 320 OR LEN(LTRIM(RTRIM(@Name)))=0 OR DATALENGTH(@Description)>4000 OR @Code IS NULL OR @Name IS NULL
                  THROW 50900,'Account code, name or description is invalid.',1;
                IF EXISTS(SELECT 1 FROM Accounting.Accounts WHERE TenantId=@TenantId AND Code=@Code COLLATE Latin1_General_100_BIN2 AND Id<>@Id)
                  THROW 50909,'An account code is already reserved.',1;
                UPDATE Accounting.Accounts SET Code=@Code,Name=@Name,Description=@Description,Version=@SavedVersion WHERE TenantId=@TenantId AND Id=@Id;
              END
              ELSE
              BEGIN
                IF EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE DATALENGTH([key])<>DATALENGTH(RTRIM([key]))) OR (SELECT COUNT(*) FROM OPENJSON(@Payload))<>1 OR NOT EXISTS(SELECT 1 FROM OPENJSON(@Payload) WHERE [key] COLLATE Latin1_General_100_BIN2='isArchived' AND type=3)
                  THROW 50900,'Supply the account archive state.',1;
                IF JSON_VALUE(@Payload,'$.isArchived')='true' AND EXISTS(SELECT 1 FROM Accounting.Configurations c WHERE c.TenantId=@TenantId AND (
                  EXISTS(SELECT 1 FROM OPENJSON(c.Payload,'$.mappings') m WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(m.value,'$.accountId'))=@Id) OR
                  EXISTS(SELECT 1 FROM OPENJSON(c.Payload,'$.coverage') v WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(v.value,'$.accountId'))=@Id AND JSON_VALUE(v.value,'$.included')='true')))
                  THROW 50909,'Remove current mappings and included coverage before archiving this account.',1;
                UPDATE Accounting.Accounts SET ArchivedAtUtc=CASE JSON_VALUE(@Payload,'$.isArchived') WHEN 'true' THEN COALESCE(ArchivedAtUtc,@Now) ELSE NULL END,Version=@SavedVersion WHERE TenantId=@TenantId AND Id=@Id;
              END;
              SET @AccountIdsJson=N'["'+CONVERT(nvarchar(36),@Id)+N'"]';
              INSERT Accounting.Revisions(Id,TenantId,AccountId,ActorId,RequestId,Operation,Payload,Version,RecordedAtUtc)
                SELECT NEWID(),@TenantId,@Id,@ActorId,@RequestId,@Operation,(SELECT a.Code code,a.Name name,a.Type type,a.Purpose purpose,a.Description description,a.ArchivedAtUtc archivedAtUtc FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES),@SavedVersion,@Now FROM Accounting.Accounts a WHERE a.TenantId=@TenantId AND a.Id=@Id;
            END;
            INSERT Accounting.Receipts(TenantId,RequestId,ActorId,Operation,AccountId,ExpectedVersion,Payload,SavedVersion,AccountIdsJson,RecordedAtUtc)
              VALUES(@TenantId,@RequestId,@ActorId,@Operation,@Id,@ExpectedVersion,@Payload,@SavedVersion,@AccountIdsJson,@Now);
            INSERT Security.TenantSecurityAuditEvents(Id,TenantId,ActorUserId,Action,TargetType,TargetId,OccurredAtUtc)
              VALUES(NEWID(),@TenantId,@ActorId,N'Accounting.'+@Operation,N'Accounting',COALESCE(@Id,@TenantId),@Now);
            COMMIT;
            SELECT @SavedVersion SavedVersion,@AccountIdsJson AccountIdsJson;
          END TRY
          BEGIN CATCH
            IF @@TRANCOUNT>0 ROLLBACK;
            THROW;
          END CATCH;
        END;
        """;
}
