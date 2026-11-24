-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.ReverseSupplierCashSource
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@OwnerRequestId uniqueidentifier,@CashSourceId uniqueidentifier,@PostingDate date,
  @ReverseRelease bit=0
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Group uniqueidentifier,@Correction uniqueidentifier,
    @Now datetimeoffset,@Plan nvarchar(max),@Details nvarchar(max)=(SELECT @PostingDate postingDate FOR JSON PATH,WITHOUT_ARRAY_WRAPPER),
    @ChildKind nvarchar(40)=CASE WHEN @ReverseRelease=1 THEN N'ReverseRelease' ELSE N'ReverseCash' END;
  EXEC Purchasing.RequireSupplierCashComposition @ActorId,@SessionId,@OwnerRequestId,@ChildKind,@CashSourceId,@Details,
    @Group OUTPUT,@Correction OUTPUT,@Now OUTPUT,@Plan OUTPUT;
  DECLARE @OriginalJournal uniqueidentifier,@Kind varchar(32),@Permission nvarchar(100),@CommandKind nvarchar(64),
    @Reason nvarchar(2000)=JSON_VALUE(@Plan,'$.command.reason'),@Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.command.expectedConfigurationVersion')),
    @Canonical nvarchar(max),@Evidence nvarchar(max),@Journal uniqueidentifier,@SourceEvent uniqueidentifier,@Request uniqueidentifier=NEWID();
  DECLARE @Effects TABLE(ItemId uniqueidentifier PRIMARY KEY,Amount decimal(28,4),Ordinal int,MovementId uniqueidentifier);
  IF @ReverseRelease=0
  BEGIN
    SELECT @OriginalJournal=control.JournalId,@Kind=cash.Kind FROM Purchasing.SupplierCashSources cash
      CROSS APPLY Purchasing.SupplierCashControl(@Tenant,cash.Id) control WHERE cash.TenantId=@Tenant AND cash.Id=@CashSourceId;
    SET @Permission=CASE @Kind WHEN 'SupplierRefund' THEN N'SupplierRefundsCorrect' WHEN 'SupplierRefundRepayment' THEN N'SupplierRefundRepaymentsCorrect' END;
    SET @CommandKind=CASE @Kind WHEN 'SupplierRefund' THEN N'Supplier.CorrectRefund' WHEN 'SupplierRefundRepayment' THEN N'Supplier.CorrectRefundRepayment' END;
    INSERT @Effects SELECT ItemId,Amount,JournalOrdinal,NEWID() FROM Purchasing.SupplierCashSettlements
      WHERE TenantId=@Tenant AND CashSourceId=@CashSourceId AND JournalId=@OriginalJournal;
  END
  ELSE
  BEGIN
    SELECT @OriginalJournal=released.JournalId FROM Purchasing.SupplierCashSettlementReleases released
      CROSS APPLY Purchasing.SupplierRefundReleaseControl(@Tenant,released.SettlementId) control
      WHERE released.TenantId=@Tenant AND released.JournalId=@CashSourceId;
    SET @Permission=N'SupplierAllocationsManage'; SET @CommandKind=N'Supplier.CorrectRefundRelease';
    INSERT @Effects SELECT settlement.ItemId,-released.Amount,1,NEWID() FROM Purchasing.SupplierCashSettlementReleases released
      JOIN Purchasing.SupplierCashSettlements settlement ON settlement.TenantId=released.TenantId AND settlement.Id=released.SettlementId
      WHERE released.TenantId=@Tenant AND released.JournalId=@OriginalJournal;
    INSERT @Effects SELECT SettlementId,-Amount,2,NEWID() FROM Purchasing.SupplierCashSettlementReleases
      WHERE TenantId=@Tenant AND JournalId=@OriginalJournal;
  END;
  IF @OriginalJournal IS NULL OR @Permission IS NULL OR NOT EXISTS(SELECT 1 FROM @Effects)
    OR EXISTS(SELECT 1 FROM Accounting.CorrectionGroups WHERE TenantId=@Tenant AND OriginalJournalId=@OriginalJournal)
    THROW 51009,'The complete owned cash or release journal is unavailable or already corrected.',1;
  SET @Evidence=(SELECT 1 schemaVersion,CONVERT(nvarchar(36),sourceEvent.SourceRevision) originalSourceRevision,
    CONVERT(varchar(64),sourceEvent.SnapshotSha256,2) originalSnapshotSha256 FROM Accounting.SourceEvents sourceEvent
    JOIN Accounting.JournalEntries journal ON journal.TenantId=sourceEvent.TenantId AND journal.SourceEventId=sourceEvent.Id
    WHERE journal.TenantId=@Tenant AND journal.Id=@OriginalJournal FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  DECLARE @Movements nvarchar(max)=(SELECT ItemId itemId,@Group groupId,@PostingDate postingDate,CONVERT(nvarchar(60),Amount) amount FROM @Effects FOR JSON PATH);
  EXEC Purchasing.AssertSupplierAvailability @Tenant,@Movements;
  SELECT @Canonical=CanonicalInput FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@Tenant AND RequestId=@OwnerRequestId;
  DECLARE @Corrected TABLE(CorrectionId uniqueidentifier,ReversalJournalId uniqueidentifier,ReplacementJournalId uniqueidentifier,
    ReplacementSourceRevision uniqueidentifier,RecordedAtUtc datetimeoffset);
  INSERT @Corrected EXEC Accounting.CorrectJournalCore @ActorId,@SessionId,@Request,@Permission,@CommandKind,1,
    @Canonical,@OriginalJournal,@Config,@PostingDate,@Reason,@Evidence;
  SELECT @Journal=ReversalJournalId FROM @Corrected;
  SELECT @SourceEvent=SourceEventId FROM Accounting.JournalEntries WHERE TenantId=@Tenant AND Id=@Journal;
  UPDATE Accounting.SourceEvents SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND Id=@SourceEvent;
  UPDATE Accounting.JournalEntries SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND Id=@Journal;
  UPDATE Accounting.PostingReceipts SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND JournalId=@Journal;
  UPDATE actual SET RecordedAtUtc=@Now FROM Accounting.CorrectionGroups actual JOIN @Corrected produced ON produced.CorrectionId=actual.Id WHERE actual.TenantId=@Tenant;
  UPDATE audit SET OccurredAtUtc=@Now FROM Security.TenantSecurityAuditEvents audit JOIN @Corrected produced ON produced.CorrectionId=audit.TargetId
    WHERE audit.TenantId=@Tenant AND audit.Action='Accounting.CorrectJournal';
  INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,PostingDate,Amount,RecordedAtUtc)
    SELECT @Tenant,MovementId,ItemId,@Group,'ReverseSource',@SourceEvent,@PostingDate,Amount,@Now FROM @Effects;
  INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount)
    SELECT @Tenant,NEWID(),@Group,effect.MovementId,@Journal,effect.Ordinal,line.AccountId,line.AccountVersion,line.AccountPurpose,effect.Amount
    FROM @Effects effect JOIN Accounting.JournalLines line ON line.TenantId=@Tenant AND line.JournalId=@Journal AND line.Ordinal=effect.Ordinal
      AND line.AccountPurpose IN('SupplierAdvance','SupplierCreditReceivable','SupplierRefundClearing');
  IF @@ROWCOUNT<>(SELECT COUNT(*) FROM @Effects) THROW 51004,'Cash inverse historical control attribution is incomplete.',1;
  UPDATE version SET ItemId=version.ItemId FROM Purchasing.SupplierItemVersions version JOIN @Effects effect ON effect.ItemId=version.ItemId WHERE version.TenantId=@Tenant;
  SET @Details=(SELECT @CashSourceId sourceId,@OriginalJournal originalJournalId,produced.CorrectionId accountingCorrectionId,
    @ReverseRelease reverseRelease FROM @Corrected produced FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  INSERT Purchasing.SupplierSourceCorrectionMembers(TenantId,Id,CorrectionId,GroupId,Kind,EntityId,JournalId,EffectsJson)
    VALUES(@Tenant,NEWID(),@Correction,@Group,'ReverseSource',@CashSourceId,@Journal,@Details);
END;
