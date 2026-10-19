-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PlanSupplierCashCorrection
  @TenantId uniqueidentifier,@ActorId uniqueidentifier,@SessionId uniqueidentifier,@Operation nvarchar(40),
  @Command nvarchar(max),@Plan nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId IS NULL OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Cash correction planning requires its owner transaction.',1;
  DECLARE @Canonical nvarchar(max),@Sources nvarchar(max),@Releases nvarchar(max),@Applications nvarchar(max),
    @Targets nvarchar(max),@EvidenceBindings nvarchar(max),@Evidence nvarchar(max);
  EXEC Purchasing.CanonicalizeSupplierSourceCorrection @Operation,@Command,@Canonical OUTPUT;
  SET @Canonical=JSON_MODIFY(@Canonical,'$.expectedPlanFingerprint',NULL);
  EXEC Purchasing.RequireSupplierCashCorrectionAuthority @ActorId,@SessionId,@Operation,@Canonical;
  DECLARE @Root uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.sourceId')),
    @Po uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
    @Supplier uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),
    @Currency varchar(3)=JSON_VALUE(@Canonical,'$.currency'),@Date date=CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23),
    @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')),
    @Policy nvarchar(max),@Scale int;
  IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@Po
    AND State='Ordered' AND IsDeleted=0 AND SupplierId=@Supplier AND Currency=@Currency
    AND RowVersion=CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'),1))
    THROW 51009,'Purchase revision changed.',1;
  SELECT @Policy=Payload FROM Accounting.Configurations WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Version=@Config;
  IF @Policy IS NULL THROW 51009,'Accounting configuration changed.',1;
  SET @Scale=TRY_CONVERT(int,JSON_VALUE(@Policy,'$.policies.scale'));
  IF @Scale IS NULL OR @Scale NOT BETWEEN 0 AND 4 OR JSON_VALUE(@Policy,'$.policies.currency') COLLATE Latin1_General_100_BIN2<>@Currency
    THROW 51000,'Cash correction currency or policy is invalid.',1;
  EXEC Accounting.EnsureOpenPeriod @Date,@Config;
  EXEC Purchasing.BuildSupplierCashCorrectionSources @TenantId,@Operation,@Canonical,@Sources OUTPUT,@Releases OUTPUT,@Applications OUTPUT;
  EXEC Purchasing.ResolveSupplierCashCorrectionReplacements @TenantId,@Canonical,@Sources OUTPUT,@Targets OUTPUT,@EvidenceBindings OUTPUT;
  IF EXISTS(SELECT 1 FROM OPENJSON(@Releases)) OR EXISTS(SELECT 1 FROM OPENJSON(@Applications))
    OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.reapplications'))
  BEGIN
    BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage'; END TRY
    BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current allocation authority is required for clearing dependencies.',1; THROW; END CATCH;
  END;
  DECLARE @Effects TABLE(Kind varchar(40),EntityId uniqueidentifier,ItemId uniqueidentifier,PostingDate date,Amount decimal(28,4));
  INSERT @Effects SELECT 'ReverseSource',cash.Id,settlement.ItemId,@Date,settlement.Amount
    FROM OPENJSON(@Sources) source JOIN Purchasing.SupplierCashSources cash ON cash.TenantId=@TenantId
      AND cash.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.sourceId'))
    JOIN Purchasing.SupplierCashSettlements settlement ON settlement.TenantId=cash.TenantId AND settlement.CashSourceId=cash.Id;
  INSERT @Effects SELECT 'ReverseRelease',TRY_CONVERT(uniqueidentifier,JSON_VALUE(released.value,'$.journalId')),item.ItemId,@Date,
      -TRY_CONVERT(decimal(28,4),JSON_VALUE(released.value,'$.amount'))
    FROM OPENJSON(@Releases) released CROSS APPLY(VALUES(TRY_CONVERT(uniqueidentifier,JSON_VALUE(released.value,'$.fundingItemId'))),
      (TRY_CONVERT(uniqueidentifier,JSON_VALUE(released.value,'$.clearingItemId')))) item(ItemId);
  INSERT @Effects SELECT 'ReverseApplication',TRY_CONVERT(uniqueidentifier,JSON_VALUE(app.value,'$.applicationId')),item.ItemId,@Date,
      TRY_CONVERT(decimal(28,4),JSON_VALUE(app.value,'$.amount'))
    FROM OPENJSON(@Applications) app CROSS APPLY(VALUES(TRY_CONVERT(uniqueidentifier,JSON_VALUE(app.value,'$.fundingItemId'))),
      (TRY_CONVERT(uniqueidentifier,JSON_VALUE(app.value,'$.debtItemId')))) item(ItemId);
  INSERT @Effects SELECT 'Replacement',TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.sourceId')),
    TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.itemId')),TRY_CONVERT(date,JSON_VALUE(value,'$.postingDate'),23),
    -TRY_CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')) FROM OPENJSON(@Targets);

  DECLARE @Reapply TABLE(Position int PRIMARY KEY,FundingId uniqueidentifier,DebtId uniqueidentifier,Amount decimal(28,4),PostingDate date);
  INSERT @Reapply SELECT CONVERT(int,[key]),CONVERT(uniqueidentifier,JSON_VALUE(value,'$.fundingItemId')),
    CONVERT(uniqueidentifier,JSON_VALUE(value,'$.debtItemId')),CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')),@Date
    FROM OPENJSON(@Canonical,'$.reapplications');
  IF EXISTS(SELECT 1 FROM @Reapply requested WHERE requested.FundingId=requested.DebtId OR requested.Amount<>ROUND(requested.Amount,@Scale,1)
    OR NOT EXISTS(SELECT 1 FROM Purchasing.SupplierOpenItems funding JOIN Purchasing.SupplierOpenItems debt ON debt.TenantId=funding.TenantId
      AND debt.Id=requested.DebtId CROSS APPLY Purchasing.SupplierAssetControl(@TenantId,funding.Id) fundingControl
      CROSS APPLY Purchasing.SupplierItemControl(@TenantId,debt.Id) debtControl
      WHERE funding.TenantId=@TenantId AND funding.Id=requested.FundingId AND funding.Kind IN('Advance','CreditReceivable')
        AND debt.Kind IN('Payable','RefundClearing') AND funding.SupplierId=@Supplier AND debt.SupplierId=@Supplier
        AND funding.PurchaseOrderId=@Po AND debt.PurchaseOrderId=@Po AND funding.Currency=@Currency AND debt.Currency=@Currency
        AND funding.SourcePostingDate<=@Date AND debt.SourcePostingDate<=@Date))
    THROW 51004,'Explicit reapplications require genuine compatible current source controls.',1;
  INSERT @Effects SELECT 'Application',requested.FundingId,item.ItemId,@Date,-requested.Amount FROM @Reapply requested
    CROSS APPLY(VALUES(requested.FundingId),(requested.DebtId)) item(ItemId);
  IF EXISTS(SELECT 1 FROM @Effects WHERE PostingDate<TRY_CONVERT(date,JSON_VALUE(@Policy,'$.policies.plannedStartDate'),23))
    THROW 51004,'A cash correction cannot predate accounting start.',1;
  IF EXISTS(SELECT 1 FROM @Effects effect JOIN Accounting.PeriodClosures closed ON closed.TenantId=@TenantId
    AND closed.PeriodStart=DATEFROMPARTS(YEAR(effect.PostingDate),MONTH(effect.PostingDate),1))
    THROW 51009,'A cash correction effect uses a closed posting period.',1;
  DECLARE @Dependencies TABLE(Kind varchar(40),Id uniqueidentifier,Version varchar(66),ParentId uniqueidentifier);
  INSERT @Dependencies SELECT 'Item',version.ItemId,CONVERT(varchar(18),CONVERT(binary(8),version.RowVersion),1),NULL
    FROM Purchasing.SupplierItemVersions version WITH(UPDLOCK,HOLDLOCK) WHERE version.TenantId=@TenantId
      AND EXISTS(SELECT 1 FROM @Effects effect WHERE effect.ItemId=version.ItemId);
  IF (SELECT COUNT(*) FROM @Dependencies)<>(SELECT COUNT(DISTINCT ItemId) FROM @Effects)
    THROW 51004,'The complete cash item version set is unavailable.',1;
  INSERT @Dependencies SELECT 'CashSource',TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.sourceId')),JSON_VALUE(value,'$.version'),NULL FROM OPENJSON(@Sources);
  INSERT @Dependencies SELECT 'CashSnapshot',TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.sourceId')),JSON_VALUE(value,'$.snapshotSha256'),
    TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.journalId')) FROM OPENJSON(@Sources);
  INSERT @Dependencies SELECT 'Application',TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.applicationId')),JSON_VALUE(value,'$.version'),
    TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.fundingItemId')) FROM OPENJSON(@Applications);
  INSERT @Dependencies SELECT 'Release',TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.settlementId')),JSON_VALUE(value,'$.snapshotSha256'),
    TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.journalId')) FROM OPENJSON(@Releases);
  INSERT @Dependencies SELECT 'SuccessorEvidence',TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.sourceId')),JSON_VALUE(value,'$.documentsSha256'),NULL FROM OPENJSON(@EvidenceBindings);
  EXEC Purchasing.PlanSupplierCashCorrectionEvidence @TenantId,@Sources,@Evidence OUTPUT;
  INSERT @Dependencies SELECT Kind,Id,Version,ParentId FROM OPENJSON(@Evidence)
    WITH(Kind varchar(40) '$.kind',Id uniqueidentifier '$.id',Version varchar(66) '$.version',ParentId uniqueidentifier '$.parentId');
  IF (SELECT COUNT(*) FROM @Dependencies)+(SELECT COUNT(*) FROM @Effects)+(SELECT COUNT(*) FROM OPENJSON(@Targets))>1000
    THROW 51000,'The complete cash correction closure exceeds its event bound.',1;
  DECLARE @Movements nvarchar(max)=(SELECT ItemId itemId,@Root groupId,PostingDate postingDate,CONVERT(nvarchar(60),Amount) amount FROM @Effects FOR JSON PATH);
  EXEC Purchasing.AssertSupplierAvailability @TenantId,@Movements;
  SET @Plan=(SELECT 1 schemaVersion,@Operation operation,@Root sourceId,JSON_QUERY(@Canonical) command,JSON_QUERY(@Sources) sources,
    JSON_QUERY(@Releases) reverseReleases,JSON_QUERY(@Applications) applications,JSON_QUERY(@Targets) replacementTargets,
    JSON_QUERY((SELECT Kind kind,Id id,Version version,ParentId parentId FROM @Dependencies ORDER BY Kind,Id,ParentId,Version FOR JSON PATH,INCLUDE_NULL_VALUES)) dependencies,
    JSON_QUERY((SELECT Kind kind,EntityId entityId,ItemId itemId,PostingDate postingDate,CONVERT(nvarchar(60),Amount) amount
      FROM @Effects ORDER BY PostingDate,Kind,EntityId,ItemId FOR JSON PATH)) effects,
    JSON_QUERY((SELECT FundingId fundingItemId,DebtId debtItemId,CONVERT(nvarchar(60),Amount) amount,PostingDate postingDate
      FROM @Reapply ORDER BY Position FOR JSON PATH)) reapplications FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  IF DATALENGTH(@Plan)>262144 THROW 51000,'The complete cash correction plan exceeds its byte bound.',1;
  SET @Plan=JSON_MODIFY(@Plan,'$.planFingerprint',CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Plan)),2));
END;
