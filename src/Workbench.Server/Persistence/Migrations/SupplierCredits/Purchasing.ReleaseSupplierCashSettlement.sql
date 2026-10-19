-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.ReleaseSupplierCashSettlement
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@OwnerRequestId uniqueidentifier,@SettlementId uniqueidentifier,@PostingDate date
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Group uniqueidentifier,@Correction uniqueidentifier,
    @Now datetimeoffset,@Plan nvarchar(max),@Details nvarchar(max)=(SELECT @PostingDate postingDate FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  EXEC Purchasing.RequireSupplierSourceComposition @ActorId,@SessionId,@OwnerRequestId,N'ReleaseSettlement',@SettlementId,@Details,
    @Group OUTPUT,@Correction OUTPUT,@Now OUTPUT,@Plan OUTPUT;
  IF EXISTS(SELECT 1 FROM Purchasing.SupplierCashSettlementReleases WHERE TenantId=@Tenant AND SettlementId=@SettlementId)
    THROW 51009,'An actual cash settlement can be released only once.',1;
  DECLARE @Funding uniqueidentifier,@CashSource uniqueidentifier,@Amount decimal(28,4),@Account uniqueidentifier,@AccountVersion uniqueidentifier,
    @Clearing uniqueidentifier,@ClearingVersion uniqueidentifier,@Supplier uniqueidentifier,@Po uniqueidentifier,@Currency varchar(3),
    @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.command.expectedConfigurationVersion')),
    @Policy nvarchar(max),@Snapshot nvarchar(max),@Lines nvarchar(max),@Request uniqueidentifier=NEWID(),
    @Restored uniqueidentifier=NEWID(),@Opening uniqueidentifier=NEWID(),@Journal uniqueidentifier,@SourceEvent uniqueidentifier;
  SELECT @Funding=settlement.ItemId,@CashSource=settlement.CashSourceId,@Amount=settlement.Amount,
    @Account=control.AccountId,@AccountVersion=control.AccountVersion,@Supplier=correction.SupplierId,@Po=correction.PurchaseOrderId,@Currency=correction.Currency
    FROM Purchasing.SupplierCashSettlements settlement
    CROSS APPLY Purchasing.SupplierCashControl(@Tenant,settlement.CashSourceId) cashControl
    CROSS APPLY Purchasing.SupplierCreditControl(@Tenant,settlement.ItemId) control
    JOIN Purchasing.SupplierSourceCorrections correction ON correction.TenantId=settlement.TenantId AND correction.Id=@Correction
      AND correction.SourceKind='SupplierCredit' AND correction.SourceId=settlement.ItemId
    JOIN Purchasing.SupplierCashSources cash ON cash.TenantId=settlement.TenantId AND cash.Id=settlement.CashSourceId AND cash.PostingDate<=@PostingDate
    WHERE settlement.TenantId=@Tenant AND settlement.Id=@SettlementId;
  IF @Funding IS NULL THROW 51004,'The original credit-backed actual refund settlement is unavailable.',1;
  SELECT @Policy=Payload FROM Accounting.Configurations WHERE TenantId=@Tenant AND Version=@Config;
  SELECT @Clearing=account.Id,@ClearingVersion=account.Version FROM OPENJSON(@Policy,'$.mappings') mapping
    JOIN Accounting.Accounts account WITH(UPDLOCK,HOLDLOCK) ON account.TenantId=@Tenant AND account.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(mapping.value,'$.accountId'))
    WHERE JSON_VALUE(mapping.value,'$.slot')='SupplierRefundClearing' AND account.Type='Liability'
      AND account.Purpose='SupplierRefundClearing' AND account.ArchivedAtUtc IS NULL;
  IF @Clearing IS NULL THROW 51004,'Current supplier refund-clearing mapping unavailable.',1;
  SET @Snapshot=(SELECT @Correction correctionId,@Group groupId,@OwnerRequestId ownerRequestId,@SettlementId settlementId,
    @CashSource cashSourceId,@Funding fundingItemId,CONVERT(nvarchar(60),@Amount) amount,@PostingDate postingDate,
    @Clearing clearingAccountId,@ClearingVersion clearingAccountVersion FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  SET @Lines=(SELECT ordinal,accountId,accountVersion,CONVERT(nvarchar(60),debit) debit,CONVERT(nvarchar(60),credit) credit
    FROM(VALUES(1,@Account,@AccountVersion,@Amount,CONVERT(decimal(28,4),0)),
      (2,@Clearing,@ClearingVersion,CONVERT(decimal(28,4),0),@Amount)) line(ordinal,accountId,accountVersion,debit,credit)
    ORDER BY ordinal FOR JSON PATH);
  DECLARE @Posted TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
  INSERT @Posted EXEC Accounting.PostJournal @ActorId,@SessionId,@Request,N'SupplierCreditsCorrect',N'Supplier.RefundRelease',1,
    @Snapshot,N'SupplierRefundRelease',@SettlementId,@Correction,N'Release',1,@Config,@Currency,
    @PostingDate,@PostingDate,@PostingDate,NULL,N'Release settlement while retaining actual cash',@Snapshot,@Lines;
  SELECT @Journal=JournalId,@SourceEvent=SourceEventId FROM @Posted;
  UPDATE Accounting.SourceEvents SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND Id=@SourceEvent;
  UPDATE Accounting.JournalEntries SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND Id=@Journal;
  UPDATE Accounting.PostingReceipts SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND RequestId=@Request;
  UPDATE Security.TenantSecurityAuditEvents SET OccurredAtUtc=@Now WHERE TenantId=@Tenant AND TargetId=@Journal AND Action='Accounting.PostJournal';
  DECLARE @Movements nvarchar(max)=(SELECT itemId,@Group groupId,@PostingDate postingDate,CONVERT(nvarchar(60),@Amount) amount
    FROM(VALUES(@Funding),(@SettlementId)) item(itemId) FOR JSON PATH);
  EXEC Purchasing.AssertSupplierAvailability @Tenant,@Movements;
  INSERT Purchasing.SupplierOpenItems(TenantId,Id,Kind,SupplierId,PurchaseOrderId,BillId,Currency,SourceKind,SourceId,SourceRevisionId,
    SourcePostingDate,DueDate,SourceSnapshotJson,RecordedAtUtc)
    VALUES(@Tenant,@SettlementId,'RefundClearing',@Supplier,@Po,NULL,@Currency,'SupplierRefundRelease',@SettlementId,@Correction,@PostingDate,NULL,@Snapshot,@Now);
  INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,PostingDate,Amount,RecordedAtUtc)
    VALUES(@Tenant,@Restored,@Funding,@Group,'ReleaseSettlement',@SourceEvent,@PostingDate,@Amount,@Now),
      (@Tenant,@Opening,@SettlementId,@Group,'Open',@SourceEvent,@PostingDate,@Amount,@Now);
  INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount)
    SELECT @Tenant,NEWID(),@Group,CASE line.Ordinal WHEN 1 THEN @Restored ELSE @Opening END,@Journal,line.Ordinal,
      line.AccountId,line.AccountVersion,line.AccountPurpose,@Amount FROM Accounting.JournalLines line
      WHERE line.TenantId=@Tenant AND line.JournalId=@Journal;
  INSERT Purchasing.SupplierCashSettlementReleases(TenantId,SettlementId,CorrectionId,GroupId,JournalId,RestoredMovementId,OpeningMovementId,
    Amount,ClearingAccountId,ClearingAccountVersion,PostingDate,ActorId,RecordedAtUtc)
    VALUES(@Tenant,@SettlementId,@Correction,@Group,@Journal,@Restored,@Opening,@Amount,@Clearing,@ClearingVersion,@PostingDate,@ActorId,@Now);
  INSERT Purchasing.SupplierItemVersions(TenantId,ItemId) VALUES(@Tenant,@SettlementId);
  UPDATE Purchasing.SupplierItemVersions SET ItemId=ItemId WHERE TenantId=@Tenant AND ItemId=@Funding;
  UPDATE Purchasing.SupplierCashVersions SET CashSourceId=CashSourceId WHERE TenantId=@Tenant AND CashSourceId=@CashSource;
  INSERT Purchasing.SupplierSourceCorrectionMembers(TenantId,Id,CorrectionId,GroupId,Kind,EntityId,JournalId,ItemId,EffectsJson)
    VALUES(@Tenant,NEWID(),@Correction,@Group,'ReleaseSettlement',@SettlementId,@Journal,@SettlementId,@Snapshot);
END;
