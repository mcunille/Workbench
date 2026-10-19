-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.AppendSupplierBillSuccessor
  @TenantId uniqueidentifier,@RecognitionRequestId uniqueidentifier,@BillId uniqueidentifier,@OwnerRequestId uniqueidentifier
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Actor uniqueidentifier,@Session uniqueidentifier,@Group uniqueidentifier,@Correction uniqueidentifier,@Now datetimeoffset,
    @Plan nvarchar(max),@Canonical nvarchar(max),@OwnerCanonical nvarchar(max),@Payload nvarchar(max),@Revision uniqueidentifier;
  -- The caller is a private source adapter. Bind every row to its actual pending parent,
  -- authentic reviewed posting and the real recognition receipt produced in this transaction.
  IF @@TRANCOUNT=0 OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Successor derivation requires the bill owner transaction.',1;
  SELECT @Actor=receipt.ActorId,@Group=receipt.GroupId,@Now=receipt.RecordedAtUtc,@Plan=JSON_QUERY(receipt.ResultJson,'$.plan'),@OwnerCanonical=receipt.CanonicalInput
    FROM Purchasing.SupplierFinancialReceipts receipt JOIN Purchasing.SupplierFinancialGroups financialGroup
      ON financialGroup.TenantId=receipt.TenantId AND financialGroup.Id=receipt.GroupId AND financialGroup.Operation='CorrectBill'
        AND financialGroup.RecordedAtUtc=receipt.RecordedAtUtc
    WHERE receipt.TenantId=@TenantId AND receipt.RequestId=@OwnerRequestId AND receipt.Operation='CorrectSupplierBill'
      AND JSON_VALUE(receipt.ResultJson,'$.state')='pending';
  SELECT @Correction=Id FROM Purchasing.SupplierSourceCorrections WHERE TenantId=@TenantId AND GroupId=@Group
    AND SourceKind='SupplierBill' AND ReplacementSourceId=@BillId AND ActorId=@Actor AND RecordedAtUtc=@Now;
  SELECT @Revision=posting.RevisionId,@Payload=revision.Payload,@Canonical=recognition.CanonicalInput
    FROM Purchasing.SupplierBillPostings posting
    JOIN Purchasing.SupplierBills bill ON bill.TenantId=posting.TenantId AND bill.Id=posting.BillId AND bill.State='Posted'
      AND bill.CurrentRevisionId=posting.RevisionId AND bill.CurrentReviewId=posting.ReviewId
    JOIN Purchasing.SupplierBillRevisions revision ON revision.TenantId=posting.TenantId AND revision.Id=posting.RevisionId AND revision.BillId=bill.Id
    JOIN Purchasing.RecognitionGroupReceipts recognition ON recognition.TenantId=posting.TenantId AND recognition.RequestId=posting.RecognitionRequestId
      AND recognition.RecordedAtUtc=@Now AND recognition.ActorId=@Actor AND recognition.CommandKind='Post'
      AND recognition.InputSha256=HASHBYTES('SHA2_256',CONVERT(varbinary(max),recognition.CanonicalInput))
    WHERE posting.TenantId=@TenantId AND posting.BillId=@BillId AND posting.RecognitionRequestId=@RecognitionRequestId AND posting.RecordedAtUtc=@Now;
  IF @Correction IS NULL OR @Payload IS NULL OR @Plan IS NULL
    OR @BillId<>TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.sources[0].replacementId'))
    OR @Revision<>TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.sources[0].replacementRevisionId'))
    OR HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Payload))<>TRY_CONVERT(binary(32),JSON_VALUE(@Plan,'$.sources[0].successorPayloadSha256'),2)
    OR JSON_VALUE(@OwnerCanonical,'$.expectedPlanFingerprint')<>JSON_VALUE(@Plan,'$.planFingerprint')
    THROW 51004,'Complete reviewed successor ownership is unavailable.',1;
  DECLARE @PostedSides TABLE(Id uniqueidentifier PRIMARY KEY,UnitId uniqueidentifier,JournalId uniqueidentifier,Amount decimal(28,4));
  INSERT @PostedSides SELECT actual.Id,actual.UnitId,actual.JournalId,actual.SourceAmount
    FROM Purchasing.SupplierBillPostingEvents posting
    JOIN Purchasing.RecognitionSideEvents actual ON actual.TenantId=posting.TenantId AND actual.Id=posting.EventId AND actual.Side='Invoice'
      AND actual.SourceId=@BillId AND actual.SourceRevision=@Revision AND actual.ActorId=@Actor AND actual.RecordedAtUtc=@Now
    WHERE posting.TenantId=@TenantId AND posting.BillId=@BillId;
  DECLARE @Events TABLE(Id uniqueidentifier PRIMARY KEY,UnitId uniqueidentifier,JournalId uniqueidentifier,SourceEventId uniqueidentifier,
    Ordinal int,Amount decimal(28,4),PostingDate date,AccountId uniqueidentifier,AccountVersion uniqueidentifier);
  INSERT @Events SELECT actual.Id,actual.UnitId,journal.Id,journal.SourceEventId,line.Ordinal,actual.SourceAmount,actual.PostingDate,line.AccountId,line.AccountVersion
    FROM Purchasing.SupplierBillPostingEvents posting
    JOIN Purchasing.RecognitionSideEvents actual ON actual.TenantId=posting.TenantId AND actual.Id=posting.EventId AND actual.Side='Invoice'
      AND actual.SourceId=@BillId AND actual.SourceRevision=@Revision AND actual.ActorId=@Actor AND actual.RecordedAtUtc=@Now
    JOIN Accounting.JournalEntries journal ON journal.TenantId=actual.TenantId AND journal.Id=actual.JournalId AND journal.RecordedAtUtc=@Now
      AND journal.ActorId=@Actor AND journal.PostingDate=actual.PostingDate
    JOIN Accounting.SourceEvents sourceEvent ON sourceEvent.TenantId=journal.TenantId AND sourceEvent.Id=journal.SourceEventId
      AND sourceEvent.SourceKind='PurchaseRecognition' AND sourceEvent.EventKind='Invoice' AND sourceEvent.SourceId=actual.Id
      AND sourceEvent.SourceRevision=@Revision AND sourceEvent.RecordedAtUtc=@Now AND sourceEvent.ActorId=@Actor
    JOIN Accounting.JournalLines line ON line.TenantId=journal.TenantId AND line.JournalId=journal.Id
      AND line.AccountPurpose='SupplierPayable' AND line.Debit=0 AND line.Credit=actual.SourceAmount
    WHERE posting.TenantId=@TenantId AND posting.BillId=@BillId AND actual.SourceAmount>0;
  IF (SELECT COUNT(*) FROM @PostedSides)<>(SELECT COUNT(*) FROM OPENJSON(@Plan,'$.units'))
    OR (SELECT COUNT(*) FROM @Events)<>(SELECT COUNT(*) FROM @PostedSides WHERE Amount>0)
    OR EXISTS(SELECT 1 FROM @PostedSides WHERE Amount=0 AND JournalId IS NOT NULL)
    OR (SELECT SUM(CONVERT(decimal(38,4),Amount)) FROM @Events)<>TRY_CONVERT(decimal(28,4),JSON_VALUE(@Payload,'$.total'))
    OR EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.units') unit WHERE NOT EXISTS(SELECT 1 FROM @PostedSides actual
      WHERE actual.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(unit.value,'$.binding.invoiceEventId'))
        AND actual.UnitId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(unit.value,'$.binding.successorUnitId'))))
    OR EXISTS(SELECT 1 FROM Purchasing.SupplierOpenItems WHERE TenantId=@TenantId AND Id=@BillId)
    THROW 51004,'The successor payable requires every authentic reviewed recognition side.',1;
  DECLARE @Movements nvarchar(max)=(SELECT @BillId itemId,@Group groupId,PostingDate postingDate,CONVERT(nvarchar(60),Amount) amount FROM @Events FOR JSON PATH);
  EXEC Purchasing.AssertSupplierAvailability @TenantId,@Movements;
  INSERT Purchasing.SupplierOpenItems(TenantId,Id,Kind,SupplierId,PurchaseOrderId,Currency,SourceKind,SourceId,SourceRevisionId,
    BillId,SourcePostingDate,DueDate,SourceSnapshotJson,RecordedAtUtc)
    SELECT @TenantId,@BillId,'Payable',SupplierId,PurchaseOrderId,Currency,'SupplierBill',@BillId,@Revision,@BillId,
      CONVERT(date,JSON_VALUE(@Payload,'$.postingDate'),23),TRY_CONVERT(date,JSON_VALUE(@Payload,'$.dueDate'),23),@Payload,@Now
      FROM Purchasing.SupplierBills WHERE TenantId=@TenantId AND Id=@BillId;
  INSERT Purchasing.SupplierItemVersions(TenantId,ItemId) VALUES(@TenantId,@BillId);
  INSERT Purchasing.SupplierItemMovements(TenantId,Id,ItemId,GroupId,EventKind,SourceEventId,RecognitionEventId,PostingDate,Amount,RecordedAtUtc)
    SELECT @TenantId,Id,@BillId,@Group,'Open',SourceEventId,Id,PostingDate,Amount,@Now FROM @Events;
  INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount)
    SELECT @TenantId,JournalId,@Group,Id,JournalId,Ordinal,AccountId,AccountVersion,'SupplierPayable',Amount FROM @Events;
  INSERT Purchasing.SupplierSourceCorrectionMembers(TenantId,Id,CorrectionId,GroupId,Kind,EntityId,JournalId,RecognitionEventId,ItemId,EffectsJson)
    SELECT @TenantId,NEWID(),@Correction,@Group,'Replacement',Id,JournalId,Id,@BillId,
      (SELECT @BillId sourceId,@Revision sourceRevisionId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER) FROM @PostedSides;
  UPDATE link SET ReplacementEventId=successor.Id FROM Purchasing.RecognitionEventCorrections link
    JOIN Purchasing.RecognitionSideEvents original ON original.TenantId=link.TenantId AND original.Id=link.OriginalEventId
    JOIN Purchasing.RecognitionCorrectionGroups correction ON correction.TenantId=link.TenantId AND correction.Id=link.CorrectionGroupId
    CROSS APPLY OPENJSON(@Plan,'$.units') unit
    JOIN @PostedSides successor ON successor.UnitId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(unit.value,'$.binding.successorUnitId'))
    WHERE link.TenantId=@TenantId AND original.Side='Invoice' AND original.UnitId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(unit.value,'$.unitId'))
      AND correction.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(unit.value,'$.binding.correctionGroupId')) AND correction.RecordedAtUtc=@Now;
END;
