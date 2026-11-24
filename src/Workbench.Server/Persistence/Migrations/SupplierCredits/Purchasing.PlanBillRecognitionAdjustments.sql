-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PlanBillRecognitionAdjustments
  @TenantId uniqueidentifier,@Units nvarchar(max),@PostingDate date,@Adjustments nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Monetary recognition planning requires its bill owner transaction.',1;
  DECLARE @Affected TABLE(Id uniqueidentifier PRIMARY KEY);
  INSERT @Affected SELECT member.Id FROM Purchasing.SupplierSourceCorrectionMembers member
    WHERE member.TenantId=@TenantId AND member.Kind='RecognitionAdjustment' AND member.JournalId IS NOT NULL
      AND EXISTS(SELECT 1 FROM OPENJSON(@Units) unit CROSS APPLY OPENJSON(unit.value,'$.originals') original
        WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(original.value,'$.eventId'))=member.RecognitionEventId)
      AND NOT EXISTS(SELECT 1 FROM Accounting.CorrectionGroups corrected WHERE corrected.TenantId=member.TenantId AND corrected.OriginalJournalId=member.JournalId);
  DECLARE @Resolved TABLE(MemberId uniqueidentifier PRIMARY KEY,JournalId uniqueidentifier,SourceEventId uniqueidentifier,
    RecognitionEventId uniqueidentifier,CreditId uniqueidentifier,CreditVersion varchar(18),SnapshotHash varchar(64),Amount decimal(28,4));
  INSERT @Resolved SELECT member.Id,journal.Id,sourceEvent.Id,member.RecognitionEventId,correction.SourceId,
      CONVERT(varchar(18),CONVERT(binary(8),version.RowVersion),1),CONVERT(varchar(64),sourceEvent.SnapshotSha256,2),
      TRY_CONVERT(decimal(28,4),JSON_VALUE(member.EffectsJson,'$.amount'))
    FROM @Affected affected JOIN Purchasing.SupplierSourceCorrectionMembers member ON member.TenantId=@TenantId AND member.Id=affected.Id
    JOIN Purchasing.SupplierSourceCorrections correction ON correction.TenantId=member.TenantId AND correction.Id=member.CorrectionId
      AND correction.SourceKind='SupplierCredit' AND correction.GroupId=member.GroupId
    JOIN Purchasing.SupplierCreditVersions version WITH(UPDLOCK,HOLDLOCK) ON version.TenantId=correction.TenantId AND version.CreditId=correction.SourceId
    CROSS APPLY Purchasing.SupplierCreditControl(@TenantId,correction.SourceId) creditControl
    JOIN Purchasing.SupplierFinancialGroups financialGroup ON financialGroup.TenantId=member.TenantId AND financialGroup.Id=member.GroupId
      AND financialGroup.RecordedAtUtc=correction.RecordedAtUtc
    JOIN Purchasing.SupplierFinancialReceipts receipt ON receipt.TenantId=financialGroup.TenantId AND receipt.GroupId=financialGroup.Id
      AND receipt.ActorId=correction.ActorId AND receipt.RecordedAtUtc=correction.RecordedAtUtc AND JSON_VALUE(receipt.ResultJson,'$.state') IS NULL
      AND (receipt.Operation='CorrectSupplierCredit' AND financialGroup.Operation='CorrectCredit'
        OR receipt.Operation='CorrectSupplierBill' AND financialGroup.Operation='CorrectBill')
    JOIN Accounting.JournalEntries journal ON journal.TenantId=member.TenantId AND journal.Id=member.JournalId
      AND journal.ActorId=correction.ActorId AND journal.RecordedAtUtc=correction.RecordedAtUtc AND journal.PostingDate<=@PostingDate
    JOIN Accounting.SourceEvents sourceEvent ON sourceEvent.TenantId=journal.TenantId AND sourceEvent.Id=journal.SourceEventId
      AND sourceEvent.SourceKind='SupplierRecognitionCreditAdjustment' AND sourceEvent.EventKind='Adjust'
      AND sourceEvent.SourceRevision=correction.Id AND sourceEvent.SourceId=member.EntityId AND sourceEvent.ActorId=journal.ActorId
      AND sourceEvent.RecordedAtUtc=journal.RecordedAtUtc AND sourceEvent.PostingDate=journal.PostingDate
      AND sourceEvent.SnapshotSha256=HASHBYTES('SHA2_256',CONVERT(varbinary(max),sourceEvent.SnapshotJson))
      AND CONVERT(varbinary(max),JSON_QUERY(sourceEvent.SnapshotJson,'$.adjustment'))=CONVERT(varbinary(max),member.EffectsJson)
      AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(sourceEvent.SnapshotJson,'$.groupId'))=member.GroupId
      AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(member.EffectsJson,'$.sourceId'))=correction.SourceId
      AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(member.EffectsJson,'$.recognitionEventId'))=member.RecognitionEventId
    JOIN Accounting.PostingReceipts posting ON posting.TenantId=journal.TenantId AND posting.JournalId=journal.Id AND posting.SourceEventId=sourceEvent.Id
      AND posting.ActorId=journal.ActorId AND posting.RecordedAtUtc=journal.RecordedAtUtc AND posting.SourceCommandKind='Supplier.RecognitionCreditAdjustment'
      AND CONVERT(varbinary(max),posting.CanonicalInput)=CONVERT(varbinary(max),sourceEvent.SnapshotJson);
  IF (SELECT COUNT(*) FROM @Resolved)<>(SELECT COUNT(*) FROM @Affected)
    OR EXISTS(SELECT 1 FROM @Resolved WHERE Amount IS NULL OR Amount=0)
    OR EXISTS(SELECT 1 FROM @Resolved adjustment WHERE (SELECT COUNT(*) FROM Accounting.JournalLines line
      WHERE line.TenantId=@TenantId AND line.JournalId=adjustment.JournalId AND line.AccountPurpose='General'
        AND (line.Ordinal=1 AND line.AccountType='Expense'
          AND line.Debit=CASE WHEN adjustment.Amount>0 THEN adjustment.Amount ELSE 0 END
          AND line.Credit=CASE WHEN adjustment.Amount<0 THEN -adjustment.Amount ELSE 0 END
          OR line.Ordinal=2 AND line.AccountType='Asset'
          AND line.Credit=CASE WHEN adjustment.Amount>0 THEN adjustment.Amount ELSE 0 END
          AND line.Debit=CASE WHEN adjustment.Amount<0 THEN -adjustment.Amount ELSE 0 END))<>2
      OR (SELECT COUNT(*) FROM Accounting.JournalLines line WHERE line.TenantId=@TenantId AND line.JournalId=adjustment.JournalId)<>2)
    THROW 51004,'An earlier monetary recognition adjustment has an incomplete or later source owner.',1;
  SET @Adjustments=(SELECT MemberId memberId,JournalId journalId,SourceEventId sourceEventId,RecognitionEventId recognitionEventId,
    CreditId creditId,CreditVersion creditVersion,SnapshotHash snapshotSha256,CONVERT(nvarchar(60),Amount) amount,@PostingDate postingDate
    FROM @Resolved ORDER BY JournalId FOR JSON PATH);
END;
