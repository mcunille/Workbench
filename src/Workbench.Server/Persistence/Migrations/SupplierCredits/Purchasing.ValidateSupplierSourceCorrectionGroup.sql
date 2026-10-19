-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.ValidateSupplierSourceCorrectionGroup
  @TenantId uniqueidentifier,@GroupId uniqueidentifier,@OwnerRequestId uniqueidentifier
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Complete source validation requires its owner transaction.',1;
  DECLARE @Plan nvarchar(max),@Actor uniqueidentifier,@Now datetimeoffset,@Cash bit=0;
  IF EXISTS(SELECT 1 FROM Purchasing.SupplierFinancialReceipts WHERE TenantId=@TenantId AND GroupId=@GroupId
    AND RequestId=@OwnerRequestId AND Operation IN('CorrectSupplierRefund','CorrectSupplierRefundRepayment'))
  BEGIN
    EXEC Purchasing.ValidateSupplierCashCorrectionGroup @TenantId,@GroupId,@OwnerRequestId;
    SET @Cash=1;
  END;
  SELECT @Plan=JSON_QUERY(ResultJson,'$.plan'),@Actor=ActorId,@Now=RecordedAtUtc FROM Purchasing.SupplierFinancialReceipts
    WHERE TenantId=@TenantId AND RequestId=@OwnerRequestId AND GroupId=@GroupId AND Operation IN('CorrectSupplierCredit','CorrectSupplierBill','CorrectSupplierRefund','CorrectSupplierRefundRepayment') AND JSON_VALUE(ResultJson,'$.state')='pending';
  IF @Plan IS NULL OR (SELECT COUNT(*) FROM OPENJSON(@Plan,'$.sources'))<>(SELECT COUNT(*) FROM Purchasing.SupplierSourceCorrections WHERE TenantId=@TenantId AND GroupId=@GroupId)
    THROW 51004,'Complete pending source correction ownership is unavailable.',1;
  DECLARE @Expected TABLE(ItemId uniqueidentifier,PostingDate date,Amount decimal(38,4));
  INSERT @Expected SELECT ItemId,PostingDate,SUM(CONVERT(decimal(38,4),Amount)) FROM OPENJSON(@Plan,'$.effects')
    WITH(ItemId uniqueidentifier '$.itemId',PostingDate date '$.postingDate',Amount decimal(28,4) '$.amount')
    WHERE ItemId IS NOT NULL GROUP BY ItemId,PostingDate;
  DECLARE @Actual TABLE(ItemId uniqueidentifier,PostingDate date,Amount decimal(38,4));
  INSERT @Actual SELECT ItemId,PostingDate,SUM(CONVERT(decimal(38,4),Amount)) FROM Purchasing.SupplierItemMovements
    WHERE TenantId=@TenantId AND GroupId=@GroupId GROUP BY ItemId,PostingDate;
  IF EXISTS(SELECT * FROM @Expected EXCEPT SELECT * FROM @Actual)
    OR EXISTS(SELECT * FROM @Actual EXCEPT SELECT * FROM @Expected)
    THROW 51004,'The produced item timeline differs from the complete approved proposal.',1;
  IF EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.recognitionAdjustments') expected WHERE NOT EXISTS(
      SELECT 1 FROM Purchasing.SupplierSourceCorrectionMembers member WHERE member.TenantId=@TenantId AND member.GroupId=@GroupId
        AND member.Kind='RecognitionAdjustment' AND CONVERT(varbinary(max),member.EffectsJson)=CONVERT(varbinary(max),expected.value)))
    OR (SELECT COUNT(*) FROM OPENJSON(@Plan,'$.recognitionAdjustments'))<>(SELECT COUNT(*) FROM Purchasing.SupplierSourceCorrectionMembers
      WHERE TenantId=@TenantId AND GroupId=@GroupId AND Kind='RecognitionAdjustment')
    THROW 51004,'The monetary recognition dependency proposal is incomplete.',1;
  IF EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.reverseAdjustments') expected WHERE NOT EXISTS(
      SELECT 1 FROM Purchasing.SupplierSourceCorrectionMembers member JOIN Accounting.CorrectionGroups correction
        ON correction.TenantId=member.TenantId AND correction.OriginalJournalId=member.EntityId AND correction.ReversalJournalId=member.JournalId
      WHERE member.TenantId=@TenantId AND member.GroupId=@GroupId AND member.Kind='ReverseSource'
        AND member.EntityId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(expected.value,'$.journalId'))
        AND CONVERT(varbinary(max),member.EffectsJson)=CONVERT(varbinary(max),expected.value)
        AND correction.ActorId=@Actor AND correction.RecordedAtUtc=@Now))
    THROW 51004,'An earlier monetary recognition adjustment was not completely inverted.',1;
  IF EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrections correction WHERE correction.TenantId=@TenantId AND correction.GroupId=@GroupId
    AND (correction.ActorId<>@Actor OR correction.RecordedAtUtc<>@Now
      OR (correction.SourceKind IN('SupplierCredit','SupplierBill') AND COALESCE((SELECT SUM(CONVERT(decimal(38,4),Amount)) FROM Purchasing.SupplierItemMovements movement
        WHERE movement.TenantId=correction.TenantId AND movement.ItemId=correction.SourceId),0)<>0)
      OR NOT EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrectionMembers member WHERE member.TenantId=correction.TenantId
        AND member.CorrectionId=correction.Id AND member.GroupId=correction.GroupId AND member.Kind='ReverseSource' AND member.EntityId=correction.SourceId)
      OR (correction.ReplacementSourceId IS NOT NULL AND correction.SourceKind='SupplierCredit' AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierCredits replacement
        CROSS APPLY Purchasing.SupplierCreditControl(@TenantId,replacement.Id) control
        WHERE replacement.TenantId=correction.TenantId AND replacement.Id=correction.ReplacementSourceId
          AND replacement.RevisionId=correction.ReplacementSourceRevisionId AND replacement.GroupId=correction.GroupId
          AND replacement.SupplierId=correction.SupplierId AND replacement.PurchaseOrderId=correction.PurchaseOrderId AND replacement.Currency=correction.Currency
          AND replacement.ActorId=correction.ActorId AND replacement.RecordedAtUtc=correction.RecordedAtUtc))
      OR (correction.ReplacementSourceId IS NOT NULL AND correction.SourceKind='SupplierBill' AND NOT EXISTS(
        SELECT 1 FROM Purchasing.SupplierBillPostings replacement CROSS APPLY Purchasing.SupplierItemControl(@TenantId,replacement.BillId) control
        WHERE replacement.TenantId=correction.TenantId AND replacement.BillId=correction.ReplacementSourceId
          AND replacement.RevisionId=correction.ReplacementSourceRevisionId AND replacement.RecordedAtUtc=@Now
          AND control.AccountPurpose='SupplierPayable'
          AND EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrectionMembers successor WHERE successor.TenantId=replacement.TenantId
            AND successor.CorrectionId=correction.Id AND successor.GroupId=@GroupId AND successor.Kind='Replacement' AND successor.ItemId=replacement.BillId)))))
    THROW 51004,'A source inverse or declared successor is incomplete.',1;
  IF EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.recognitionSuccessors') expected WHERE NOT EXISTS(
    SELECT 1 FROM Purchasing.RecognitionSideEvents actual
    JOIN Purchasing.RecognitionEventCorrections link ON link.TenantId=actual.TenantId AND link.ReplacementEventId=actual.Id
      AND link.OriginalEventId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(expected.value,'$.originalEventId'))
    JOIN Purchasing.RecognitionCorrectionGroups correction ON correction.TenantId=link.TenantId AND correction.Id=link.CorrectionGroupId
      AND correction.ReplacementUnitId=actual.UnitId AND correction.ActorId=@Actor AND correction.RecordedAtUtc=@Now
    JOIN Purchasing.SupplierSourceCorrectionMembers member ON member.TenantId=actual.TenantId AND member.GroupId=@GroupId
      AND member.Kind='Replacement' AND member.EntityId=actual.Id AND member.RecognitionEventId=actual.Id
    JOIN Purchasing.RecognitionGroupReceipts receipt ON receipt.TenantId=actual.TenantId AND receipt.ActorId=@Actor
      AND receipt.RecordedAtUtc=@Now AND receipt.CommandKind='Post'
      AND CONVERT(varbinary(max),receipt.CanonicalInput)=CONVERT(varbinary(max),JSON_QUERY(expected.value,'$.command'))
      AND receipt.InputSha256=HASHBYTES('SHA2_256',CONVERT(varbinary(max),receipt.CanonicalInput))
      AND CONVERT(varbinary(max),receipt.ResultJson)=CONVERT(varbinary(max),member.EffectsJson)
    CROSS APPLY OPENJSON(receipt.ResultJson,'$.eventIds') produced
    WHERE actual.TenantId=@TenantId AND actual.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(expected.value,'$.eventId'))
      AND actual.Id=TRY_CONVERT(uniqueidentifier,produced.value)
      AND actual.UnitId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(expected.value,'$.unitId'))
      AND actual.ActorId=@Actor AND actual.RecordedAtUtc=@Now
      AND ((actual.JournalId=member.JournalId) OR (actual.JournalId IS NULL AND member.JournalId IS NULL))))
    THROW 51004,'A declared genuine receipt successor is incomplete.',1;
  IF EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.applications') expected WHERE NOT EXISTS(
      SELECT 1 FROM Purchasing.SupplierSourceCorrectionMembers member WHERE member.TenantId=@TenantId AND member.GroupId=@GroupId
        AND member.Kind='ReverseApplication' AND member.EntityId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(expected.value,'$.applicationId'))))
    OR EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.settlements') expected WHERE NOT EXISTS(
      SELECT 1 FROM Purchasing.SupplierCashSettlementReleases released WHERE released.TenantId=@TenantId AND released.GroupId=@GroupId
        AND released.SettlementId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(expected.value,'$.settlementId'))))
    OR (SELECT COUNT(*) FROM OPENJSON(@Plan,'$.reapplications'))<>(SELECT COUNT(*) FROM Purchasing.SupplierApplications WHERE TenantId=@TenantId AND GroupId=@GroupId)
    THROW 51004,'A complete correction participant is missing.',1;
  IF EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrectionMembers member JOIN Accounting.JournalEntries journal
      ON journal.TenantId=member.TenantId AND journal.Id=member.JournalId
    JOIN Accounting.SourceEvents sourceEvent ON sourceEvent.TenantId=journal.TenantId AND sourceEvent.Id=journal.SourceEventId
    WHERE member.TenantId=@TenantId AND member.GroupId=@GroupId AND
      (journal.RecordedAtUtc<>@Now OR sourceEvent.RecordedAtUtc<>@Now OR journal.ActorId<>@Actor OR sourceEvent.ActorId<>@Actor))
    OR EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrectionMembers member JOIN Accounting.JournalLines line
      ON line.TenantId=member.TenantId AND line.JournalId=member.JournalId
      WHERE member.TenantId=@TenantId AND member.GroupId=@GroupId AND @Cash=0 AND line.AccountPurpose IN('Bank','Cash','CardLiability'))
    THROW 51004,'Source correction changed actual cash or its common recorded instant.',1;
  IF EXISTS(SELECT 1 FROM Purchasing.SupplierItemMovements movement WHERE movement.TenantId=@TenantId AND movement.GroupId=@GroupId
    AND (movement.RecordedAtUtc<>@Now OR NOT EXISTS(SELECT 1 FROM Purchasing.SupplierControlAttributions attribution
      WHERE attribution.TenantId=movement.TenantId AND attribution.GroupId=movement.GroupId AND attribution.MovementId=movement.Id AND attribution.Amount=movement.Amount)))
    THROW 51004,'A source correction movement has incomplete attribution.',1;
  IF EXISTS(SELECT attribution.JournalId,attribution.Ordinal,attribution.AccountId,attribution.AccountVersion,SUM(CONVERT(decimal(38,4),attribution.Amount))
      FROM Purchasing.SupplierControlAttributions attribution WHERE attribution.TenantId=@TenantId AND attribution.GroupId=@GroupId
      GROUP BY attribution.JournalId,attribution.Ordinal,attribution.AccountId,attribution.AccountVersion
    EXCEPT SELECT line.JournalId,line.Ordinal,line.AccountId,line.AccountVersion,
      CASE WHEN line.AccountType='Asset' THEN line.Debit-line.Credit ELSE line.Credit-line.Debit END
      FROM Accounting.JournalLines line WHERE line.TenantId=@TenantId
        AND line.AccountPurpose IN('SupplierAdvance','SupplierCreditReceivable','SupplierPayable','SupplierRefundClearing')
        AND EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrectionMembers member WHERE member.TenantId=line.TenantId AND member.GroupId=@GroupId AND member.JournalId=line.JournalId))
    THROW 51004,'The correction control attribution does not match its actual journal lines.',1;
END;
