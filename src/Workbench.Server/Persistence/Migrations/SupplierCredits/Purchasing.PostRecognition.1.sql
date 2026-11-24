-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE Purchasing.PostRecognition
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,
  @RequiredPermission nvarchar(max),@Command nvarchar(max),
  @ParentCorrectionGroupId uniqueidentifier=NULL,@ResultJson nvarchar(max)=NULL OUTPUT,@SuppressResult bit=0,
  @BillOwnerRequestId uniqueidentifier=NULL
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
    SELECT @ResultJson=ResultJson FROM Purchasing.RecognitionGroupReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId; IF @SuppressResult=0 SELECT @ResultJson ResultJson;
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
    DECLARE @BillGroup uniqueidentifier,@BillCorrection uniqueidentifier,@BillNow datetimeoffset,@BillPlan nvarchar(max),@BillBinding nvarchar(max);
    IF @BillOwnerRequestId IS NOT NULL
    BEGIN
      EXEC Purchasing.RequireSupplierBillComposition @ActorId,@SessionId,@BillOwnerRequestId,N'SuccessorRecognition',@UnitId,@Unit,
        @BillGroup OUTPUT,@BillCorrection OUTPUT,@BillNow OUTPUT,@BillPlan OUTPUT;
      SELECT @BillBinding=JSON_QUERY(value,'$.binding') FROM OPENJSON(@BillPlan,'$.units')
        WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.binding.successorUnitId'))=@UnitId;
      SET @ParentCorrectionGroupId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@BillBinding,'$.correctionGroupId'));
      SET @Now=@BillNow;
    END;
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
    DECLARE @CreditDependencies nvarchar(max)=N'[]',@CreditReduction decimal(28,4)=0;
    IF @Side='Recognition' AND @PriorEventId IS NOT NULL
    BEGIN
      DECLARE @LaterCreditIncrease decimal(38,4);
      ;WITH creditEvents AS (
        SELECT credit.PostingDate,CONVERT(decimal(38,4),claim.Amount) Amount
        FROM Purchasing.SupplierCreditClaims claim
        JOIN Purchasing.SupplierCredits credit ON credit.TenantId=claim.TenantId AND credit.Id=claim.CreditId
        WHERE claim.TenantId=@TenantId AND claim.RecognitionEventId=@PriorEventId AND claim.Treatment='Prepayment'
          AND credit.PostingDate>@PostingDate
        UNION ALL
        SELECT correction.PostingDate,-CONVERT(decimal(38,4),claim.Amount)
        FROM Purchasing.SupplierCreditClaims claim
        JOIN Purchasing.SupplierSourceCorrections correction ON correction.TenantId=claim.TenantId
          AND correction.SourceKind='SupplierCredit' AND correction.SourceId=claim.CreditId
        WHERE claim.TenantId=@TenantId AND claim.RecognitionEventId=@PriorEventId AND claim.Treatment='Prepayment'
          AND correction.PostingDate>@PostingDate
      ), dates AS (
        SELECT PostingDate,SUM(Amount) Amount FROM creditEvents GROUP BY PostingDate
      ), positions AS (
        SELECT SUM(Amount) OVER(ORDER BY PostingDate ROWS UNBOUNDED PRECEDING) Increase FROM dates
      )
      SELECT @LaterCreditIncrease=MAX(Increase) FROM positions;
      IF @LaterCreditIncrease>0 THROW 51009,'Recognition would overdraw prepayment at a later credit posting boundary.',1;
      SELECT @CreditReduction=COALESCE(SUM(claim.Amount),0) FROM Purchasing.SupplierCreditClaims claim
        JOIN Purchasing.SupplierCredits credit ON credit.TenantId=claim.TenantId AND credit.Id=claim.CreditId
        WHERE claim.TenantId=@TenantId AND claim.RecognitionEventId=@PriorEventId
          AND claim.Treatment='Prepayment' AND credit.PostingDate<=@PostingDate
          AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrections correction WHERE correction.TenantId=credit.TenantId
            AND correction.SourceKind='SupplierCredit' AND correction.SourceId=credit.Id AND correction.PostingDate<=@PostingDate);
      SET @CreditDependencies=(SELECT credit.Id creditId,credit.RevisionId creditRevisionId,claim.Id claimId,
        CONVERT(nvarchar(60),claim.Amount) amount FROM Purchasing.SupplierCreditClaims claim
        JOIN Purchasing.SupplierCredits credit ON credit.TenantId=claim.TenantId AND credit.Id=claim.CreditId
        WHERE claim.TenantId=@TenantId AND claim.RecognitionEventId=@PriorEventId
          AND claim.Treatment='Prepayment' AND credit.PostingDate<=@PostingDate
          AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrections correction WHERE correction.TenantId=credit.TenantId
            AND correction.SourceKind='SupplierCredit' AND correction.SourceId=credit.Id AND correction.PostingDate<=@PostingDate) ORDER BY credit.Id,claim.Id FOR JSON PATH);
      SET @PriorCost=@PriorCost-@CreditReduction;
      IF @PriorCost<0 THROW 51009,'Credit dependencies exceed remaining prepayment.',1;
      SET @Evidence=JSON_MODIFY(@Evidence,'$.supplierCreditDependencies',JSON_QUERY(@CreditDependencies));
    END;
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
