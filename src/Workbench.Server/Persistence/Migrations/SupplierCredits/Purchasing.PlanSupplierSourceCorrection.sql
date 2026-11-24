-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PlanSupplierSourceCorrection
  @TenantId uniqueidentifier,@Operation nvarchar(40),@Command nvarchar(max),@Plan nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId IS NULL OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Source correction planning requires its owner transaction.',1;
  DECLARE @Canonical nvarchar(max);
  EXEC Purchasing.CanonicalizeSupplierSourceCorrection @Operation,@Command,@Canonical OUTPUT;
  SET @Canonical=JSON_MODIFY(@Canonical,'$.expectedPlanFingerprint',NULL);
  IF @Operation<>'CorrectSupplierCredit' THROW 51004,'The requested source correction participant is unavailable.',1;
  DECLARE @Root uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.sourceId')),
    @Po uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
    @Supplier uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),
    @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')),
    @Currency varchar(3)=JSON_VALUE(@Canonical,'$.currency'),@Date date=CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23),
    @Policy nvarchar(max),@Scale int,@Sources nvarchar(max);
  IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@Po
    AND State='Ordered' AND IsDeleted=0 AND SupplierId=@Supplier AND Currency=@Currency
    AND RowVersion=CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'),1))
    THROW 51009,'Purchase revision changed.',1;
  SELECT @Policy=Payload FROM Accounting.Configurations WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Version=@Config;
  IF @Policy IS NULL THROW 51009,'Accounting configuration changed.',1;
  SET @Scale=TRY_CONVERT(int,JSON_VALUE(@Policy,'$.policies.scale'));
  IF @Scale IS NULL OR @Scale NOT BETWEEN 0 AND 4 OR JSON_VALUE(@Policy,'$.policies.currency') COLLATE Latin1_General_100_BIN2<>@Currency
    THROW 51000,'Correction currency or precision is invalid.',1;
  EXEC Purchasing.BuildSupplierCreditCorrectionSources @TenantId,@Canonical,@Sources OUTPUT;
  DECLARE @Source TABLE(Id uniqueidentifier PRIMARY KEY,RevisionId uniqueidentifier,Version varchar(18),Amount decimal(28,4),
    PostingDate date,JournalId uniqueidentifier,Replacement nvarchar(max),ReplacementId uniqueidentifier);
  INSERT @Source SELECT Id,RevisionId,Version,Amount,PostingDate,JournalId,Replacement,ReplacementId
    FROM OPENJSON(@Sources) WITH(Id uniqueidentifier '$.sourceId',RevisionId uniqueidentifier '$.sourceRevisionId',Version varchar(18) '$.version',
      Amount decimal(28,4) '$.amount',PostingDate date '$.postingDate',JournalId uniqueidentifier '$.journalId',
      Replacement nvarchar(max) '$.replacement' AS JSON,ReplacementId uniqueidentifier '$.replacementId');
  DECLARE @Applications TABLE(Id uniqueidentifier PRIMARY KEY,FundingId uniqueidentifier,DebtId uniqueidentifier,Amount decimal(28,4),
    PostingDate date,Version varchar(18),JournalId uniqueidentifier);
  INSERT @Applications
    SELECT app.Id,app.FundingItemId,app.DebtItemId,app.Amount,
      CASE WHEN app.PostingDate>source.PostingDate THEN app.PostingDate ELSE source.PostingDate END,
      CONVERT(varchar(18),CONVERT(binary(8),version.RowVersion),1),control.JournalId
    FROM Purchasing.SupplierApplications app JOIN @Source source ON source.Id=app.FundingItemId
    JOIN Purchasing.SupplierApplicationVersions version WITH(UPDLOCK,HOLDLOCK) ON version.TenantId=app.TenantId AND version.ApplicationId=app.Id
    CROSS APPLY Purchasing.SupplierApplicationControl(@TenantId,app.Id) control
    WHERE app.TenantId=@TenantId AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierApplicationReversals reversed
      WHERE reversed.TenantId=app.TenantId AND reversed.ApplicationId=app.Id);
  IF (SELECT COUNT(*) FROM @Applications)<>(SELECT COUNT(*) FROM Purchasing.SupplierApplications app JOIN @Source source ON source.Id=app.FundingItemId
    WHERE app.TenantId=@TenantId AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierApplicationReversals reversed WHERE reversed.TenantId=app.TenantId AND reversed.ApplicationId=app.Id))
    THROW 51004,'An allocation has an unsupported or incomplete owner.',1;
  DECLARE @Settlements TABLE(Id uniqueidentifier PRIMARY KEY,FundingId uniqueidentifier,CashSourceId uniqueidentifier,
    Amount decimal(28,4),PostingDate date,CashVersion varchar(18),JournalId uniqueidentifier);
  INSERT @Settlements
    SELECT settlement.Id,settlement.ItemId,cash.Id,settlement.Amount,
      CASE WHEN cash.PostingDate>source.PostingDate THEN cash.PostingDate ELSE source.PostingDate END,
      CONVERT(varchar(18),CONVERT(binary(8),version.RowVersion),1),control.JournalId
    FROM Purchasing.SupplierCashSettlements settlement JOIN @Source source ON source.Id=settlement.ItemId
    JOIN Purchasing.SupplierCashSources cash ON cash.TenantId=settlement.TenantId AND cash.Id=settlement.CashSourceId AND cash.Kind='SupplierRefund'
    JOIN Purchasing.SupplierCashVersions version WITH(UPDLOCK,HOLDLOCK) ON version.TenantId=cash.TenantId AND version.CashSourceId=cash.Id
    CROSS APPLY Purchasing.SupplierCashControl(@TenantId,cash.Id) control
    WHERE settlement.TenantId=@TenantId AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierCashSettlementReleases released
      WHERE released.TenantId=settlement.TenantId AND released.SettlementId=settlement.Id)
      AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrections corrected WHERE corrected.TenantId=cash.TenantId AND corrected.SourceKind=cash.Kind AND corrected.SourceId=cash.Id);
  IF (SELECT COUNT(*) FROM @Source)+2*(SELECT COUNT(*) FROM @Applications)+3*(SELECT COUNT(*) FROM @Settlements)
      +(SELECT COUNT(*) FROM OPENJSON(@Canonical,'$.reapplications'))>1000
    THROW 51000,'The complete source correction closure exceeds its event bound.',1;
  DECLARE @Reapply TABLE(Position int PRIMARY KEY,FundingId uniqueidentifier,DebtId uniqueidentifier,Amount decimal(28,4),PostingDate date);
  INSERT @Reapply SELECT CONVERT(int,[key]),CONVERT(uniqueidentifier,JSON_VALUE(value,'$.fundingItemId')),
    CONVERT(uniqueidentifier,JSON_VALUE(value,'$.debtItemId')),CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')),@Date
    FROM OPENJSON(@Canonical,'$.reapplications');
  INSERT @Reapply SELECT (SELECT COUNT(*) FROM OPENJSON(@Canonical,'$.reapplications'))
      +ROW_NUMBER() OVER(ORDER BY JSON_VALUE(source.value,'$.sourceId'),CONVERT(int,target.[key]))-1,
    CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.replacementId')),
    CONVERT(uniqueidentifier,JSON_VALUE(target.value,'$.itemId')),CONVERT(decimal(28,4),JSON_VALUE(target.value,'$.amount')),
    CONVERT(date,JSON_VALUE(source.value,'$.replacement.postingDate'),23)
    FROM OPENJSON(@Sources) source CROSS APPLY OPENJSON(source.value,'$.requestedAllocations') target;
  IF EXISTS(SELECT 1 FROM OPENJSON(@Sources) source CROSS APPLY OPENJSON(source.value,'$.requestedAllocations') target
    WHERE NOT EXISTS(SELECT 1 FROM Purchasing.SupplierItemVersions version WITH(UPDLOCK,HOLDLOCK)
      WHERE version.TenantId=@TenantId AND version.ItemId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(target.value,'$.itemId'))
        AND version.RowVersion=TRY_CONVERT(binary(8),JSON_VALUE(target.value,'$.expectedItemVersion'),1)))
    THROW 51009,'Replacement allocation observation changed.',1;
  IF EXISTS(SELECT 1 FROM OPENJSON(@Sources) source CROSS APPLY OPENJSON(source.value,'$.requestedAllocations') target
    WHERE NOT EXISTS(SELECT 1 FROM Purchasing.SupplierOpenItems item JOIN Purchasing.SupplierBillPostings bill
      ON bill.TenantId=item.TenantId AND bill.BillId=item.BillId AND bill.RevisionId=item.SourceRevisionId
      WHERE item.TenantId=@TenantId AND item.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(target.value,'$.itemId'))
        AND item.BillId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(target.value,'$.billId')) AND item.Kind='Payable'
        AND item.SupplierId=@Supplier AND item.PurchaseOrderId=@Po AND item.Currency=@Currency))
    THROW 51004,'Replacement allocations require their authentic posted bills.',1;
  IF EXISTS(SELECT 1 FROM @Reapply WHERE Amount<>ROUND(Amount,@Scale,1))
    THROW 51000,'Reapplication precision is invalid.',1;
  IF EXISTS(SELECT 1 FROM @Reapply reapply WHERE NOT EXISTS(SELECT 1 FROM @Source source WHERE source.ReplacementId=reapply.FundingId)
      AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierOpenItems item WHERE item.TenantId=@TenantId AND item.Id=reapply.FundingId
        AND item.Kind IN('Advance','CreditReceivable') AND item.SupplierId=@Supplier AND item.PurchaseOrderId=@Po AND item.Currency=@Currency))
    OR EXISTS(SELECT 1 FROM @Reapply reapply WHERE NOT EXISTS(SELECT 1 FROM @Settlements released WHERE released.Id=reapply.DebtId)
      AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierOpenItems item WHERE item.TenantId=@TenantId AND item.Id=reapply.DebtId
        AND item.Kind IN('Payable','RefundClearing') AND item.SupplierId=@Supplier AND item.PurchaseOrderId=@Po AND item.Currency=@Currency))
    THROW 51004,'Explicit reapplication requires eligible source-scoped items.',1;
  UPDATE reapply SET PostingDate=(SELECT MAX(dateValue) FROM(
      SELECT @Date dateValue UNION ALL SELECT item.SourcePostingDate FROM Purchasing.SupplierOpenItems item
        WHERE item.TenantId=@TenantId AND item.Id IN(reapply.FundingId,reapply.DebtId)
      UNION ALL SELECT released.PostingDate FROM @Settlements released WHERE released.Id=reapply.DebtId
      UNION ALL SELECT CONVERT(date,JSON_VALUE(source.Replacement,'$.postingDate'),23) FROM @Source source WHERE source.ReplacementId=reapply.FundingId
    ) dates) FROM @Reapply reapply;
  DECLARE @Effects TABLE(Kind varchar(40),EntityId uniqueidentifier,ItemId uniqueidentifier,PostingDate date,Amount decimal(28,4));
  INSERT @Effects SELECT 'ReverseApplication',app.Id,item.ItemId,app.PostingDate,app.Amount FROM @Applications app
    CROSS APPLY(VALUES(app.FundingId),(app.DebtId)) item(ItemId);
  INSERT @Effects SELECT 'ReleaseSettlement',settlement.Id,item.ItemId,settlement.PostingDate,settlement.Amount FROM @Settlements settlement
    CROSS APPLY(VALUES(settlement.FundingId),(settlement.Id)) item(ItemId);
  INSERT @Effects SELECT 'ReverseSource',Id,Id,PostingDate,-Amount FROM @Source;
  INSERT @Effects SELECT 'Replacement',ReplacementId,ReplacementId,CONVERT(date,JSON_VALUE(Replacement,'$.postingDate'),23),
    CONVERT(decimal(28,4),JSON_VALUE(Replacement,'$.total')) FROM @Source WHERE Replacement IS NOT NULL;
  INSERT @Effects SELECT 'Application',reapply.FundingId,item.ItemId,reapply.PostingDate,-reapply.Amount FROM @Reapply reapply
    CROSS APPLY(VALUES(reapply.FundingId),(reapply.DebtId)) item(ItemId);
  DECLARE @SourceCapacity nvarchar(max),@RecognitionAdjustments nvarchar(max);
  EXEC Purchasing.PlanSupplierCreditCapacity @TenantId,@Sources,@SourceCapacity OUTPUT,@RecognitionAdjustments OUTPUT;
  DECLARE @PositionBindings nvarchar(max);
  EXEC Purchasing.PlanSupplierCreditPositionBindings @TenantId,@Sources,@PositionBindings OUTPUT;
  INSERT @Effects SELECT 'RecognitionAdjustment',CONVERT(uniqueidentifier,JSON_VALUE(value,'$.recognitionEventId')),NULL,
    CONVERT(date,JSON_VALUE(value,'$.postingDate'),23),CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')) FROM OPENJSON(@RecognitionAdjustments);
  IF EXISTS(SELECT 1 FROM @Effects effect WHERE effect.PostingDate<TRY_CONVERT(date,JSON_VALUE(@Policy,'$.policies.plannedStartDate'),23))
    THROW 51004,'A correction effect predates accounting start.',1;
  IF EXISTS(SELECT 1 FROM @Effects effect JOIN Accounting.PeriodClosures closed ON closed.TenantId=@TenantId
    AND closed.PeriodStart=DATEFROMPARTS(YEAR(effect.PostingDate),MONTH(effect.PostingDate),1))
    THROW 51009,'A correction effect falls in a closed posting period.',1;
  DECLARE @Dependencies TABLE(Kind varchar(40),Id uniqueidentifier,Version varchar(66),ParentId uniqueidentifier,Amount decimal(28,4),PostingDate date);
  INSERT @Dependencies SELECT 'ProposedCreditPosition',TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.recognitionEventId')),
    CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),value)),2),
    TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.creditId')),NULL,TRY_CONVERT(date,JSON_VALUE(value,'$.postingDate'),23) FROM OPENJSON(@PositionBindings);
  INSERT @Dependencies SELECT 'RecognitionAdjustment',CONVERT(uniqueidentifier,JSON_VALUE(value,'$.recognitionEventId')),
    JSON_VALUE(value,'$.recognitionEvidenceSha256'),CONVERT(uniqueidentifier,JSON_VALUE(value,'$.invoiceEventId')),
    CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')),CONVERT(date,JSON_VALUE(value,'$.postingDate'),23) FROM OPENJSON(@RecognitionAdjustments);
  INSERT @Dependencies SELECT 'SupplierCredit',Id,Version,NULL,Amount,PostingDate FROM @Source;
  INSERT @Dependencies SELECT 'Application',Id,Version,FundingId,Amount,PostingDate FROM @Applications;
  INSERT @Dependencies SELECT 'CashSettlement',Id,NULL,CashSourceId,Amount,PostingDate FROM @Settlements;
  INSERT @Dependencies SELECT DISTINCT 'SupplierRefund',CashSourceId,CashVersion,NULL,NULL,NULL FROM @Settlements;
  INSERT @Dependencies SELECT DISTINCT 'Item',version.ItemId,CONVERT(varchar(18),CONVERT(binary(8),version.RowVersion),1),NULL,NULL,NULL
    FROM Purchasing.SupplierItemVersions version WITH(UPDLOCK,HOLDLOCK) WHERE version.TenantId=@TenantId
      AND EXISTS(SELECT 1 FROM @Effects effect WHERE effect.ItemId=version.ItemId);
  INSERT @Dependencies SELECT DISTINCT 'SupplierBill',bill.Id,CONVERT(varchar(18),CONVERT(binary(8),bill.RowVersion),1),NULL,NULL,NULL
    FROM @Source source JOIN Purchasing.SupplierCreditClaims claim ON claim.TenantId=@TenantId AND claim.CreditId=source.Id
    JOIN Purchasing.SupplierBills bill ON bill.TenantId=claim.TenantId AND bill.Id=claim.BillId;
  INSERT @Dependencies SELECT DISTINCT 'CapacityCredit',claim.CreditId,CONVERT(varchar(18),CONVERT(binary(8),version.RowVersion),1),NULL,NULL,NULL
    FROM Purchasing.SupplierCreditClaims claim JOIN Purchasing.SupplierCreditVersions version WITH(UPDLOCK,HOLDLOCK)
      ON version.TenantId=claim.TenantId AND version.CreditId=claim.CreditId
    WHERE claim.TenantId=@TenantId AND (EXISTS(SELECT 1 FROM Purchasing.SupplierCreditClaims affected JOIN @Source source ON source.Id=affected.CreditId
        WHERE affected.TenantId=claim.TenantId AND affected.RecognitionEventId=claim.RecognitionEventId)
      OR EXISTS(SELECT 1 FROM @Source source CROSS APPLY OPENJSON(source.Replacement,'$.claims') proposed
        WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(proposed.value,'$.recognitionEventId'))=claim.RecognitionEventId));
  INSERT @Dependencies SELECT DISTINCT 'Account',account.Id,CONVERT(varchar(36),account.Version),NULL,NULL,NULL
    FROM OPENJSON(@Policy,'$.mappings') mapping JOIN Accounting.Accounts account WITH(UPDLOCK,HOLDLOCK)
      ON account.TenantId=@TenantId AND account.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(mapping.value,'$.accountId'))
    WHERE JSON_VALUE(mapping.value,'$.slot') IN('SupplierCreditReceivable','SupplierRefundClearing','Expense');
  DECLARE @EvidenceDependencies nvarchar(max);
  EXEC Purchasing.PlanSupplierCreditEvidence @TenantId,@Sources,@EvidenceDependencies OUTPUT;
  INSERT @Dependencies SELECT Kind,Id,Version,ParentId,NULL,NULL FROM OPENJSON(@EvidenceDependencies)
    WITH(Kind varchar(40) '$.kind',Id uniqueidentifier '$.id',Version varchar(66) '$.version',ParentId uniqueidentifier '$.parentId');
  IF (SELECT COUNT(*) FROM @Dependencies)+(SELECT COUNT(*) FROM @Effects)>1000
    THROW 51000,'The complete source correction closure exceeds its event bound.',1;
  DECLARE @Movements nvarchar(max)=(SELECT ItemId itemId,@Root groupId,PostingDate postingDate,CONVERT(nvarchar(60),Amount) amount FROM @Effects WHERE ItemId IS NOT NULL FOR JSON PATH);
  EXEC Purchasing.AssertSupplierAvailability @TenantId,@Movements;
  SET @Plan=(SELECT 1 schemaVersion,@Operation operation,@Root sourceId,JSON_QUERY(@Canonical) command,
    JSON_QUERY(@Sources) sources,
    JSON_QUERY((SELECT Kind kind,Id id,Version version,ParentId parentId,CONVERT(nvarchar(60),Amount) amount,PostingDate postingDate
      FROM @Dependencies ORDER BY Kind,Id,ParentId,Version,PostingDate,Amount FOR JSON PATH,INCLUDE_NULL_VALUES)) dependencies,
    JSON_QUERY((SELECT Kind kind,EntityId entityId,ItemId itemId,PostingDate postingDate,CONVERT(nvarchar(60),Amount) amount
      FROM @Effects ORDER BY PostingDate,Kind,EntityId,ItemId,Amount FOR JSON PATH)) effects,
    JSON_QUERY((SELECT Id applicationId,FundingId fundingItemId,DebtId debtItemId,CONVERT(nvarchar(60),Amount) amount,
      PostingDate postingDate,Version version,JournalId journalId FROM @Applications ORDER BY Id FOR JSON PATH)) applications,
    JSON_QUERY((SELECT Id settlementId,FundingId fundingItemId,CashSourceId cashSourceId,CONVERT(nvarchar(60),Amount) amount,
      PostingDate postingDate,CashVersion cashVersion,JournalId journalId FROM @Settlements ORDER BY Id FOR JSON PATH)) settlements,
    JSON_QUERY((SELECT FundingId fundingItemId,DebtId debtItemId,CONVERT(nvarchar(60),Amount) amount,PostingDate postingDate FROM @Reapply ORDER BY Position FOR JSON PATH)) reapplications,
    JSON_QUERY((SELECT ItemId itemId,PostingDate postingDate,CONVERT(nvarchar(60),Balance) balance
      FROM Purchasing.SupplierProposedOpenPositions(@TenantId,@Movements) ORDER BY ItemId,PostingDate FOR JSON PATH)) remainingPositions,
    JSON_QUERY(@SourceCapacity) componentPositions,JSON_QUERY(@RecognitionAdjustments) recognitionAdjustments
    FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  IF DATALENGTH(@Plan)>262144 THROW 51000,'Complete correction plan exceeds its byte bound.',1;
  SET @Plan=JSON_MODIFY(@Plan,'$.planFingerprint',CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Plan)),2));
END;
