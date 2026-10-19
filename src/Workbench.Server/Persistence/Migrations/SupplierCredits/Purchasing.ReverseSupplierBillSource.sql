-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.ReverseSupplierBillSource
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@OwnerRequestId uniqueidentifier,@BillId uniqueidentifier,@PostingDate date
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Group uniqueidentifier,@Correction uniqueidentifier,
    @Now datetimeoffset,@Plan nvarchar(max),@Details nvarchar(max)=(SELECT @PostingDate postingDate FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  EXEC Purchasing.RequireSupplierBillComposition @ActorId,@SessionId,@OwnerRequestId,N'ReverseBill',@BillId,@Details,
    @Group OUTPUT,@Correction OUTPUT,@Now OUTPUT,@Plan OUTPUT;
  DECLARE @Config uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.command.expectedConfigurationVersion')),
    @Reason nvarchar(2000)=JSON_VALUE(@Plan,'$.command.reason'),@Canonical nvarchar(max),@Unit uniqueidentifier,@RecognitionGroup uniqueidentifier,
    @OriginalMatch uniqueidentifier,@UnitPlan nvarchar(max),@Operation varchar(16);
  SELECT @Canonical=CanonicalInput FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@Tenant AND RequestId=@OwnerRequestId;
  DECLARE units CURSOR LOCAL FAST_FORWARD FOR SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.unitId')),value FROM OPENJSON(@Plan,'$.units') ORDER BY JSON_VALUE(value,'$.unitId');
  OPEN units; FETCH NEXT FROM units INTO @Unit,@UnitPlan;
  WHILE @@FETCH_STATUS=0
  BEGIN
    SET @Operation=CASE WHEN JSON_VALUE(@UnitPlan,'$.binding.successorUnitId') IS NULL THEN 'Reverse' ELSE 'Replace' END;
    SET @RecognitionGroup=COALESCE(TRY_CONVERT(uniqueidentifier,JSON_VALUE(@UnitPlan,'$.binding.correctionGroupId')),NEWID());
    SET @OriginalMatch=NULL;
    SELECT @OriginalMatch=Id FROM Purchasing.RecognitionMatches WHERE TenantId=@Tenant AND UnitId=@Unit;
    IF EXISTS(SELECT 1 FROM Purchasing.RecognitionCorrectionGroups WHERE TenantId=@Tenant AND UnitId=@Unit)
      THROW 51009,'The complete bill recognition unit was already corrected.',1;
    INSERT Purchasing.RecognitionCorrectionGroups(TenantId,Id,UnitId,OriginalMatchId,ActorId,Operation,PostingDate,Reason,RecordedAtUtc)
      VALUES(@Tenant,@RecognitionGroup,@Unit,@OriginalMatch,@ActorId,@Operation,@PostingDate,@Reason,@Now);
    INSERT Purchasing.RecognitionEventCorrections(TenantId,OriginalEventId,CorrectionGroupId)
      SELECT @Tenant,TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.eventId')),@RecognitionGroup FROM OPENJSON(@UnitPlan,'$.originals');
    FETCH NEXT FROM units INTO @Unit,@UnitPlan;
  END;
  CLOSE units; DEALLOCATE units;
  DECLARE @OriginalEvent uniqueidentifier,@OriginalJournal uniqueidentifier,@Side varchar(16),@Evidence nvarchar(max),@Request uniqueidentifier,
    @Journal uniqueidentifier,@Event uniqueidentifier,@Movement uniqueidentifier,@Amount decimal(28,4);
  DECLARE @Corrected TABLE(CorrectionId uniqueidentifier,ReversalJournalId uniqueidentifier,ReplacementJournalId uniqueidentifier,
    ReplacementSourceRevision uniqueidentifier,RecordedAtUtc datetimeoffset);
  DECLARE @EarlierAdjustment nvarchar(max),@OwnerGroup uniqueidentifier,@OwnerNow datetimeoffset,@OwnerPlan nvarchar(max);
  DECLARE adjustments CURSOR LOCAL FAST_FORWARD FOR SELECT value FROM OPENJSON(@Plan,'$.reverseAdjustments')
    ORDER BY JSON_VALUE(value,'$.journalId');
  OPEN adjustments; FETCH NEXT FROM adjustments INTO @EarlierAdjustment;
  WHILE @@FETCH_STATUS=0
  BEGIN
    SET @OriginalJournal=CONVERT(uniqueidentifier,JSON_VALUE(@EarlierAdjustment,'$.journalId'));
    SET @Details=(SELECT @PostingDate postingDate FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    EXEC Purchasing.RequireSupplierBillComposition @ActorId,@SessionId,@OwnerRequestId,N'ReverseRecognitionAdjustment',@OriginalJournal,@Details,
      @OwnerGroup OUTPUT,@Correction OUTPUT,@OwnerNow OUTPUT,@OwnerPlan OUTPUT;
    SET @Evidence=(SELECT 1 schemaVersion,CONVERT(nvarchar(36),sourceEvent.SourceRevision) originalSourceRevision,
      CONVERT(varchar(64),sourceEvent.SnapshotSha256,2) originalSnapshotSha256
      FROM Accounting.SourceEvents sourceEvent JOIN Accounting.JournalEntries journal ON journal.TenantId=sourceEvent.TenantId AND journal.SourceEventId=sourceEvent.Id
      WHERE journal.TenantId=@Tenant AND journal.Id=@OriginalJournal FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    SET @Request=NEWID(); DELETE @Corrected;
    INSERT @Corrected EXEC Accounting.CorrectJournalCore @ActorId,@SessionId,@Request,N'SupplierCreditsCorrect',N'Supplier.CorrectBillMatchingAdjustment',1,
      @Canonical,@OriginalJournal,@Config,@PostingDate,@Reason,@Evidence;
    SELECT @Journal=ReversalJournalId FROM @Corrected;
    SELECT @Event=SourceEventId FROM Accounting.JournalEntries WHERE TenantId=@Tenant AND Id=@Journal;
    UPDATE Accounting.SourceEvents SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND Id=@Event;
    UPDATE Accounting.JournalEntries SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND Id=@Journal;
    UPDATE Accounting.PostingReceipts SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND JournalId=@Journal;
    UPDATE correction SET RecordedAtUtc=@Now FROM Accounting.CorrectionGroups correction JOIN @Corrected produced ON produced.CorrectionId=correction.Id WHERE correction.TenantId=@Tenant;
    UPDATE audit SET OccurredAtUtc=@Now FROM Security.TenantSecurityAuditEvents audit JOIN @Corrected produced ON produced.CorrectionId=audit.TargetId
      WHERE audit.TenantId=@Tenant AND audit.Action='Accounting.CorrectJournal';
    INSERT Purchasing.SupplierSourceCorrectionMembers(TenantId,Id,CorrectionId,GroupId,Kind,EntityId,JournalId,RecognitionEventId,EffectsJson)
      VALUES(@Tenant,NEWID(),@Correction,@Group,'ReverseSource',@OriginalJournal,@Journal,
        CONVERT(uniqueidentifier,JSON_VALUE(@EarlierAdjustment,'$.recognitionEventId')),@EarlierAdjustment);
    FETCH NEXT FROM adjustments INTO @EarlierAdjustment;
  END;
  CLOSE adjustments; DEALLOCATE adjustments;
  DECLARE journals CURSOR LOCAL FAST_FORWARD FOR
    SELECT actual.Id,actual.JournalId,actual.Side,actual.SourceAmount FROM OPENJSON(@Plan,'$.units') unit
      CROSS APPLY OPENJSON(unit.value,'$.originals') original
      JOIN Purchasing.RecognitionSideEvents actual ON actual.TenantId=@Tenant AND actual.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(original.value,'$.eventId'))
      LEFT JOIN Accounting.JournalEntries journal ON journal.TenantId=actual.TenantId AND journal.Id=actual.JournalId
      ORDER BY journal.Sequence DESC,actual.Id;
  OPEN journals; FETCH NEXT FROM journals INTO @OriginalEvent,@OriginalJournal,@Side,@Amount;
  WHILE @@FETCH_STATUS=0
  BEGIN
    IF @OriginalJournal IS NOT NULL
    BEGIN
      SET @Evidence=(SELECT 1 schemaVersion,CONVERT(nvarchar(36),sourceEvent.SourceRevision) originalSourceRevision,
        CONVERT(varchar(64),sourceEvent.SnapshotSha256,2) originalSnapshotSha256
        FROM Accounting.SourceEvents sourceEvent JOIN Accounting.JournalEntries journal ON journal.TenantId=sourceEvent.TenantId AND journal.SourceEventId=sourceEvent.Id
        WHERE journal.TenantId=@Tenant AND journal.Id=@OriginalJournal FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
      SET @Request=NEWID(); DELETE @Corrected;
      INSERT @Corrected EXEC Accounting.CorrectJournalCore @ActorId,@SessionId,@Request,N'SupplierBillsCorrect',N'PurchaseRecognition.CorrectInternal',1,
        @Canonical,@OriginalJournal,@Config,@PostingDate,@Reason,@Evidence;
      SELECT @Journal=ReversalJournalId FROM @Corrected;
      SELECT @Event=SourceEventId FROM Accounting.JournalEntries WHERE TenantId=@Tenant AND Id=@Journal;
      UPDATE Purchasing.RecognitionEventCorrections SET AccountingCorrectionGroupId=(SELECT CorrectionId FROM @Corrected)
        WHERE TenantId=@Tenant AND OriginalEventId=@OriginalEvent;
      UPDATE Accounting.SourceEvents SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND Id=@Event;
      UPDATE Accounting.JournalEntries SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND Id=@Journal;
      UPDATE Accounting.PostingReceipts SET RecordedAtUtc=@Now WHERE TenantId=@Tenant AND JournalId=@Journal;
      UPDATE correction SET RecordedAtUtc=@Now FROM Accounting.CorrectionGroups correction JOIN @Corrected produced ON produced.CorrectionId=correction.Id WHERE correction.TenantId=@Tenant;
      UPDATE audit SET OccurredAtUtc=@Now FROM Security.TenantSecurityAuditEvents audit JOIN @Corrected produced ON produced.CorrectionId=audit.TargetId
        WHERE audit.TenantId=@Tenant AND audit.Action='Accounting.CorrectJournal';
      IF @Side='Invoice'
      BEGIN
        SET @Movement=NEWID();
        INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,RecognitionEventId,PostingDate,Amount,RecordedAtUtc)
          VALUES(@Tenant,@Movement,@BillId,@Group,'ReverseSource',@Event,@OriginalEvent,@PostingDate,-@Amount,@Now);
        INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount)
          SELECT @Tenant,NEWID(),@Group,@Movement,@Journal,Ordinal,AccountId,AccountVersion,AccountPurpose,-@Amount
          FROM Accounting.JournalLines WHERE TenantId=@Tenant AND JournalId=@Journal AND AccountPurpose='SupplierPayable' AND Debit=@Amount AND Credit=0;
        IF @@ROWCOUNT<>1 THROW 51004,'The bill inverse payable attribution is incomplete.',1;
      END;
      SET @Details=(SELECT @BillId sourceId,@OriginalJournal originalJournalId,@OriginalEvent recognitionEventId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
      INSERT Purchasing.SupplierSourceCorrectionMembers(TenantId,Id,CorrectionId,GroupId,Kind,EntityId,JournalId,RecognitionEventId,ItemId,EffectsJson)
        VALUES(@Tenant,NEWID(),@Correction,@Group,'ReverseSource',@OriginalEvent,@Journal,@OriginalEvent,@BillId,@Details);
    END;
    FETCH NEXT FROM journals INTO @OriginalEvent,@OriginalJournal,@Side,@Amount;
  END;
  CLOSE journals; DEALLOCATE journals;
  UPDATE Purchasing.SupplierItemVersions SET ItemId=ItemId WHERE TenantId=@Tenant AND ItemId=@BillId;
  SET @Details=(SELECT @BillId sourceId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  INSERT Purchasing.SupplierSourceCorrectionMembers(TenantId,Id,CorrectionId,GroupId,Kind,EntityId,ItemId,EffectsJson)
    VALUES(@Tenant,NEWID(),@Correction,@Group,'ReverseSource',@BillId,@BillId,@Details);
END;
