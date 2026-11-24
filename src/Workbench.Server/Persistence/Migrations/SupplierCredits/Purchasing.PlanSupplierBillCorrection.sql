-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PlanSupplierBillCorrection
  @TenantId uniqueidentifier,@ActorId uniqueidentifier,@SessionId uniqueidentifier,@Command nvarchar(max),@Plan nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Bill planning requires its owner transaction.',1;
  DECLARE @Canonical nvarchar(max);
  EXEC Purchasing.CanonicalizeSupplierSourceCorrection N'CorrectSupplierBill',@Command,@Canonical OUTPUT;
  SET @Canonical=JSON_MODIFY(@Canonical,'$.expectedPlanFingerprint',NULL);
  DECLARE @Bill uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.sourceId')),
    @Po uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
    @Supplier uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),
    @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion')),
    @Currency varchar(3)=JSON_VALUE(@Canonical,'$.currency'),@Date date=CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23),
    @Policy nvarchar(max),@Scale int,@Source nvarchar(max),@Units nvarchar(max);
  IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@Po
    AND State='Ordered' AND IsDeleted=0 AND SupplierId=@Supplier AND Currency=@Currency
    AND RowVersion=CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion'),1))
    THROW 51009,'Purchase revision changed.',1;
  SELECT @Policy=Payload FROM Accounting.Configurations WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Version=@Config;
  IF @Policy IS NULL THROW 51009,'Accounting configuration changed.',1;
  SET @Scale=TRY_CONVERT(int,JSON_VALUE(@Policy,'$.policies.scale'));
  IF @Scale IS NULL OR @Scale NOT BETWEEN 0 AND 4 OR JSON_VALUE(@Policy,'$.policies.currency') COLLATE Latin1_General_100_BIN2<>@Currency
    THROW 51000,'Correction currency or precision is invalid.',1;
  EXEC Purchasing.BuildSupplierBillCorrectionSource @TenantId,@Canonical,@Source OUTPUT,@Units OUTPUT;
  DECLARE @ReverseAdjustments nvarchar(max);
  EXEC Purchasing.PlanBillRecognitionAdjustments @TenantId,@Units,@Date,@ReverseAdjustments OUTPUT;
  DECLARE @Credits nvarchar(max),@Positions nvarchar(max),@CreditAdjustments nvarchar(max),@Sources nvarchar(max);
  EXEC Purchasing.BuildBillDependentCreditSources @TenantId,@Canonical,@Units,@Credits OUTPUT,@Positions OUTPUT,@CreditAdjustments OUTPUT;
  DECLARE @SourceOwners nvarchar(max),@RecognitionSuccessors nvarchar(max);
  EXEC Purchasing.PlanBillRecognitionSources @TenantId,@ActorId,@SessionId,@Canonical,@Units,@SourceOwners OUTPUT,@RecognitionSuccessors OUTPUT;
  EXEC Purchasing.ResolveBillCreditSuccessorIntents @TenantId,@Canonical,@Credits,@Credits OUTPUT;
  DECLARE @CreditPositionBindings nvarchar(max);
  EXEC Purchasing.ProjectBillCreditCapacity @TenantId,@Canonical,@Units,@Credits,@Positions OUTPUT,@CreditAdjustments OUTPUT,@CreditPositionBindings OUTPUT;
  SET @Sources=N'['+@Source+CASE WHEN @Credits=N'[]' THEN N'' ELSE N','+SUBSTRING(@Credits,2,LEN(@Credits)-2) END+N']';
  IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.sourceDecisions') WHERE JSON_VALUE(value,'$.sourceKind') NOT IN('PurchaseRecognition','SupplierCredit'))
    THROW 51004,'An unsupported source decision cannot be silently omitted.',1;
  DECLARE @Successor uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Source,'$.replacementId'));
  DECLARE @Applications TABLE(Id uniqueidentifier PRIMARY KEY,FundingId uniqueidentifier,DebtId uniqueidentifier,
    Amount decimal(28,4),PostingDate date,Version varchar(18),JournalId uniqueidentifier,Embedded bit);
  INSERT @Applications SELECT app.Id,app.FundingItemId,app.DebtItemId,app.Amount,@Date,
      CONVERT(varchar(18),CONVERT(binary(8),version.RowVersion),1),control.JournalId,
      CASE WHEN payment.Id IS NULL THEN 0 ELSE 1 END
    FROM Purchasing.SupplierApplications app
    JOIN Purchasing.SupplierApplicationVersions version WITH(UPDLOCK,HOLDLOCK) ON version.TenantId=app.TenantId AND version.ApplicationId=app.Id
    CROSS APPLY Purchasing.SupplierApplicationControl(@TenantId,app.Id) control
    LEFT JOIN Purchasing.SupplierPayments payment ON payment.TenantId=app.TenantId AND payment.Id=app.FundingItemId AND payment.GroupId=app.GroupId
    WHERE app.TenantId=@TenantId AND (app.DebtItemId=@Bill OR EXISTS(SELECT 1 FROM OPENJSON(@Credits) credit WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(credit.value,'$.sourceId'))=app.FundingItemId))
      AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierApplicationReversals reversed WHERE reversed.TenantId=app.TenantId AND reversed.ApplicationId=app.Id)
      AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierPaymentCorrections corrected WHERE corrected.TenantId=app.TenantId AND corrected.OriginalPaymentId=app.FundingItemId);
  IF (SELECT COUNT(*) FROM @Applications)<>(SELECT COUNT(*) FROM Purchasing.SupplierApplications app
    WHERE app.TenantId=@TenantId AND (app.DebtItemId=@Bill OR EXISTS(SELECT 1 FROM OPENJSON(@Credits) credit WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(credit.value,'$.sourceId'))=app.FundingItemId))
      AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierApplicationReversals reversed WHERE reversed.TenantId=app.TenantId AND reversed.ApplicationId=app.Id)
      AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierPaymentCorrections corrected WHERE corrected.TenantId=app.TenantId AND corrected.OriginalPaymentId=app.FundingItemId))
    OR EXISTS(SELECT 1 FROM Purchasing.SupplierApplications app JOIN @Applications affected ON affected.Id=app.Id
      WHERE app.TenantId=@TenantId AND app.PostingDate>@Date)
    THROW 51004,'An affected allocation has an unsupported or later owner.',1;
  DECLARE @Reapply TABLE(Position int PRIMARY KEY,FundingId uniqueidentifier,DebtId uniqueidentifier,Amount decimal(28,4),PostingDate date);
  DECLARE @ReapplicationInput nvarchar(max);
  EXEC Purchasing.PlanBillReapplications @TenantId,@Canonical,@Sources,@ReapplicationInput OUTPUT;
  INSERT @Reapply SELECT CONVERT(int,[key]),TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.fundingItemId')),
    TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.debtItemId')),TRY_CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')),
    TRY_CONVERT(date,JSON_VALUE(value,'$.postingDate'),23) FROM OPENJSON(@ReapplicationInput);
  DECLARE @Effects TABLE(Kind varchar(40),EntityId uniqueidentifier,ItemId uniqueidentifier,PostingDate date,Amount decimal(28,4));
  DECLARE @Settlements TABLE(Id uniqueidentifier PRIMARY KEY,FundingId uniqueidentifier,CashSourceId uniqueidentifier,
    Amount decimal(28,4),PostingDate date,CashVersion varchar(18),JournalId uniqueidentifier);
  INSERT @Settlements SELECT settlement.Id,settlement.ItemId,cash.Id,settlement.Amount,@Date,
      CONVERT(varchar(18),CONVERT(binary(8),version.RowVersion),1),control.JournalId
    FROM Purchasing.SupplierCashSettlements settlement JOIN OPENJSON(@Credits) credit ON TRY_CONVERT(uniqueidentifier,JSON_VALUE(credit.value,'$.sourceId'))=settlement.ItemId
    JOIN Purchasing.SupplierCashSources cash ON cash.TenantId=settlement.TenantId AND cash.Id=settlement.CashSourceId AND cash.Kind='SupplierRefund'
    JOIN Purchasing.SupplierCashVersions version WITH(UPDLOCK,HOLDLOCK) ON version.TenantId=cash.TenantId AND version.CashSourceId=cash.Id
    CROSS APPLY Purchasing.SupplierCashControl(@TenantId,cash.Id) control
    WHERE settlement.TenantId=@TenantId AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierCashSettlementReleases released
      WHERE released.TenantId=settlement.TenantId AND released.SettlementId=settlement.Id)
      AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrections corrected WHERE corrected.TenantId=cash.TenantId AND corrected.SourceKind=cash.Kind AND corrected.SourceId=cash.Id);
  IF EXISTS(SELECT 1 FROM @Settlements settlement JOIN Purchasing.SupplierCashSources cash ON cash.TenantId=@TenantId AND cash.Id=settlement.CashSourceId WHERE cash.PostingDate>@Date)
    THROW 51009,'The correction cannot precede a dependent actual refund.',1;
  INSERT @Effects SELECT 'ReleaseSettlement',settlement.Id,item.ItemId,@Date,settlement.Amount FROM @Settlements settlement
    CROSS APPLY(VALUES(settlement.FundingId),(settlement.Id)) item(ItemId);
  INSERT @Effects SELECT 'ReverseApplication',app.Id,item.ItemId,@Date,app.Amount FROM @Applications app
    CROSS APPLY(VALUES(app.FundingId),(app.DebtId)) item(ItemId);
  INSERT @Effects VALUES('ReverseSource',@Bill,@Bill,@Date,-CONVERT(decimal(28,4),JSON_VALUE(@Source,'$.amount')));
  INSERT @Effects SELECT 'ReverseSource',TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.sourceId')),TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.sourceId')),
    @Date,-TRY_CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')) FROM OPENJSON(@Credits);
  INSERT @Effects SELECT 'Replacement',TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.replacementId')),TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.replacementId')),
    TRY_CONVERT(date,JSON_VALUE(value,'$.replacement.postingDate'),23),TRY_CONVERT(decimal(28,4),JSON_VALUE(value,'$.replacement.total'))
    FROM OPENJSON(@Credits) WHERE JSON_QUERY(value,'$.replacement') IS NOT NULL;
  INSERT @Effects SELECT 'RecognitionAdjustment',TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.recognitionEventId')),NULL,
    TRY_CONVERT(date,JSON_VALUE(value,'$.postingDate'),23),TRY_CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')) FROM OPENJSON(@CreditAdjustments);
  INSERT @Effects SELECT 'ReverseRecognitionAdjustment',TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.journalId')),NULL,
    @Date,-TRY_CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')) FROM OPENJSON(@ReverseAdjustments);
  IF @Successor IS NOT NULL INSERT @Effects VALUES('Replacement',@Successor,@Successor,@Date,CONVERT(decimal(28,4),JSON_VALUE(@Source,'$.replacementAmount')));
  INSERT @Effects SELECT 'Application',target.FundingId,item.ItemId,target.PostingDate,-target.Amount FROM @Reapply target
    CROSS APPLY(VALUES(target.FundingId),(target.DebtId)) item(ItemId);
  IF EXISTS(SELECT 1 FROM @Effects WHERE PostingDate<TRY_CONVERT(date,JSON_VALUE(@Policy,'$.policies.plannedStartDate'),23))
    THROW 51004,'A bill correction cannot predate accounting start.',1;
  IF EXISTS(SELECT 1 FROM @Effects effect JOIN Accounting.PeriodClosures closed ON closed.TenantId=@TenantId AND closed.PeriodStart=DATEFROMPARTS(YEAR(effect.PostingDate),MONTH(effect.PostingDate),1))
    THROW 51009,'The correction posting period is closed.',1;
  DECLARE @Dependencies TABLE(Kind varchar(40),Id uniqueidentifier,Version varchar(66),ParentId uniqueidentifier);
  INSERT @Dependencies SELECT 'Item',version.ItemId,CONVERT(varchar(18),CONVERT(binary(8),version.RowVersion),1),NULL
    FROM Purchasing.SupplierItemVersions version WITH(UPDLOCK,HOLDLOCK) WHERE version.TenantId=@TenantId
      AND EXISTS(SELECT 1 FROM @Effects effect WHERE effect.ItemId=version.ItemId);
  INSERT @Dependencies SELECT 'Application',Id,Version,FundingId FROM @Applications;
  INSERT @Dependencies SELECT 'SupplierCredit',TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.sourceId')),JSON_VALUE(value,'$.version'),NULL FROM OPENJSON(@Credits);
  INSERT @Dependencies SELECT 'CashSettlement',Id,NULL,CashSourceId FROM @Settlements;
  INSERT @Dependencies SELECT DISTINCT 'SupplierRefund',CashSourceId,CashVersion,NULL FROM @Settlements;
  INSERT @Dependencies SELECT 'HistoricalRecognitionAdjustment',TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.journalId')),
    JSON_VALUE(value,'$.snapshotSha256'),TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.creditId')) FROM OPENJSON(@ReverseAdjustments);
  INSERT @Dependencies SELECT DISTINCT 'AdjustmentCredit',TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.creditId')),
    JSON_VALUE(value,'$.creditVersion'),NULL FROM OPENJSON(@ReverseAdjustments);
  INSERT @Dependencies SELECT 'RecognitionEvent',TRY_CONVERT(uniqueidentifier,JSON_VALUE(event.value,'$.eventId')),
      JSON_VALUE(event.value,'$.evidenceSha256'),TRY_CONVERT(uniqueidentifier,JSON_VALUE(unit.value,'$.unitId'))
    FROM OPENJSON(@Units) unit CROSS APPLY OPENJSON(unit.value,'$.originals') event;
  INSERT @Dependencies SELECT 'Account',account.Id,CONVERT(varchar(36),account.Version),NULL
    FROM OPENJSON(@Policy,'$.mappings') mapping JOIN Accounting.Accounts account WITH(UPDLOCK,HOLDLOCK)
      ON account.TenantId=@TenantId AND account.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(mapping.value,'$.accountId'));
  DECLARE @EvidenceSet uniqueidentifier,@EvidenceParent uniqueidentifier,@EvidenceVersion varchar(18);
  SELECT @EvidenceSet=evidence.Id FROM Accounting.FinancialEvidenceSets evidence WITH(UPDLOCK,HOLDLOCK)
    CROSS APPLY Accounting.FinancialEvidenceSource(@TenantId,evidence.OwnerKind,evidence.OwnerId,evidence.OwnerRevisionId) owner
    WHERE evidence.TenantId=@TenantId AND evidence.OwnerKind='SupplierBill' AND evidence.OwnerId=@Bill
      AND evidence.OwnerRevisionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Source,'$.sourceRevisionId'))
      AND evidence.SourceSnapshotSha256=owner.SourceSnapshotSha256 AND evidence.ActorId=owner.ActorId
      AND evidence.PurchaseOrderId=@Po AND evidence.SupplierId=@Supplier AND evidence.RecordedAtUtc=owner.RecordedAtUtc;
  IF @EvidenceSet IS NULL THROW 51004,'The authentic bill evidence set is unavailable.',1;
  WHILE @EvidenceSet IS NOT NULL
  BEGIN
    IF EXISTS(SELECT 1 FROM @Dependencies WHERE Kind='FinancialEvidenceSet' AND Id=@EvidenceSet)
      THROW 51004,'Inherited bill evidence has a cycle.',1;
    SET @EvidenceVersion=NULL; SET @EvidenceParent=NULL;
    SELECT @EvidenceVersion=CONVERT(varchar(18),CONVERT(binary(8),evidence.RowVersion),1),@EvidenceParent=evidence.InheritedEvidenceSetId
      FROM Accounting.FinancialEvidenceSets evidence WITH(UPDLOCK,HOLDLOCK)
      CROSS APPLY Accounting.FinancialEvidenceSource(@TenantId,evidence.OwnerKind,evidence.OwnerId,evidence.OwnerRevisionId) owner
      WHERE evidence.TenantId=@TenantId AND evidence.Id=@EvidenceSet AND evidence.OwnerKind='SupplierBill'
        AND evidence.PurchaseOrderId=@Po AND evidence.SupplierId=@Supplier AND owner.PurchaseOrderId=@Po AND owner.SupplierId=@Supplier
        AND evidence.SourceSnapshotSha256=owner.SourceSnapshotSha256 AND evidence.ActorId=owner.ActorId AND evidence.RecordedAtUtc=owner.RecordedAtUtc;
    IF @EvidenceVersion IS NULL THROW 51004,'An inherited bill evidence owner is unavailable.',1;
    INSERT @Dependencies VALUES('FinancialEvidenceSet',@EvidenceSet,@EvidenceVersion,@EvidenceParent);
    INSERT @Dependencies SELECT 'FinancialEvidenceLink',Id,Sha256,EvidenceSetId FROM Accounting.FinancialEvidenceLinks WHERE TenantId=@TenantId AND EvidenceSetId=@EvidenceSet;
    INSERT @Dependencies SELECT 'FinancialEvidenceAddition',Id,NULL,EvidenceSetId FROM Accounting.FinancialEvidenceAdditions WHERE TenantId=@TenantId AND EvidenceSetId=@EvidenceSet;
    IF (SELECT COUNT(*) FROM @Dependencies)>1000 THROW 51000,'The complete bill correction closure exceeds its event bound.',1;
    SET @EvidenceSet=@EvidenceParent;
  END;
  IF @Credits<>N'[]'
  BEGIN
    DECLARE @CreditEvidence nvarchar(max);
    EXEC Purchasing.PlanSupplierCreditEvidence @TenantId,@Credits,@CreditEvidence OUTPUT;
    INSERT @Dependencies SELECT Kind,Id,Version,ParentId FROM OPENJSON(@CreditEvidence)
      WITH(Kind varchar(40) '$.kind',Id uniqueidentifier '$.id',Version varchar(66) '$.version',ParentId uniqueidentifier '$.parentId');
  END;
  IF (SELECT COUNT(*) FROM @Dependencies)+(SELECT COUNT(*) FROM @Effects)+(SELECT COUNT(*) FROM OPENJSON(@CreditPositionBindings))>1000
    THROW 51000,'The complete bill correction closure exceeds its event bound.',1;
  DECLARE @Movements nvarchar(max)=(SELECT ItemId itemId,@Bill groupId,PostingDate postingDate,CONVERT(nvarchar(60),Amount) amount FROM @Effects WHERE ItemId IS NOT NULL FOR JSON PATH);
  EXEC Purchasing.AssertSupplierAvailability @TenantId,@Movements;
  SET @Plan=(SELECT 1 schemaVersion,'CorrectSupplierBill' operation,@Bill sourceId,JSON_QUERY(@Canonical) command,
    JSON_QUERY(@Sources) sources,JSON_QUERY(@Units) units,
    JSON_QUERY(@SourceOwners) sourceOwners,JSON_QUERY(@RecognitionSuccessors) recognitionSuccessors,
    JSON_QUERY(@CreditPositionBindings) creditPositionBindings,
    JSON_QUERY(@ReverseAdjustments) reverseAdjustments,
    JSON_QUERY((SELECT Kind kind,Id id,Version version,ParentId parentId FROM @Dependencies ORDER BY Kind,Id,ParentId,Version FOR JSON PATH,INCLUDE_NULL_VALUES)) dependencies,
    JSON_QUERY((SELECT Kind kind,EntityId entityId,ItemId itemId,PostingDate postingDate,CONVERT(nvarchar(60),Amount) amount FROM @Effects ORDER BY PostingDate,Kind,EntityId,ItemId FOR JSON PATH)) effects,
    JSON_QUERY((SELECT Id applicationId,FundingId fundingItemId,DebtId debtItemId,CONVERT(nvarchar(60),Amount) amount,
      PostingDate postingDate,Version version,JournalId journalId,Embedded embedded FROM @Applications ORDER BY Id FOR JSON PATH)) applications,
    JSON_QUERY((SELECT FundingId fundingItemId,DebtId debtItemId,CONVERT(nvarchar(60),Amount) amount,PostingDate postingDate FROM @Reapply ORDER BY Position FOR JSON PATH)) reapplications,
    JSON_QUERY((SELECT Id settlementId,FundingId fundingItemId,CashSourceId cashSourceId,CONVERT(nvarchar(60),Amount) amount,
      PostingDate postingDate,CashVersion cashVersion,JournalId journalId FROM @Settlements ORDER BY Id FOR JSON PATH)) settlements,
    JSON_QUERY(@CreditAdjustments) recognitionAdjustments,JSON_QUERY(@Positions) componentPositions,
    JSON_QUERY((SELECT ItemId itemId,PostingDate postingDate,CONVERT(nvarchar(60),Balance) balance
      FROM Purchasing.SupplierProposedOpenPositions(@TenantId,@Movements) ORDER BY ItemId,PostingDate FOR JSON PATH)) remainingPositions
    FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  IF DATALENGTH(@Plan)>262144 THROW 51000,'The complete bill correction plan exceeds its byte bound.',1;
  SET @Plan=JSON_MODIFY(@Plan,'$.planFingerprint',CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Plan)),2));
END;
