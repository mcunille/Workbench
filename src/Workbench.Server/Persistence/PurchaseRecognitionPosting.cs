// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class PurchaseRecognitionPosting
{
    // Only trusted source adapters may call this procedure. Source capacity and eligibility evidence
    // must be derived from durable upstream records under the transaction-owned accounting lock.
    // There is deliberately no runtime EXECUTE grant or production fixture/source-authority hook.
    internal const string Sql = """
        CREATE PROCEDURE Purchasing.PostRecognition
          @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,
          @RequiredPermission nvarchar(max),@Command nvarchar(max)
        AS
        BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          IF @@TRANCOUNT=0 THROW 51000,'An outer source transaction is required.',1;
          DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
          IF @TenantId IS NULL OR @ActorId IS NULL OR @SessionId IS NULL THROW 51003,'Current source authority is required.',1;
          IF @RequiredPermission IS NULL OR DATALENGTH(@RequiredPermission) NOT BETWEEN 2 AND 200
            THROW 51000,'A bounded source permission is required.',1;
          DECLARE @Resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),@TenantId),@LockResult int;
          EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
          IF @LockResult<0 THROW 51009,'Accounting is being changed. Retry.',1;
          BEGIN TRY
            EXEC Accounting.RequirePermission @ActorId,@SessionId,@RequiredPermission;
          END TRY BEGIN CATCH
            IF ERROR_NUMBER()=50903 THROW 51003,'Current source authority is required.',1;
            THROW;
          END CATCH;
          IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000'
            OR @Command IS NULL OR DATALENGTH(@Command)>262144 OR ISJSON(@Command,OBJECT)<>1
            THROW 51000,'Invalid recognition envelope.',1;

          -- Parse every property without narrowing first. The schema rejects duplicates, unknown fields,
          -- wrong JSON types and overlong values before JSON_VALUE or bounded SQL columns are used.
          DECLARE @Schema TABLE(Kind varchar(20),Name nvarchar(60) COLLATE Latin1_General_100_BIN2,JsonType int,Required bit,MaxBytes int);
          INSERT @Schema VALUES
            ('root','schemaVersion',2,1,2),('root','operation',1,1,8),('root','expectedConfigurationVersion',1,1,72),
            ('root','purchaseOrderId',1,1,72),('root','expectedPurchaseOrderVersion',1,1,36),('root','supplierId',1,1,72),
            ('root','currency',1,1,6),('root','postingDate',1,1,20),('root','units',4,1,262144),
            ('unit','unitId',1,1,72),('unit','classification',1,1,32),('unit','goodsReference',1,1,400),
            ('unit','quantity',1,1,58),('unit','quantityUnit',1,1,80),('unit','expectedPriorEventRevision',2,1,20),('unit','sides',4,1,262144),
            ('side','side',1,1,32),('side','eventRevision',2,1,20),('side','sourceId',1,1,72),('side','sourceRevision',1,1,72),
            ('side','sourceComponentKey',1,1,400),('side','subdivisionKey',1,1,400),('side','sourceQuantity',1,1,58),('side','sourceAmount',1,1,58),
            ('side','documentDate',1,1,20),('side','effectiveDate',1,1,20),('side','evidence',5,1,262144),('side','components',4,1,262144),
            ('component','componentKey',1,1,400),('component','kind',1,1,64),('component','amount',1,1,60),
            ('component','reason',1,0,4000),('component','assignedCostComponentKey',1,0,400),
            ('evidence','schemaVersion',2,1,2),('evidence','rationale',1,1,4000),
            ('evidence','sourceCapacityQuantity',1,1,58),('evidence','sourceCapacityAmount',1,1,58),
            ('evidence','recognitionBasis',1,0,80),('evidence','serviceDescription',1,0,4000),
            ('evidence','serviceStartDate',1,0,20),('evidence','serviceEndDate',1,0,20),('evidence','controlTransferDate',1,0,20),
            ('evidence','inTransit',3,0,10),('evidence','estimateBasis',1,0,4000),
            ('evidence','invoiceEligible',3,0,10),('evidence','presentObligation',3,0,10),('evidence','enforceableRight',3,0,10),
            ('evidence','taxPolicyReference',1,0,4000),('evidence','taxEntitlement',3,0,10),
            ('evidence','documentRevision',1,0,400),('evidence','documentDigest',1,0,128);
          DECLARE @Nodes TABLE(Id int IDENTITY PRIMARY KEY,ParentId int,Depth int,Kind varchar(20),Name nvarchar(4000) COLLATE Latin1_General_100_BIN2,
            JsonType int,Value nvarchar(max) COLLATE Latin1_General_100_BIN2,Canonical nvarchar(max) COLLATE Latin1_General_100_BIN2);
          INSERT @Nodes(ParentId,Depth,Kind,Name,JsonType,Value) VALUES(NULL,0,'root','',5,@Command);
          DECLARE @Depth int=0;
          WHILE EXISTS(SELECT 1 FROM @Nodes WHERE Depth=@Depth AND JsonType IN (4,5))
          BEGIN
            IF @Depth>7 THROW 51000,'Recognition input nesting is invalid.',1;
            INSERT @Nodes(ParentId,Depth,Kind,Name,JsonType,Value)
              SELECT n.Id,@Depth+1,CASE WHEN n.Kind='units' THEN 'unit' WHEN n.Kind='sides' THEN 'side' WHEN n.Kind='components' THEN 'component'
                WHEN p.[key] IN ('units','sides','components','evidence') THEN p.[key] ELSE '' END,p.[key],p.type,p.value
              FROM @Nodes n CROSS APPLY OPENJSON(n.Value) p WHERE n.Depth=@Depth AND n.JsonType IN (4,5);
            IF EXISTS(SELECT 1 FROM @Nodes n JOIN @Nodes p ON p.Id=n.ParentId
              LEFT JOIN @Schema s ON s.Kind=p.Kind AND s.Name=n.Name
              WHERE n.Depth=@Depth+1 AND ((p.JsonType=4 AND n.JsonType<>5) OR
                (p.JsonType=5 AND (s.Name IS NULL OR n.JsonType<>s.JsonType OR DATALENGTH(n.Value)>s.MaxBytes
                  OR DATALENGTH(n.Name)<>DATALENGTH(RTRIM(n.Name))))))
              OR EXISTS(SELECT 1 FROM @Nodes p JOIN @Schema s ON s.Kind=p.Kind AND s.Required=1
                WHERE p.Depth=@Depth AND NOT EXISTS(SELECT 1 FROM @Nodes n WHERE n.ParentId=p.Id AND n.Name=s.Name))
              OR EXISTS(SELECT 1 FROM @Nodes n JOIN @Nodes p ON p.Id=n.ParentId WHERE n.Depth=@Depth+1 AND p.JsonType=5
                GROUP BY n.ParentId,n.Name HAVING COUNT(*)>1)
              THROW 51000,'Invalid recognition property shape.',1;
            SET @Depth+=1;
          END;
          IF EXISTS(SELECT 1 FROM @Nodes WHERE Name IN ('unitId','purchaseOrderId','supplierId','sourceId','sourceRevision','expectedConfigurationVersion')
              AND (DATALENGTH(Value)<>72 OR TRY_CONVERT(uniqueidentifier,Value) IS NULL OR TRY_CONVERT(uniqueidentifier,Value)='00000000-0000-0000-0000-000000000000'))
            OR EXISTS(SELECT 1 FROM @Nodes WHERE Name IN ('postingDate','documentDate','effectiveDate','serviceStartDate','serviceEndDate','controlTransferDate')
              AND (DATALENGTH(Value)<>20 OR TRY_CONVERT(date,Value,23) IS NULL OR CONVERT(nvarchar(10),TRY_CONVERT(date,Value,23),23)<>Value))
            OR EXISTS(SELECT 1 FROM @Nodes WHERE JsonType=1 AND LEN(LTRIM(RTRIM(Value)))=0)
            OR EXISTS(SELECT 1 FROM @Nodes WHERE Name IN ('classification','side','kind','recognitionBasis') AND DATALENGTH(Value)<>DATALENGTH(RTRIM(Value)))
            OR EXISTS(SELECT 1 FROM @Nodes WHERE Name='schemaVersion' AND Value<>'1')
            OR JSON_VALUE(@Command,'$.operation') COLLATE Latin1_General_100_BIN2<>'Post'
            THROW 51000,'Invalid recognition identities, dates or policy version.',1;
          -- Exact decimals: reject exponent notation, signs (except rounding), whitespace and discarded precision.
          IF EXISTS(SELECT 1 FROM @Nodes n WHERE n.Name IN ('quantity','sourceQuantity','sourceAmount','sourceCapacityQuantity','sourceCapacityAmount','amount')
            AND (n.Value COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9.]%' OR LEFT(n.Value,1)='.' OR RIGHT(n.Value,1)='.'
              OR LEN(n.Value)-LEN(REPLACE(n.Value,'.',''))>1 OR TRY_CONVERT(decimal(38,6),n.Value) IS NULL
              OR LEN(SUBSTRING(n.Value,CHARINDEX('.',n.Value+'.')+1,100))>CASE WHEN n.Name IN ('quantity','sourceQuantity','sourceCapacityQuantity') THEN 6 ELSE 4 END
              OR LEN(LEFT(n.Value,CHARINDEX('.',n.Value+'.')-1))>CASE WHEN n.Name IN ('quantity','sourceQuantity','sourceCapacityQuantity') THEN 22 ELSE 24 END))
            THROW 51000,'Invalid exact decimal amount.',1;
          UPDATE @Nodes SET Canonical=CASE WHEN JsonType=1 THEN N'"'+STRING_ESCAPE(Value,'json')+N'"' ELSE Value END WHERE JsonType NOT IN (4,5);
          WHILE @Depth>=0
          BEGIN
            UPDATE n SET Canonical=CASE WHEN n.JsonType=4 THEN N'[' ELSE N'{' END+
              COALESCE((SELECT STRING_AGG(CONVERT(nvarchar(max),CASE WHEN parent.JsonType=5 THEN N'"'+STRING_ESCAPE(c.Name,'json')+N'":' ELSE N'' END)+c.Canonical,N',')
                WITHIN GROUP(ORDER BY CASE WHEN parent.JsonType=4 THEN TRY_CONVERT(int,c.Name) ELSE 0 END,c.Name)
                FROM @Nodes c JOIN @Nodes parent ON parent.Id=c.ParentId WHERE c.ParentId=n.Id),N'')+CASE WHEN n.JsonType=4 THEN N']' ELSE N'}' END
              FROM @Nodes n WHERE Depth=@Depth AND JsonType IN (4,5);
            SET @Depth-=1;
          END;
          DECLARE @Canonical nvarchar(max)=(SELECT Canonical FROM @Nodes WHERE ParentId IS NULL);
          IF DATALENGTH(@Canonical)>262144 THROW 51000,'Canonical recognition input exceeds its bound.',1;
          IF EXISTS(SELECT 1 FROM Purchasing.RecognitionGroupReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
          BEGIN
            IF NOT EXISTS(SELECT 1 FROM Purchasing.RecognitionGroupReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId
              AND ActorId=@ActorId AND CommandKind='Post' AND CommandVersion=1 AND CONVERT(varbinary(max),CanonicalInput)=CONVERT(varbinary(max),@Canonical))
              THROW 51009,'This request identifier was already used with different content.',1;
            SELECT ResultJson FROM Purchasing.RecognitionGroupReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId;
            RETURN;
          END;

          DECLARE @ConfigurationVersion uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')),
            @PurchaseOrderId uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
            @SupplierId uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),
            @PostingDate date=CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23),@Currency nvarchar(3)=JSON_VALUE(@Canonical,'$.currency'),
            @PurchaseOrderVersion binary(8)=TRY_CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'),1),
            @PoRevision int,@Config nvarchar(max),@Scale int;
          IF @PurchaseOrderVersion IS NULL OR DATALENGTH(JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'))<>36
            THROW 51000,'Invalid purchase order version.',1;
          SELECT @PoRevision=Revision FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK)
            WHERE TenantId=@TenantId AND Id=@PurchaseOrderId AND IsDeleted=0 AND State='Ordered'
              AND SupplierId=@SupplierId AND Currency COLLATE Latin1_General_100_BIN2=@Currency AND RowVersion=@PurchaseOrderVersion;
          IF @PoRevision IS NULL THROW 51009,'Purchase order identity or revision changed.',1;
          SELECT @Config=Payload FROM Accounting.Configurations WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Version=@ConfigurationVersion;
          IF @Config IS NULL THROW 51009,'Accounting configuration changed.',1;
          SET @Scale=TRY_CONVERT(int,JSON_VALUE(@Config,'$.policies.scale'));
          IF @Scale IS NULL OR @Scale NOT BETWEEN 0 AND 4 OR JSON_VALUE(@Config,'$.policies.currency') COLLATE Latin1_General_100_BIN2<>@Currency
            THROW 51000,'Recognition currency or policy is invalid.',1;
          EXEC Accounting.EnsureOpenPeriod @PostingDate,@ConfigurationVersion;
          DECLARE @Units TABLE(Id uniqueidentifier PRIMARY KEY,Input nvarchar(max));
          IF (SELECT COUNT(*) FROM OPENJSON(@Canonical,'$.units')) NOT BETWEEN 1 AND 1000
            OR EXISTS(SELECT JSON_VALUE(value,'$.unitId') FROM OPENJSON(@Canonical,'$.units') GROUP BY JSON_VALUE(value,'$.unitId') HAVING COUNT(*)>1)
            THROW 51000,'Recognition unit identities must be distinct and bounded.',1;
          INSERT @Units SELECT CONVERT(uniqueidentifier,JSON_VALUE(value,'$.unitId')),value FROM OPENJSON(@Canonical,'$.units');
          DECLARE @Now datetimeoffset=SYSUTCDATETIME(),@CommandId uniqueidentifier=NEWID();
          DECLARE @Events TABLE(Id uniqueidentifier,UnitId uniqueidentifier,JournalId uniqueidentifier);
          DECLARE @UnitId uniqueidentifier,@Unit nvarchar(max);
          DECLARE unit_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Id,Input FROM @Units ORDER BY Id;
          OPEN unit_cursor; FETCH NEXT FROM unit_cursor INTO @UnitId,@Unit;
          WHILE @@FETCH_STATUS=0
          BEGIN
            DECLARE @Classification varchar(16)=JSON_VALUE(@Unit,'$.classification'),@Quantity decimal(28,6)=CONVERT(decimal(28,6),JSON_VALUE(@Unit,'$.quantity')),
              @Prior int=TRY_CONVERT(int,JSON_VALUE(@Unit,'$.expectedPriorEventRevision'));
            IF @Classification COLLATE Latin1_General_100_BIN2 NOT IN ('Expense','Inventory') OR @Quantity<=0 OR @Prior IS NULL OR @Prior<>0
              THROW 51000,'Invalid independent recognition unit.',1;
            IF EXISTS(SELECT 1 FROM Purchasing.RecognitionUnits WHERE TenantId=@TenantId AND Id=@UnitId)
              THROW 51009,'Recognition unit already has an event.',1;
            -- Matching is installed by the following change; fail closed until both sides can commit together.
            IF (SELECT COUNT(*) FROM OPENJSON(@Unit,'$.sides'))<>1 THROW 51000,'Independent posting requires exactly one side.',1;
            DECLARE @SideInput nvarchar(max)=(SELECT value FROM OPENJSON(@Unit,'$.sides')),
              @Side varchar(16),@EventRevision int,@SourceId uniqueidentifier,@SourceRevision uniqueidentifier,
              @SourceComponentKey nvarchar(200),@SubdivisionKey nvarchar(200),@SourceQuantity decimal(28,6),@SourceAmount decimal(28,4),
              @DocumentDate date,@EffectiveDate date,@Evidence nvarchar(max);
            SELECT @Side=JSON_VALUE(@SideInput,'$.side'),@EventRevision=TRY_CONVERT(int,JSON_VALUE(@SideInput,'$.eventRevision')),
              @SourceId=CONVERT(uniqueidentifier,JSON_VALUE(@SideInput,'$.sourceId')),@SourceRevision=CONVERT(uniqueidentifier,JSON_VALUE(@SideInput,'$.sourceRevision')),
              @SourceComponentKey=JSON_VALUE(@SideInput,'$.sourceComponentKey'),@SubdivisionKey=JSON_VALUE(@SideInput,'$.subdivisionKey'),
              @SourceQuantity=CONVERT(decimal(28,6),JSON_VALUE(@SideInput,'$.sourceQuantity')),@SourceAmount=CONVERT(decimal(28,4),JSON_VALUE(@SideInput,'$.sourceAmount')),
              @DocumentDate=CONVERT(date,JSON_VALUE(@SideInput,'$.documentDate'),23),@EffectiveDate=CONVERT(date,JSON_VALUE(@SideInput,'$.effectiveDate'),23),
              @Evidence=JSON_QUERY(@SideInput,'$.evidence');
            IF @Side COLLATE Latin1_General_100_BIN2 NOT IN ('Recognition','Invoice') OR @EventRevision IS NULL OR @EventRevision<>1
              OR @SourceQuantity<>@Quantity OR @SourceQuantity<=0 OR @EffectiveDate>@PostingDate
              THROW 51000,'Invalid whole-unit side, revision or effective date.',1;
            IF EXISTS(SELECT 1 FROM Purchasing.RecognitionSideEvents WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Side=@Side
              AND SourceId=@SourceId AND SourceRevision=@SourceRevision AND SourceComponentKey=@SourceComponentKey AND SubdivisionKey=@SubdivisionKey)
              THROW 51009,'Source subdivision already has an event.',1;
            DECLARE @CapacityQuantity decimal(28,6)=CONVERT(decimal(28,6),JSON_VALUE(@Evidence,'$.sourceCapacityQuantity')),
              @CapacityAmount decimal(28,4)=CONVERT(decimal(28,4),JSON_VALUE(@Evidence,'$.sourceCapacityAmount'));
            IF @CapacityQuantity<=0 OR @SourceQuantity>@CapacityQuantity OR @SourceAmount>@CapacityAmount
              THROW 51000,'Source claim exceeds its capacity.',1;
            IF EXISTS(SELECT 1 FROM Purchasing.RecognitionSideEvents WHERE TenantId=@TenantId AND Side=@Side
              AND SourceId=@SourceId AND SourceRevision=@SourceRevision AND SourceComponentKey=@SourceComponentKey
              GROUP BY SourceId HAVING SUM(CONVERT(decimal(38,6),SourceQuantity))>@CapacityQuantity-@SourceQuantity
                OR SUM(CONVERT(decimal(38,4),SourceAmount))>@CapacityAmount-@SourceAmount)
              THROW 51009,'Source capacity is already claimed.',1;
            IF @Side='Recognition' AND (NULLIF(JSON_VALUE(@Evidence,'$.estimateBasis'),'') IS NULL
                OR (@Classification='Inventory' AND (COALESCE(JSON_VALUE(@Evidence,'$.recognitionBasis'),'')<>'ControlTransferred'
                  OR JSON_VALUE(@Evidence,'$.controlTransferDate') IS NULL OR CONVERT(date,JSON_VALUE(@Evidence,'$.controlTransferDate'),23)>@EffectiveDate
                  OR JSON_VALUE(@Evidence,'$.inTransit') IS NULL))
                OR (@Classification='Expense' AND (COALESCE(JSON_VALUE(@Evidence,'$.recognitionBasis'),'') COLLATE Latin1_General_100_BIN2 NOT IN ('ServicePerformed','GoodsConsumed')
                  OR (JSON_VALUE(@Evidence,'$.recognitionBasis')='ServicePerformed' AND (JSON_VALUE(@Evidence,'$.serviceDescription') IS NULL OR JSON_VALUE(@Evidence,'$.serviceStartDate') IS NULL OR JSON_VALUE(@Evidence,'$.serviceEndDate') IS NULL
                  OR CONVERT(date,JSON_VALUE(@Evidence,'$.serviceStartDate'),23)>CONVERT(date,JSON_VALUE(@Evidence,'$.serviceEndDate'),23)
                  OR CONVERT(date,JSON_VALUE(@Evidence,'$.serviceEndDate'),23)>@EffectiveDate)))))
              THROW 51000,'Recognition evidence does not establish the selected classification.',1;
            IF @Side='Invoice' AND (COALESCE(JSON_VALUE(@Evidence,'$.invoiceEligible'),'false')<>'true'
              OR COALESCE(JSON_VALUE(@Evidence,'$.presentObligation'),'false')<>'true' OR COALESCE(JSON_VALUE(@Evidence,'$.enforceableRight'),'false')<>'true')
              THROW 51000,'Invoice does not establish an obligation and enforceable future right.',1;
            DECLARE @Components TABLE(ComponentKey nvarchar(200),Kind varchar(32),Amount decimal(28,4),Reason nvarchar(2000),AssignedCostComponentKey nvarchar(200));
            DELETE @Components;
            IF (SELECT COUNT(*) FROM OPENJSON(@SideInput,'$.components')) NOT BETWEEN 1 AND 1000
              OR EXISTS(SELECT JSON_VALUE(value,'$.componentKey') FROM OPENJSON(@SideInput,'$.components') GROUP BY JSON_VALUE(value,'$.componentKey') HAVING COUNT(*)>1)
              THROW 51000,'Components must have distinct bounded identities.',1;
            INSERT @Components SELECT JSON_VALUE(value,'$.componentKey'),JSON_VALUE(value,'$.kind'),CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')),
              JSON_VALUE(value,'$.reason'),JSON_VALUE(value,'$.assignedCostComponentKey') FROM OPENJSON(@SideInput,'$.components');
            IF EXISTS(SELECT 1 FROM @Components WHERE Kind COLLATE Latin1_General_100_BIN2 NOT IN ('BaseCost','RecoverableTax') OR Amount<>ROUND(Amount,@Scale,1))
              THROW 51000,'Unsupported component kind or currency precision.',1;
            DECLARE @CostWide decimal(38,4),@TaxWide decimal(38,4),@Cost decimal(28,4),@Tax decimal(28,4);
            SELECT @CostWide=COALESCE(SUM(CASE WHEN Kind='BaseCost' THEN CONVERT(decimal(38,4),Amount) ELSE 0 END),0),
              @TaxWide=COALESCE(SUM(CASE WHEN Kind='RecoverableTax' THEN CONVERT(decimal(38,4),Amount) ELSE 0 END),0) FROM @Components;
            IF @CostWide>999999999999999999999999.9999 OR @TaxWide>999999999999999999999999.9999
              OR (@Side='Recognition' AND @TaxWide<>0) THROW 51000,'Component totals do not match the source.',1;
            SET @Cost=CONVERT(decimal(28,4),@CostWide); SET @Tax=CONVERT(decimal(28,4),@TaxWide);
            IF @Cost+@Tax<>@SourceAmount OR @Cost+@Tax>999999999999999999999999.9999
              THROW 51000,'Component totals do not match the source.',1;
            IF @Tax>0 AND (JSON_VALUE(@Evidence,'$.taxPolicyReference') IS NULL OR COALESCE(JSON_VALUE(@Evidence,'$.taxEntitlement'),'false')<>'true')
              THROW 51000,'Recoverable tax requires reviewed policy and current entitlement.',1;
            DECLARE @Lines TABLE(Slot varchar(40),AccountId uniqueidentifier,AccountVersion uniqueidentifier,Debit decimal(28,4),Credit decimal(28,4));
            DELETE @Lines;
            INSERT @Lines(Slot,Debit,Credit) VALUES(CASE WHEN @Side='Recognition' THEN @Classification ELSE 'Prepayment' END,@Cost,0),
              (CASE WHEN @Side='Recognition' THEN 'GoodsReceivedNotInvoiced' ELSE 'SupplierPayable' END,0,@Cost+@Tax);
            IF @Tax>0 INSERT @Lines(Slot,Debit,Credit) VALUES('RecoverableTax',@Tax,0);
            UPDATE l SET AccountId=a.Id,AccountVersion=a.Version FROM @Lines l
              JOIN OPENJSON(@Config,'$.mappings') m ON JSON_VALUE(m.value,'$.slot') COLLATE Latin1_General_100_BIN2=l.Slot
              JOIN Accounting.Accounts a WITH(UPDLOCK,HOLDLOCK) ON a.TenantId=@TenantId AND a.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(m.value,'$.accountId'))
                AND a.ArchivedAtUtc IS NULL AND a.Type=CASE WHEN l.Slot='Expense' THEN 'Expense' WHEN l.Slot IN ('GoodsReceivedNotInvoiced','SupplierPayable') THEN 'Liability' ELSE 'Asset' END
                AND a.Purpose=CASE WHEN l.Slot='SupplierPayable' THEN 'SupplierPayable' ELSE 'General' END;
            IF EXISTS(SELECT 1 FROM @Lines WHERE AccountId IS NULL) OR EXISTS(SELECT AccountId FROM @Lines GROUP BY AccountId HAVING COUNT(*)>1)
              THROW 51004,'A required recognition mapping is unavailable or incompatible.',1;
            DELETE @Lines WHERE Debit=0 AND Credit=0;
            DECLARE @EventId uniqueidentifier=NEWID(),@JournalId uniqueidentifier=NULL,@JournalRequestId uniqueidentifier=NEWID(),@LinesJson nvarchar(max),@Snapshot nvarchar(max);
            IF EXISTS(SELECT 1 FROM @Lines)
            BEGIN
              SET @LinesJson=(SELECT ROW_NUMBER() OVER(ORDER BY Slot) ordinal,AccountId accountId,AccountVersion accountVersion,
                CONVERT(varchar(30),Debit) debit,CONVERT(varchar(30),Credit) credit FROM @Lines FOR JSON PATH);
              SET @Snapshot=(SELECT @UnitId unitId,@EventId eventId,@PurchaseOrderId purchaseOrderId,@PoRevision purchaseOrderRevision,@SupplierId supplierId,
                @Classification classification,JSON_QUERY(@SideInput) side FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
              DECLARE @Posted TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
              DELETE @Posted;
              INSERT @Posted EXEC Accounting.PostJournal @ActorId=@ActorId,@SessionId=@SessionId,@RequestId=@JournalRequestId,
                @RequiredPermission=@RequiredPermission,@SourceCommandKind=N'PurchaseRecognition',@SourceCommandVersion=1,@CanonicalInput=@Canonical,
                @SourceKind=N'PurchaseRecognition',@SourceId=@EventId,@SourceRevision=@SourceRevision,@EventKind=@Side,@RuleVersion=1,
                @ExpectedConfigurationVersion=@ConfigurationVersion,@Currency=@Currency,@DocumentDate=@DocumentDate,@EffectiveDate=@EffectiveDate,
                @PostingDate=@PostingDate,@SourceSnapshot=@Snapshot,@Lines=@LinesJson;
              SELECT @JournalId=JournalId FROM @Posted;
              -- Normalize only rows created by this transaction's kernel call to the group's recorded instant.
              UPDATE Accounting.SourceEvents SET RecordedAtUtc=@Now WHERE TenantId=@TenantId AND Id IN (SELECT SourceEventId FROM @Posted);
              UPDATE Accounting.JournalEntries SET RecordedAtUtc=@Now WHERE TenantId=@TenantId AND Id=@JournalId;
              UPDATE Accounting.PostingReceipts SET RecordedAtUtc=@Now WHERE TenantId=@TenantId AND RequestId=@JournalRequestId;
              UPDATE Accounting.PolicyFreezes SET RecordedAtUtc=@Now WHERE TenantId=@TenantId AND FirstJournalId=@JournalId;
              UPDATE Security.TenantSecurityAuditEvents SET OccurredAtUtc=@Now WHERE TenantId=@TenantId AND TargetId=@JournalId AND Action='Accounting.PostJournal';
            END;
            INSERT Purchasing.RecognitionUnits(TenantId,Id,PurchaseOrderId,PurchaseOrderRevision,SupplierId,Currency,GoodsReference,Classification,Quantity,QuantityUnit,PolicyVersion,CreatedAtUtc)
              VALUES(@TenantId,@UnitId,@PurchaseOrderId,@PoRevision,@SupplierId,@Currency,JSON_VALUE(@Unit,'$.goodsReference'),@Classification,@Quantity,JSON_VALUE(@Unit,'$.quantityUnit'),1,@Now);
            INSERT Purchasing.RecognitionSideEvents(TenantId,Id,UnitId,Side,EventRevision,SourceId,SourceRevision,SourceComponentKey,SubdivisionKey,SourceQuantity,SourceAmount,
              DocumentDate,EffectiveDate,PostingDate,ConfigurationVersion,ActorId,EvidenceJson,EvidenceSha256,JournalId,RecordedAtUtc)
              VALUES(@TenantId,@EventId,@UnitId,@Side,@EventRevision,@SourceId,@SourceRevision,@SourceComponentKey,@SubdivisionKey,@SourceQuantity,@SourceAmount,
                @DocumentDate,@EffectiveDate,@PostingDate,@ConfigurationVersion,@ActorId,@Evidence,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Evidence)),@JournalId,@Now);
            INSERT Purchasing.RecognitionComponents(TenantId,EventId,ComponentKey,Kind,Amount,Reason,AssignedCostComponentKey)
              SELECT @TenantId,@EventId,ComponentKey,Kind,Amount,Reason,AssignedCostComponentKey FROM @Components;
            INSERT @Events VALUES(@EventId,@UnitId,@JournalId);
            FETCH NEXT FROM unit_cursor INTO @UnitId,@Unit;
          END;
          CLOSE unit_cursor; DEALLOCATE unit_cursor;
          DECLARE @UnitIds nvarchar(max)=(SELECT N'['+STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),Id)+N'"'),N',') WITHIN GROUP(ORDER BY Id)+N']' FROM @Units),
            @EventIds nvarchar(max)=(SELECT N'['+STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),Id)+N'"'),N',') WITHIN GROUP(ORDER BY Id)+N']' FROM @Events),
            @JournalIds nvarchar(max)=(SELECT N'['+COALESCE(STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),JournalId)+N'"'),N',') WITHIN GROUP(ORDER BY JournalId),N'')+N']' FROM @Events WHERE JournalId IS NOT NULL),
            @Result nvarchar(max);
          SET @Result=(SELECT @CommandId commandId,JSON_QUERY(@UnitIds) unitIds,JSON_QUERY(@EventIds) eventIds,JSON_QUERY(N'[]') matchIds,
            CONVERT(uniqueidentifier,NULL) correctionGroupId,JSON_QUERY(@JournalIds) journalIds,@Now recordedAtUtc FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
          INSERT Purchasing.RecognitionGroupReceipts(TenantId,RequestId,ActorId,CommandKind,CommandVersion,CanonicalInput,InputSha256,ResultJson,RecordedAtUtc)
            VALUES(@TenantId,@RequestId,@ActorId,'Post',1,@Canonical,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Canonical)),@Result,@Now);
          INSERT Security.TenantSecurityAuditEvents(Id,TenantId,ActorUserId,Action,TargetType,TargetId,OccurredAtUtc)
            VALUES(NEWID(),@TenantId,@ActorId,N'Purchasing.PostRecognition',N'RecognitionCommand',@CommandId,@Now);
          SELECT @Result ResultJson;
        END;
        """;
}
