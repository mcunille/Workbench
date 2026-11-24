-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.BuildSupplierCashCorrectionSources
  @TenantId uniqueidentifier,@Operation nvarchar(40),@Canonical nvarchar(max),
  @Sources nvarchar(max) OUTPUT,@Releases nvarchar(max) OUTPUT,@Applications nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId IS NULL OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Cash planning requires its owner transaction.',1;
  DECLARE @Root uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.sourceId')),
    @Kind varchar(32)=CASE @Operation WHEN N'CorrectSupplierRefund' THEN 'SupplierRefund'
      WHEN N'CorrectSupplierRefundRepayment' THEN 'SupplierRefundRepayment' END,
    @Po uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
    @Supplier uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),
    @Currency varchar(3)=JSON_VALUE(@Canonical,'$.currency'),@Date date=CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23);
  IF @Kind IS NULL THROW 51000,'A fixed cash correction operation is required.',1;
  DECLARE @Selected TABLE(Id uniqueidentifier PRIMARY KEY,Kind varchar(32),Expected binary(8),Replacement nvarchar(max));
  INSERT @Selected VALUES(@Root,@Kind,CONVERT(binary(8),JSON_VALUE(@Canonical,'$.expectedSourceVersion'),1),JSON_QUERY(@Canonical,'$.replacement'));
  IF EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.sourceDecisions') WHERE JSON_VALUE(value,'$.sourceKind') NOT IN('SupplierRefund','SupplierRefundRepayment')
    OR TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.sourceId'))=@Root)
    THROW 51004,'Only complete affected cash source decisions are supported.',1;

  -- Closing an incoming receipt's noncash release cancels its clearing opening.
  -- Discover every surviving outgoing source consuming that opening, as a WHOLE source.
  -- An outgoing-source inverse only restores capacity; it does not cancel other openings.
  DECLARE @Needed TABLE(Id uniqueidentifier PRIMARY KEY);
  INSERT @Needed SELECT DISTINCT repayment.Id
    FROM Purchasing.SupplierCashSettlements originalSettlement
    JOIN Purchasing.SupplierCashSettlementReleases released ON released.TenantId=originalSettlement.TenantId AND released.SettlementId=originalSettlement.Id
    JOIN Purchasing.SupplierCashSettlements consumed ON consumed.TenantId=released.TenantId AND consumed.ItemId=released.SettlementId
    JOIN Purchasing.SupplierCashSources repayment ON repayment.TenantId=consumed.TenantId AND repayment.Id=consumed.CashSourceId
      AND repayment.Kind='SupplierRefundRepayment'
    WHERE originalSettlement.TenantId=@TenantId AND originalSettlement.CashSourceId=@Root AND @Kind='SupplierRefund'
      AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrections corrected WHERE corrected.TenantId=repayment.TenantId
        AND corrected.SourceKind=repayment.Kind AND corrected.SourceId=repayment.Id);
  IF EXISTS(SELECT 1 FROM @Needed needed WHERE NOT EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.sourceDecisions') decision
      WHERE JSON_VALUE(decision.value,'$.sourceKind')='SupplierRefundRepayment'
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(decision.value,'$.sourceId'))=needed.Id))
    OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.sourceDecisions') decision WHERE NOT EXISTS(SELECT 1 FROM @Needed needed
      WHERE needed.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(decision.value,'$.sourceId'))
        AND JSON_VALUE(decision.value,'$.sourceKind')='SupplierRefundRepayment'))
    THROW 51009,'Every surviving repayment of the released clearing requires its explicit complete source decision.',1;
  INSERT @Selected SELECT needed.Id,'SupplierRefundRepayment',TRY_CONVERT(binary(8),JSON_VALUE(decision.value,'$.expectedSourceVersion'),1),
    JSON_QUERY(decision.value,'$.replacement') FROM @Needed needed
    JOIN OPENJSON(@Canonical,'$.sourceDecisions') decision ON TRY_CONVERT(uniqueidentifier,JSON_VALUE(decision.value,'$.sourceId'))=needed.Id;
  IF (SELECT COUNT(*) FROM @Selected)>1000 THROW 51000,'The complete cash correction closure exceeds its event bound.',1;

  DECLARE @Actual TABLE(Id uniqueidentifier PRIMARY KEY,Kind varchar(32),RevisionId uniqueidentifier,Version varchar(18),
    Amount decimal(28,4),OriginalPostingDate date,JournalId uniqueidentifier,SnapshotHash varchar(64),Replacement nvarchar(max));
  INSERT @Actual SELECT cash.Id,cash.Kind,cash.RevisionId,CONVERT(varchar(18),CONVERT(binary(8),version.RowVersion),1),
      cash.Amount,cash.PostingDate,control.JournalId,CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),cash.SourceSnapshotJson)),2),selected.Replacement
    FROM @Selected selected JOIN Purchasing.SupplierCashSources cash WITH(UPDLOCK,HOLDLOCK)
      ON cash.TenantId=@TenantId AND cash.Id=selected.Id AND cash.Kind=selected.Kind
    JOIN Purchasing.SupplierCashVersions version WITH(UPDLOCK,HOLDLOCK)
      ON version.TenantId=cash.TenantId AND version.CashSourceId=cash.Id AND version.RowVersion=selected.Expected
    CROSS APPLY Purchasing.SupplierCashControl(@TenantId,cash.Id) control
    WHERE cash.SupplierId=@Supplier AND cash.PurchaseOrderId=@Po AND cash.Currency=@Currency
      AND cash.PostingDate<=@Date AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrections corrected
        WHERE corrected.TenantId=cash.TenantId AND corrected.SourceKind=cash.Kind AND corrected.SourceId=cash.Id)
      AND NOT EXISTS(SELECT 1 FROM Accounting.CorrectionGroups corrected WHERE corrected.TenantId=cash.TenantId AND corrected.OriginalJournalId=control.JournalId);
  IF (SELECT COUNT(*) FROM @Actual)<>(SELECT COUNT(*) FROM @Selected)
    THROW 51009,'A complete original cash source changed, was corrected, is later or is unavailable.',1;

  DECLARE @ReleaseRows TABLE(SettlementId uniqueidentifier PRIMARY KEY,CashSourceId uniqueidentifier,FundingId uniqueidentifier,
    Amount decimal(28,4),JournalId uniqueidentifier,OriginalCorrectionId uniqueidentifier,SnapshotHash varchar(64));
  INSERT @ReleaseRows SELECT released.SettlementId,settlement.CashSourceId,settlement.ItemId,released.Amount,released.JournalId,
      released.CorrectionId,CONVERT(varchar(64),sourceEvent.SnapshotSha256,2)
    FROM @Actual actual JOIN Purchasing.SupplierCashSettlements settlement ON settlement.TenantId=@TenantId AND settlement.CashSourceId=actual.Id
    JOIN Purchasing.SupplierCashSettlementReleases released ON released.TenantId=settlement.TenantId AND released.SettlementId=settlement.Id
    JOIN Accounting.JournalEntries journal ON journal.TenantId=released.TenantId AND journal.Id=released.JournalId
    JOIN Accounting.SourceEvents sourceEvent ON sourceEvent.TenantId=journal.TenantId AND sourceEvent.Id=journal.SourceEventId
    CROSS APPLY Purchasing.SupplierRefundReleaseControl(@TenantId,released.SettlementId) control
    WHERE actual.Kind='SupplierRefund' AND released.PostingDate<=@Date
      AND NOT EXISTS(SELECT 1 FROM Accounting.CorrectionGroups corrected WHERE corrected.TenantId=released.TenantId AND corrected.OriginalJournalId=released.JournalId);
  IF (SELECT COUNT(*) FROM @ReleaseRows)<>(SELECT COUNT(*) FROM @Actual actual
      JOIN Purchasing.SupplierCashSettlements settlement ON settlement.TenantId=@TenantId AND settlement.CashSourceId=actual.Id
      JOIN Purchasing.SupplierCashSettlementReleases released ON released.TenantId=settlement.TenantId AND released.SettlementId=settlement.Id
      WHERE actual.Kind='SupplierRefund')
    THROW 51004,'Every original noncash release must have an authentic available inverse.',1;

  DECLARE @AppRows TABLE(Id uniqueidentifier PRIMARY KEY,FundingId uniqueidentifier,DebtId uniqueidentifier,Amount decimal(28,4),
    Version varchar(18),JournalId uniqueidentifier);
  INSERT @AppRows SELECT app.Id,app.FundingItemId,app.DebtItemId,app.Amount,
      CONVERT(varchar(18),CONVERT(binary(8),version.RowVersion),1),control.JournalId
    FROM Purchasing.SupplierApplications app
    JOIN Purchasing.SupplierApplicationVersions version WITH(UPDLOCK,HOLDLOCK) ON version.TenantId=app.TenantId AND version.ApplicationId=app.Id
    CROSS APPLY Purchasing.SupplierApplicationControl(@TenantId,app.Id) control
    WHERE app.TenantId=@TenantId AND EXISTS(SELECT 1 FROM @ReleaseRows released WHERE released.SettlementId=app.DebtItemId)
      AND app.PostingDate<=@Date
      AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierApplicationReversals reversed WHERE reversed.TenantId=app.TenantId AND reversed.ApplicationId=app.Id)
      AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierPaymentCorrections corrected WHERE corrected.TenantId=app.TenantId AND corrected.OriginalPaymentId=app.FundingItemId);
  IF (SELECT COUNT(*) FROM @AppRows)<>(SELECT COUNT(*) FROM Purchasing.SupplierApplications app
      WHERE app.TenantId=@TenantId AND EXISTS(SELECT 1 FROM @ReleaseRows released WHERE released.SettlementId=app.DebtItemId)
        AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierApplicationReversals reversed WHERE reversed.TenantId=app.TenantId AND reversed.ApplicationId=app.Id)
        AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierPaymentCorrections corrected WHERE corrected.TenantId=app.TenantId AND corrected.OriginalPaymentId=app.FundingItemId))
    THROW 51004,'An affected clearing allocation has an unavailable or later owner.',1;
  SET @Sources=(SELECT Kind sourceKind,Id sourceId,RevisionId sourceRevisionId,Version version,CONVERT(nvarchar(60),Amount) amount,
    @Date postingDate,OriginalPostingDate originalPostingDate,JournalId journalId,SnapshotHash snapshotSha256,JSON_QUERY(Replacement) replacement
    FROM @Actual ORDER BY CASE WHEN Id=@Root THEN 0 ELSE 1 END,Id FOR JSON PATH,INCLUDE_NULL_VALUES);
  SET @Releases=(SELECT SettlementId settlementId,CashSourceId cashSourceId,FundingId fundingItemId,SettlementId clearingItemId,
    CONVERT(nvarchar(60),Amount) amount,@Date postingDate,JournalId journalId,OriginalCorrectionId originalCorrectionId,SnapshotHash snapshotSha256
    FROM @ReleaseRows ORDER BY SettlementId FOR JSON PATH);
  SET @Applications=(SELECT Id applicationId,FundingId fundingItemId,DebtId debtItemId,CONVERT(nvarchar(60),Amount) amount,
    @Date postingDate,Version version,JournalId journalId FROM @AppRows ORDER BY Id FOR JSON PATH);
END;
