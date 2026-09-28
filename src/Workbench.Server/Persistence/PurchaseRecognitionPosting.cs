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
          @RequiredPermission nvarchar(max),@Command nvarchar(max),
          @ParentCorrectionGroupId uniqueidentifier=NULL,@ResultJson nvarchar(max)=NULL OUTPUT
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

          DECLARE @Canonical nvarchar(max);
          EXEC Purchasing.CanonicalizeRecognition @Command,0,@Canonical OUTPUT;
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
          IF @ParentCorrectionGroupId IS NOT NULL
          BEGIN
            SELECT @Now=RecordedAtUtc FROM Purchasing.RecognitionCorrectionGroups WHERE TenantId=@TenantId AND Id=@ParentCorrectionGroupId;
            IF NOT EXISTS(SELECT 1 FROM Purchasing.RecognitionCorrectionGroups WHERE TenantId=@TenantId AND Id=@ParentCorrectionGroupId AND Operation='Replace' AND ReplacementUnitId IS NULL)
              THROW 51009,'The replacement group is unavailable.',1;
          END;
          DECLARE @Events TABLE(Id uniqueidentifier,UnitId uniqueidentifier,JournalId uniqueidentifier);
          DECLARE @Matches TABLE(Id uniqueidentifier);
          DECLARE @UnitId uniqueidentifier,@Unit nvarchar(max);
          DECLARE unit_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Id,Input FROM @Units ORDER BY Id;
          OPEN unit_cursor; FETCH NEXT FROM unit_cursor INTO @UnitId,@Unit;
          WHILE @@FETCH_STATUS=0
          BEGIN
            DECLARE @Classification varchar(16)=JSON_VALUE(@Unit,'$.classification'),@Quantity decimal(28,6)=CONVERT(decimal(28,6),JSON_VALUE(@Unit,'$.quantity')),
              @Prior int=TRY_CONVERT(int,JSON_VALUE(@Unit,'$.expectedPriorEventRevision'));
            IF @Classification COLLATE Latin1_General_100_BIN2 NOT IN ('Expense','Inventory') OR @Quantity<=0 OR @Prior IS NULL OR @Prior<0
              THROW 51000,'Invalid recognition unit.',1;
            DECLARE @Existing bit=CASE WHEN EXISTS(SELECT 1 FROM Purchasing.RecognitionUnits WHERE TenantId=@TenantId AND Id=@UnitId) THEN 1 ELSE 0 END;
            IF @Existing=1 AND NOT EXISTS(SELECT 1 FROM Purchasing.RecognitionUnits WHERE TenantId=@TenantId AND Id=@UnitId
                AND PurchaseOrderId=@PurchaseOrderId AND SupplierId=@SupplierId AND Currency=@Currency AND Classification=@Classification AND Quantity=@Quantity
                AND CONVERT(varbinary(max),GoodsReference)=CONVERT(varbinary(max),JSON_VALUE(@Unit,'$.goodsReference'))
                AND CONVERT(varbinary(max),QuantityUnit)=CONVERT(varbinary(max),JSON_VALUE(@Unit,'$.quantityUnit')))
              THROW 51009,'Recognition unit identity or classification changed.',1;
            IF (@Existing=0 AND @Prior<>0) OR (@Existing=1 AND
                ((SELECT COUNT(*) FROM Purchasing.ActiveRecognitionSideEvents WHERE TenantId=@TenantId AND UnitId=@UnitId)<>1
                 OR @Prior<>(SELECT MAX(EventRevision) FROM Purchasing.ActiveRecognitionSideEvents WHERE TenantId=@TenantId AND UnitId=@UnitId)))
              THROW 51009,'Recognition prior event revision changed.',1;
            DECLARE @SideCount int=(SELECT COUNT(*) FROM OPENJSON(@Unit,'$.sides')),@SideOrdinal int=0;
            IF @SideCount NOT BETWEEN 1 AND 2 OR (@Existing=1 AND @SideCount<>1)
              OR EXISTS(SELECT JSON_VALUE(value,'$.side') FROM OPENJSON(@Unit,'$.sides') GROUP BY JSON_VALUE(value,'$.side') HAVING COUNT(*)>1)
              THROW 51000,'A unit accepts at most two distinct sides.',1;
            WHILE @SideOrdinal<@SideCount
            BEGIN
            DECLARE @SideInput nvarchar(max)=(SELECT value FROM OPENJSON(@Unit,'$.sides') WHERE CONVERT(int,[key])=@SideOrdinal),
              @Side varchar(16),@EventRevision int,@SourceId uniqueidentifier,@SourceRevision uniqueidentifier,
              @SourceComponentKey nvarchar(200),@SubdivisionKey nvarchar(200),@SourceQuantity decimal(28,6),@SourceAmount decimal(28,4),
              @DocumentDate date,@EffectiveDate date,@Evidence nvarchar(max);
            SELECT @Side=JSON_VALUE(@SideInput,'$.side'),@EventRevision=TRY_CONVERT(int,JSON_VALUE(@SideInput,'$.eventRevision')),
              @SourceId=CONVERT(uniqueidentifier,JSON_VALUE(@SideInput,'$.sourceId')),@SourceRevision=CONVERT(uniqueidentifier,JSON_VALUE(@SideInput,'$.sourceRevision')),
              @SourceComponentKey=JSON_VALUE(@SideInput,'$.sourceComponentKey'),@SubdivisionKey=JSON_VALUE(@SideInput,'$.subdivisionKey'),
              @SourceQuantity=CONVERT(decimal(28,6),JSON_VALUE(@SideInput,'$.sourceQuantity')),@SourceAmount=CONVERT(decimal(28,4),JSON_VALUE(@SideInput,'$.sourceAmount')),
              @DocumentDate=CONVERT(date,JSON_VALUE(@SideInput,'$.documentDate'),23),@EffectiveDate=CONVERT(date,JSON_VALUE(@SideInput,'$.effectiveDate'),23),
              @Evidence=JSON_QUERY(@SideInput,'$.evidence');
            IF @Side COLLATE Latin1_General_100_BIN2 NOT IN ('Recognition','Invoice') OR @EventRevision IS NULL OR @EventRevision<1 OR (@ParentCorrectionGroupId IS NULL AND @EventRevision<>1)
              OR @SourceQuantity<>@Quantity OR @SourceQuantity<=0 OR @EffectiveDate>@PostingDate
              THROW 51000,'Invalid whole-unit side, revision or effective date.',1;
            IF EXISTS(SELECT 1 FROM Purchasing.ActiveRecognitionSideEvents WHERE TenantId=@TenantId AND UnitId=@UnitId AND Side=@Side)
              THROW 51009,'This unit already has the submitted side.',1;
            DECLARE @PriorEventId uniqueidentifier=NULL,@PriorEvidence nvarchar(max)=NULL,@PriorCost decimal(28,4)=NULL,@PriorPostingDate date=NULL;
            SELECT @PriorEventId=Id,@PriorEvidence=EvidenceJson,@PriorPostingDate=PostingDate
              FROM Purchasing.ActiveRecognitionSideEvents WHERE TenantId=@TenantId AND UnitId=@UnitId AND Side<>@Side;
            IF @PriorPostingDate>@PostingDate THROW 51000,'The second side cannot predate the first posting.',1;
            SELECT @PriorCost=CONVERT(decimal(28,4),SUM(CASE WHEN Kind='Discount' THEN -CONVERT(decimal(38,4),Amount)
              WHEN Kind='RecoverableTax' THEN 0 ELSE CONVERT(decimal(38,4),Amount) END))
              FROM Purchasing.RecognitionComponents WHERE TenantId=@TenantId AND EventId=@PriorEventId;
            IF EXISTS(SELECT 1 FROM Purchasing.RecognitionSideEvents WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Side=@Side
              AND SourceId=@SourceId AND SourceRevision=@SourceRevision AND SourceComponentKey=@SourceComponentKey AND SubdivisionKey=@SubdivisionKey)
              THROW 51009,'Source subdivision already has an event.',1;
            IF @ParentCorrectionGroupId IS NULL AND EXISTS(SELECT 1 FROM Purchasing.RecognitionSideEvents WHERE TenantId=@TenantId AND Side=@Side
              AND SourceId=@SourceId AND SourceComponentKey=@SourceComponentKey AND SubdivisionKey=@SubdivisionKey)
              THROW 51009,'An existing source subdivision requires a correction.',1;
            IF EXISTS(SELECT 1 FROM Purchasing.RecognitionSideEvents e JOIN Purchasing.RecognitionEventCorrections c ON c.TenantId=e.TenantId AND c.OriginalEventId=e.Id
              JOIN Purchasing.RecognitionCorrectionGroups g ON g.TenantId=c.TenantId AND g.Id=c.CorrectionGroupId
              WHERE e.TenantId=@TenantId AND e.Side=@Side AND e.SourceId=@SourceId AND g.PostingDate>@PostingDate)
              THROW 51000,'A source claim cannot predate the correction releasing its capacity.',1;
            DECLARE @CapacityQuantity decimal(28,6)=CONVERT(decimal(28,6),JSON_VALUE(@Evidence,'$.sourceCapacityQuantity')),
              @CapacityAmount decimal(28,4)=CONVERT(decimal(28,4),JSON_VALUE(@Evidence,'$.sourceCapacityAmount'));
            IF @CapacityQuantity<=0 OR @SourceQuantity>@CapacityQuantity OR @SourceAmount>@CapacityAmount
              THROW 51000,'Source claim exceeds its capacity.',1;
            -- Revised source evidence changes the approved limit, never the surviving allocation budget.
            IF EXISTS(SELECT 1 FROM Purchasing.ActiveRecognitionSideEvents WHERE TenantId=@TenantId AND Side=@Side
              AND SourceId=@SourceId AND SourceComponentKey=@SourceComponentKey
              GROUP BY SourceId HAVING SUM(CONVERT(decimal(38,6),SourceQuantity))>@CapacityQuantity-@SourceQuantity
                OR SUM(CONVERT(decimal(38,4),SourceAmount))>@CapacityAmount-@SourceAmount)
              THROW 51009,'Source capacity is already claimed.',1;
            -- The first side establishes accrual or prepayment; a preceding side in this command counts too.
            IF @Side='Recognition' AND ((@PriorEventId IS NULL AND NULLIF(JSON_VALUE(@Evidence,'$.estimateBasis'),'') IS NULL)
                OR (@Classification='Inventory' AND (COALESCE(JSON_VALUE(@Evidence,'$.recognitionBasis'),'')<>'ControlTransferred'
                  OR JSON_VALUE(@Evidence,'$.controlTransferDate') IS NULL OR CONVERT(date,JSON_VALUE(@Evidence,'$.controlTransferDate'),23)>@EffectiveDate
                  OR JSON_VALUE(@Evidence,'$.inTransit') IS NULL))
                OR (@Classification='Expense' AND (COALESCE(JSON_VALUE(@Evidence,'$.recognitionBasis'),'') COLLATE Latin1_General_100_BIN2 NOT IN ('ServicePerformed','GoodsConsumed')
                  OR (JSON_VALUE(@Evidence,'$.recognitionBasis')='ServicePerformed' AND (JSON_VALUE(@Evidence,'$.serviceDescription') IS NULL OR JSON_VALUE(@Evidence,'$.serviceStartDate') IS NULL OR JSON_VALUE(@Evidence,'$.serviceEndDate') IS NULL
                  OR CONVERT(date,JSON_VALUE(@Evidence,'$.serviceStartDate'),23)>CONVERT(date,JSON_VALUE(@Evidence,'$.serviceEndDate'),23)
                  OR CONVERT(date,JSON_VALUE(@Evidence,'$.serviceEndDate'),23)>@EffectiveDate)))))
              THROW 51000,'Recognition evidence does not establish the selected classification.',1;
            IF @Side='Invoice' AND (COALESCE(JSON_VALUE(@Evidence,'$.invoiceEligible'),'false')<>'true'
              OR COALESCE(JSON_VALUE(@Evidence,'$.presentObligation'),'false')<>'true'
              OR (@PriorEventId IS NULL AND COALESCE(JSON_VALUE(@Evidence,'$.enforceableRight'),'false')<>'true'))
              THROW 51000,'Invoice does not establish an obligation and enforceable future right.',1;
            DECLARE @Components TABLE(ComponentKey nvarchar(200),Kind varchar(32),Amount decimal(28,4),Reason nvarchar(2000),AssignedCostComponentKey nvarchar(200));
            DELETE @Components;
            IF (SELECT COUNT(*) FROM OPENJSON(@SideInput,'$.components')) NOT BETWEEN 1 AND 1000
              OR EXISTS(SELECT JSON_VALUE(value,'$.componentKey') FROM OPENJSON(@SideInput,'$.components') GROUP BY JSON_VALUE(value,'$.componentKey') HAVING COUNT(*)>1)
              THROW 51000,'Components must have distinct bounded identities.',1;
            INSERT @Components SELECT JSON_VALUE(value,'$.componentKey'),JSON_VALUE(value,'$.kind'),CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')),
              JSON_VALUE(value,'$.reason'),JSON_VALUE(value,'$.assignedCostComponentKey') FROM OPENJSON(@SideInput,'$.components');
            IF EXISTS(SELECT 1 FROM @Components WHERE Kind COLLATE Latin1_General_100_BIN2 NOT IN
                ('BaseCost','Discount','Freight','Charge','NonrecoverableTax','RecoverableTax','Rounding')
                OR Amount<>ROUND(Amount,@Scale,1) OR (Kind<>'Rounding' AND Amount<0))
              THROW 51000,'Unsupported component kind or currency precision.',1;
            IF EXISTS(SELECT 1 FROM @Components c WHERE c.Kind IN ('Discount','Freight','Charge','NonrecoverableTax','Rounding')
                AND (c.AssignedCostComponentKey IS NULL OR NOT EXISTS(SELECT 1 FROM @Components b WHERE b.ComponentKey COLLATE Latin1_General_100_BIN2=c.AssignedCostComponentKey
                  AND b.Kind IN ('BaseCost','Freight','Charge'))))
              OR EXISTS(SELECT 1 FROM @Components WHERE Kind IN ('Freight','Charge','Rounding') AND NULLIF(Reason,'') IS NULL)
              OR (SELECT COUNT(*) FROM @Components WHERE Kind='Rounding')>1
              THROW 51000,'Component assignment, reason or rounding identity is invalid.',1;
            DECLARE @MinorUnit decimal(28,4)=CASE @Scale WHEN 0 THEN 1 WHEN 1 THEN 0.1 WHEN 2 THEN 0.01 WHEN 3 THEN 0.001 ELSE 0.0001 END;
            IF EXISTS(SELECT 1 FROM @Components WHERE Kind='Rounding' AND (ABS(Amount)>@MinorUnit OR @Side<>'Invoice'))
              OR (EXISTS(SELECT 1 FROM @Components WHERE Kind='Rounding') AND EXISTS(
                SELECT 1 FROM Purchasing.ActiveRecognitionSideEvents e WITH(UPDLOCK,HOLDLOCK)
                JOIN Purchasing.RecognitionComponents c ON c.TenantId=e.TenantId AND c.EventId=e.Id
                WHERE e.TenantId=@TenantId AND e.Side='Invoice' AND e.SourceId=@SourceId AND c.Kind='Rounding'))
              THROW 51000,'Invoice source already has rounding or its bound is exceeded.',1;
            DECLARE @CostWide decimal(38,4),@TaxWide decimal(38,4),@Cost decimal(28,4),@Tax decimal(28,4);
            SELECT @CostWide=COALESCE(SUM(CASE WHEN Kind='Discount' THEN -CONVERT(decimal(38,4),Amount)
                WHEN Kind IN ('BaseCost','Freight','Charge','NonrecoverableTax','Rounding') THEN CONVERT(decimal(38,4),Amount) ELSE 0 END),0),
              @TaxWide=COALESCE(SUM(CASE WHEN Kind='RecoverableTax' THEN CONVERT(decimal(38,4),Amount) ELSE 0 END),0) FROM @Components;
            IF @CostWide<0 OR @CostWide>999999999999999999999999.9999 OR @TaxWide>999999999999999999999999.9999
              OR (@Side='Recognition' AND @TaxWide<>0) THROW 51000,'Component totals do not match the source.',1;
            IF EXISTS(SELECT 1 FROM @Components b WHERE b.Kind IN ('BaseCost','Freight','Charge') AND
                b.Amount+COALESCE((SELECT SUM(CASE WHEN c.Kind='Discount' THEN -CONVERT(decimal(38,4),c.Amount)
                  WHEN c.Kind IN ('NonrecoverableTax','Rounding') THEN CONVERT(decimal(38,4),c.Amount) ELSE 0 END)
                  FROM @Components c WHERE c.AssignedCostComponentKey COLLATE Latin1_General_100_BIN2=b.ComponentKey),0)<0)
              THROW 51000,'Assigned cost component cannot become negative.',1;
            SET @Cost=CONVERT(decimal(28,4),@CostWide); SET @Tax=CONVERT(decimal(28,4),@TaxWide);
            IF @Cost+@Tax<>@SourceAmount OR @Cost+@Tax>999999999999999999999999.9999
              THROW 51000,'Component totals do not match the source.',1;
            IF EXISTS(SELECT 1 FROM @Components WHERE Kind IN ('RecoverableTax','NonrecoverableTax') AND Amount>0)
              AND JSON_VALUE(@Evidence,'$.taxPolicyReference') IS NULL
              THROW 51000,'Tax requires reviewed policy evidence.',1;
            IF @Tax>0 AND COALESCE(JSON_VALUE(@Evidence,'$.taxEntitlement'),'false')<>'true'
              THROW 51000,'Recoverable tax requires reviewed policy and current entitlement.',1;
            DECLARE @Variance decimal(28,4)=@Cost-@PriorCost;
            IF @Side='Invoice' AND @PriorEventId IS NOT NULL AND @Variance<>0
            BEGIN
              IF JSON_VALUE(@Evidence,'$.varianceAmount') IS NULL OR CONVERT(decimal(28,4),JSON_VALUE(@Evidence,'$.varianceAmount'))<>@Variance
                OR JSON_VALUE(@Evidence,'$.varianceReason') IS NULL OR COALESCE(JSON_VALUE(@Evidence,'$.varianceClassification'),'') COLLATE Latin1_General_100_BIN2<>@Classification
                THROW 51000,'Cost difference requires exact reviewed variance evidence.',1;
              -- The ungranted entrypoint consumes source-derived state; the trusted adapter must bind
              -- this evidence to durable inventory, under this transaction's source/accounting lock.
              IF @Classification='Inventory' AND COALESCE(JSON_VALUE(@Evidence,'$.inventoryAdjustmentState'),'') COLLATE Latin1_General_100_BIN2<>'Held'
                THROW 51000,'Inventory state cannot receive this cost adjustment.',1;
            END;
            DECLARE @Lines TABLE(Slot varchar(40),AccountId uniqueidentifier,AccountVersion uniqueidentifier,Debit decimal(28,4),Credit decimal(28,4),Historical bit);
            DELETE @Lines;
            IF @PriorEventId IS NULL
              INSERT @Lines(Slot,Debit,Credit,Historical) VALUES(CASE WHEN @Side='Recognition' THEN @Classification ELSE 'Prepayment' END,@Cost,0,0),
                (CASE WHEN @Side='Recognition' THEN 'GoodsReceivedNotInvoiced' ELSE 'SupplierPayable' END,0,@Cost+@Tax,0);
            ELSE IF @Side='Invoice'
              INSERT @Lines(Slot,Debit,Credit,Historical) VALUES('GoodsReceivedNotInvoiced',@PriorCost,0,1),
                (@Classification,CASE WHEN @Variance>0 THEN @Variance ELSE 0 END,CASE WHEN @Variance<0 THEN -@Variance ELSE 0 END,CASE WHEN @Classification='Expense' THEN 1 ELSE 0 END),
                ('SupplierPayable',0,@Cost+@Tax,0);
            ELSE
              INSERT @Lines(Slot,Debit,Credit,Historical) VALUES(@Classification,@PriorCost,0,0),('Prepayment',0,@PriorCost,1);
            IF @Tax>0 INSERT @Lines(Slot,Debit,Credit,Historical) VALUES('RecoverableTax',@Tax,0,0);
            UPDATE l SET AccountId=a.Id,AccountVersion=a.Version FROM @Lines l
              JOIN OPENJSON(@Config,'$.mappings') m ON JSON_VALUE(m.value,'$.slot') COLLATE Latin1_General_100_BIN2=l.Slot
              JOIN Accounting.Accounts a WITH(UPDLOCK,HOLDLOCK) ON a.TenantId=@TenantId AND a.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(m.value,'$.accountId'))
                AND a.ArchivedAtUtc IS NULL AND a.Type=CASE WHEN l.Slot='Expense' THEN 'Expense' WHEN l.Slot IN ('GoodsReceivedNotInvoiced','SupplierPayable') THEN 'Liability' ELSE 'Asset' END
                AND a.Purpose=CASE WHEN l.Slot='SupplierPayable' THEN 'SupplierPayable' ELSE 'General' END WHERE l.Historical=0;
            UPDATE l SET AccountId=a.Id,AccountVersion=a.Version FROM @Lines l
              JOIN OPENJSON(@PriorEvidence,'$.accountMappings') m ON JSON_VALUE(m.value,'$.slot') COLLATE Latin1_General_100_BIN2=l.Slot
              JOIN Accounting.Accounts a WITH(UPDLOCK,HOLDLOCK) ON a.TenantId=@TenantId AND a.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(m.value,'$.accountId'))
                AND a.ArchivedAtUtc IS NULL AND a.Type=CASE WHEN l.Slot='Expense' THEN 'Expense' WHEN l.Slot='GoodsReceivedNotInvoiced' THEN 'Liability' ELSE 'Asset' END
                AND a.Purpose='General' WHERE l.Historical=1;
            IF EXISTS(SELECT 1 FROM @Lines WHERE AccountId IS NULL) OR EXISTS(SELECT AccountId FROM @Lines GROUP BY AccountId HAVING COUNT(*)>1)
              THROW 51004,'A required recognition mapping is unavailable or incompatible.',1;
            -- Preserve mappings even for zero-only evidence. This SQL-owned enrichment is never an
            -- accepted caller property; canonical replay remains bound to the original typed input.
            DECLARE @Mappings nvarchar(max)=(SELECT Slot slot,AccountId accountId,AccountVersion accountVersion FROM @Lines ORDER BY Slot FOR JSON PATH);
            SET @Evidence=JSON_MODIFY(@Evidence,'$.accountMappings',JSON_QUERY(@Mappings));
            IF DATALENGTH(@Evidence)>262144 THROW 51000,'Recognition evidence exceeds its bound.',1;
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
            IF NOT EXISTS(SELECT 1 FROM Purchasing.RecognitionUnits WHERE TenantId=@TenantId AND Id=@UnitId)
            INSERT Purchasing.RecognitionUnits(TenantId,Id,PurchaseOrderId,PurchaseOrderRevision,SupplierId,Currency,GoodsReference,Classification,Quantity,QuantityUnit,PolicyVersion,CreatedAtUtc)
              VALUES(@TenantId,@UnitId,@PurchaseOrderId,@PoRevision,@SupplierId,@Currency,JSON_VALUE(@Unit,'$.goodsReference'),@Classification,@Quantity,JSON_VALUE(@Unit,'$.quantityUnit'),1,@Now);
            INSERT Purchasing.RecognitionSideEvents(TenantId,Id,UnitId,Side,EventRevision,SourceId,SourceRevision,SourceComponentKey,SubdivisionKey,SourceQuantity,SourceAmount,
              DocumentDate,EffectiveDate,PostingDate,ConfigurationVersion,ActorId,EvidenceJson,EvidenceSha256,JournalId,CorrectionGroupId,RecordedAtUtc)
              VALUES(@TenantId,@EventId,@UnitId,@Side,@EventRevision,@SourceId,@SourceRevision,@SourceComponentKey,@SubdivisionKey,@SourceQuantity,@SourceAmount,
                @DocumentDate,@EffectiveDate,@PostingDate,@ConfigurationVersion,@ActorId,@Evidence,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Evidence)),@JournalId,@ParentCorrectionGroupId,@Now);
            INSERT Purchasing.RecognitionComponents(TenantId,EventId,ComponentKey,Kind,Amount,Reason,AssignedCostComponentKey)
              SELECT @TenantId,@EventId,ComponentKey,Kind,Amount,Reason,AssignedCostComponentKey FROM @Components;
            INSERT @Events VALUES(@EventId,@UnitId,@JournalId);
            IF @PriorEventId IS NOT NULL
            BEGIN
              DECLARE @MatchId uniqueidentifier=NEWID();
              INSERT Purchasing.RecognitionMatches(TenantId,Id,UnitId,RecognitionEventId,InvoiceEventId,CorrectionGroupId,RecordedAtUtc)
                VALUES(@TenantId,@MatchId,@UnitId,CASE WHEN @Side='Recognition' THEN @EventId ELSE @PriorEventId END,
                  CASE WHEN @Side='Invoice' THEN @EventId ELSE @PriorEventId END,@ParentCorrectionGroupId,@Now);
              INSERT @Matches VALUES(@MatchId);
            END;
            SET @SideOrdinal+=1;
            END;
            FETCH NEXT FROM unit_cursor INTO @UnitId,@Unit;
          END;
          CLOSE unit_cursor; DEALLOCATE unit_cursor;
          DECLARE @UnitIds nvarchar(max)=(SELECT N'['+STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),Id)+N'"'),N',') WITHIN GROUP(ORDER BY Id)+N']' FROM @Units),
            @EventIds nvarchar(max)=(SELECT N'['+STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),Id)+N'"'),N',') WITHIN GROUP(ORDER BY Id)+N']' FROM @Events),
            @MatchIds nvarchar(max)=(SELECT N'['+COALESCE(STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),Id)+N'"'),N','),N'')+N']' FROM @Matches),
            @JournalIds nvarchar(max)=(SELECT N'['+COALESCE(STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),JournalId)+N'"'),N',') WITHIN GROUP(ORDER BY JournalId),N'')+N']' FROM @Events WHERE JournalId IS NOT NULL),
            @Result nvarchar(max);
          SET @Result=(SELECT @CommandId commandId,JSON_QUERY(@UnitIds) unitIds,JSON_QUERY(@EventIds) eventIds,JSON_QUERY(@MatchIds) matchIds,
            CONVERT(uniqueidentifier,NULL) correctionGroupId,JSON_QUERY(@JournalIds) journalIds,@Now recordedAtUtc FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
          SET @ResultJson=@Result;
          IF @ParentCorrectionGroupId IS NOT NULL RETURN;
          INSERT Purchasing.RecognitionGroupReceipts(TenantId,RequestId,ActorId,CommandKind,CommandVersion,CanonicalInput,InputSha256,ResultJson,RecordedAtUtc)
            VALUES(@TenantId,@RequestId,@ActorId,'Post',1,@Canonical,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Canonical)),@Result,@Now);
          INSERT Security.TenantSecurityAuditEvents(Id,TenantId,ActorUserId,Action,TargetType,TargetId,OccurredAtUtc)
            VALUES(NEWID(),@TenantId,@ActorId,N'Purchasing.PostRecognition',N'RecognitionCommand',@CommandId,@Now);
          SELECT @Result ResultJson;
        END;
        """;
}
