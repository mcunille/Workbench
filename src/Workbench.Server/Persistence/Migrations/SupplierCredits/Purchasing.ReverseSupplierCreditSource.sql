-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.ReverseSupplierCreditSource
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@OwnerRequestId uniqueidentifier,@CreditId uniqueidentifier,@PostingDate date
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Group uniqueidentifier,@Correction uniqueidentifier,
    @Now datetimeoffset,@Plan nvarchar(max),@Details nvarchar(max)=(SELECT @PostingDate postingDate FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  EXEC Purchasing.RequireSupplierSourceComposition @ActorId,@SessionId,@OwnerRequestId,N'ReverseSource',@CreditId,@Details,
    @Group OUTPUT,@Correction OUTPUT,@Now OUTPUT,@Plan OUTPUT;
  DECLARE @OriginalJournal uniqueidentifier,@Amount decimal(28,4),@Evidence nvarchar(max),@Canonical nvarchar(max),
    @Reason nvarchar(2000)=JSON_VALUE(@Plan,'$.command.reason'),@Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.command.expectedConfigurationVersion')),
    @Request uniqueidentifier=NEWID(),@Journal uniqueidentifier,@SourceEvent uniqueidentifier,@Movement uniqueidentifier=NEWID();
  SELECT @OriginalJournal=journal.Id,@Amount=credit.Total FROM Purchasing.SupplierCredits credit
    CROSS APPLY Purchasing.SupplierCreditControl(@Tenant,credit.Id) control
    JOIN Accounting.SourceEvents sourceEvent ON sourceEvent.TenantId=credit.TenantId AND sourceEvent.SourceKind='SupplierCredit'
      AND sourceEvent.SourceId=credit.Id AND sourceEvent.SourceRevision=credit.RevisionId AND sourceEvent.EventKind='Credit'
    JOIN Accounting.JournalEntries journal ON journal.TenantId=sourceEvent.TenantId AND journal.SourceEventId=sourceEvent.Id
    WHERE credit.TenantId=@Tenant AND credit.Id=@CreditId;
  IF @OriginalJournal IS NULL OR EXISTS(SELECT 1 FROM Accounting.CorrectionGroups WHERE TenantId=@Tenant AND OriginalJournalId=@OriginalJournal)
    THROW 51009,'The owned credit journal is unavailable or already corrected.',1;
  SET @Evidence=(SELECT 1 schemaVersion,CONVERT(nvarchar(36),sourceEvent.SourceRevision) originalSourceRevision,
    CONVERT(varchar(64),sourceEvent.SnapshotSha256,2) originalSnapshotSha256
    FROM Accounting.SourceEvents sourceEvent JOIN Accounting.JournalEntries journal ON journal.TenantId=sourceEvent.TenantId AND journal.SourceEventId=sourceEvent.Id
    WHERE journal.TenantId=@Tenant AND journal.Id=@OriginalJournal FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  DECLARE @Movements nvarchar(max)=(SELECT @CreditId itemId,@Group groupId,@PostingDate postingDate,CONVERT(nvarchar(60),-@Amount) amount FOR JSON PATH);
  EXEC Purchasing.AssertSupplierAvailability @Tenant,@Movements;
  SELECT @Canonical=CanonicalInput FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@Tenant AND RequestId=@OwnerRequestId;
  DECLARE @Corrected TABLE(CorrectionId uniqueidentifier,ReversalJournalId uniqueidentifier,ReplacementJournalId uniqueidentifier,
    ReplacementSourceRevision uniqueidentifier,RecordedAtUtc datetimeoffset);
  INSERT @Corrected EXEC Accounting.CorrectJournalCore @ActorId,@SessionId,@Request,N'SupplierCreditsCorrect',N'Supplier.CorrectCredit',1,
    @Canonical,@OriginalJournal,@Config,@PostingDate,@Reason,@Evidence;
  SELECT @Journal=ReversalJournalId FROM @Corrected;
  SELECT @SourceEvent=SourceEventId FROM Accounting.JournalEntries WHERE TenantId=@Tenant AND Id=@Journal;
  UPDATE Accounting.SourceEvents SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND Id=@SourceEvent;
  UPDATE Accounting.JournalEntries SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND Id=@Journal;
  UPDATE Accounting.PostingReceipts SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND JournalId=@Journal;
  UPDATE correction SET RecordedAtUtc=@Now FROM Accounting.CorrectionGroups correction JOIN @Corrected produced ON produced.CorrectionId=correction.Id WHERE correction.TenantId=@Tenant;
  UPDATE audit SET OccurredAtUtc=@Now FROM Security.TenantSecurityAuditEvents audit JOIN @Corrected produced ON produced.CorrectionId=audit.TargetId
    WHERE audit.TenantId=@Tenant AND audit.Action='Accounting.CorrectJournal';
  INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,PostingDate,Amount,RecordedAtUtc)
    VALUES(@Tenant,@Movement,@CreditId,@Group,'ReverseSource',@SourceEvent,@PostingDate,-@Amount,@Now);
  INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount)
    SELECT @Tenant,NEWID(),@Group,@Movement,@Journal,Ordinal,AccountId,AccountVersion,AccountPurpose,-@Amount
    FROM Accounting.JournalLines WHERE TenantId=@Tenant AND JournalId=@Journal AND Ordinal=1 AND AccountPurpose='SupplierCreditReceivable' AND Credit=@Amount AND Debit=0;
  IF @@ROWCOUNT<>1 THROW 51004,'Credit inverse control attribution is incomplete.',1;
  UPDATE Purchasing.SupplierItemVersions SET ItemId=ItemId WHERE TenantId=@Tenant AND ItemId=@CreditId;
  SET @Details=(SELECT @CreditId sourceId,@OriginalJournal originalJournalId,produced.CorrectionId accountingCorrectionId
    FROM @Corrected produced FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  INSERT Purchasing.SupplierSourceCorrectionMembers(TenantId,Id,CorrectionId,GroupId,Kind,EntityId,JournalId,ItemId,EffectsJson)
    VALUES(@Tenant,NEWID(),@Correction,@Group,'ReverseSource',@CreditId,@Journal,@CreditId,@Details);
END;
