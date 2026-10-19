-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.ValidateSupplierCashCorrectionGroup
  @TenantId uniqueidentifier,@GroupId uniqueidentifier,@OwnerRequestId uniqueidentifier
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Plan nvarchar(max),@Actor uniqueidentifier,@Now datetimeoffset;
  SELECT @Plan=JSON_QUERY(receipt.ResultJson,'$.plan'),@Actor=receipt.ActorId,@Now=receipt.RecordedAtUtc
    FROM Purchasing.SupplierFinancialReceipts receipt JOIN Purchasing.SupplierFinancialGroups financialGroup
      ON financialGroup.TenantId=receipt.TenantId AND financialGroup.Id=receipt.GroupId
      AND ((receipt.Operation='CorrectSupplierRefund' AND financialGroup.Operation='CorrectRefund')
        OR (receipt.Operation='CorrectSupplierRefundRepayment' AND financialGroup.Operation='CorrectRepayment'))
      AND financialGroup.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(receipt.CanonicalInput,'$.sourceId'))
      AND financialGroup.RecordedAtUtc=receipt.RecordedAtUtc
    WHERE receipt.TenantId=@TenantId AND receipt.GroupId=@GroupId AND receipt.RequestId=@OwnerRequestId
      AND JSON_VALUE(receipt.ResultJson,'$.state')='pending';
  IF @@TRANCOUNT=0 OR @Plan IS NULL OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51004,'An authentic pending cash correction is required.',1;
  IF EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrections correction
    CROSS APPLY OPENJSON(@Plan,'$.sources') source
    WHERE correction.TenantId=@TenantId AND correction.GroupId=@GroupId
      AND correction.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.sourceId'))
      AND (correction.SourceKind<>JSON_VALUE(source.value,'$.sourceKind') OR correction.OriginalCashSourceId<>correction.SourceId
        OR NOT EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrectionMembers member
          JOIN Accounting.CorrectionGroups inverse ON inverse.TenantId=member.TenantId AND inverse.ReversalJournalId=member.JournalId
            AND inverse.OriginalJournalId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.journalId'))
          WHERE member.TenantId=correction.TenantId AND member.GroupId=@GroupId AND member.CorrectionId=correction.Id
            AND member.Kind='ReverseSource' AND member.EntityId=correction.SourceId AND inverse.ActorId=@Actor AND inverse.RecordedAtUtc=@Now)
        OR (correction.ReplacementSourceId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierCashSources cash
          CROSS APPLY Purchasing.SupplierCashControl(@TenantId,cash.Id) control
          JOIN Purchasing.SupplierSourceCorrectionMembers member ON member.TenantId=cash.TenantId AND member.CorrectionId=correction.Id
            AND member.GroupId=@GroupId AND member.Kind='Replacement' AND member.EntityId=cash.Id AND member.JournalId=control.JournalId
            AND CONVERT(varbinary(max),member.EffectsJson)=CONVERT(varbinary(max),JSON_QUERY(source.value,'$.replacement'))
          WHERE cash.TenantId=correction.TenantId AND cash.Id=correction.ReplacementSourceId
            AND cash.RevisionId=correction.ReplacementSourceRevisionId AND cash.Kind=correction.SourceKind
            AND cash.GroupId=@GroupId AND cash.ActorId=@Actor AND cash.RecordedAtUtc=@Now))))
    THROW 51004,'A complete cash inverse or actual declared successor is missing.',1;
  IF EXISTS(SELECT 1 FROM OPENJSON(@Plan,'$.reverseReleases') released WHERE NOT EXISTS(
    SELECT 1 FROM Purchasing.SupplierSourceCorrectionMembers member JOIN Accounting.CorrectionGroups inverse
      ON inverse.TenantId=member.TenantId AND inverse.OriginalJournalId=member.EntityId AND inverse.ReversalJournalId=member.JournalId
    WHERE member.TenantId=@TenantId AND member.GroupId=@GroupId AND member.Kind='ReverseSource'
      AND member.EntityId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(released.value,'$.journalId'))
      AND inverse.ActorId=@Actor AND inverse.RecordedAtUtc=@Now))
    THROW 51004,'A planned release opening was not completely inverted.',1;
  -- Every cash-bearing member is either an exact original inverse or a proven genuine successor.
  IF EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrectionMembers member
    JOIN Accounting.JournalLines cash ON cash.TenantId=member.TenantId AND cash.JournalId=member.JournalId AND cash.AccountPurpose IN('Bank','Cash','CardLiability')
    WHERE member.TenantId=@TenantId AND member.GroupId=@GroupId AND NOT(
      (member.Kind='ReverseSource' AND EXISTS(SELECT 1 FROM Accounting.CorrectionGroups inverse
        JOIN Accounting.JournalLines original ON original.TenantId=inverse.TenantId AND original.JournalId=inverse.OriginalJournalId
          AND original.Ordinal=cash.Ordinal AND original.AccountId=cash.AccountId AND original.AccountVersion=cash.AccountVersion
          AND original.AccountCode=cash.AccountCode AND original.AccountName=cash.AccountName
          AND original.AccountType=cash.AccountType AND original.AccountPurpose=cash.AccountPurpose
          AND original.Debit=cash.Credit AND original.Credit=cash.Debit
        JOIN OPENJSON(@Plan,'$.sources') source ON TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.journalId'))=inverse.OriginalJournalId
          AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.sourceId'))=member.EntityId
        WHERE inverse.TenantId=member.TenantId AND inverse.ReversalJournalId=member.JournalId AND inverse.ActorId=@Actor AND inverse.RecordedAtUtc=@Now))
      OR (member.Kind='Replacement' AND EXISTS(SELECT 1 FROM Purchasing.SupplierCashSources source
        CROSS APPLY Purchasing.SupplierCashControl(@TenantId,source.Id) control
        JOIN Purchasing.SupplierSourceCorrections correction ON correction.TenantId=source.TenantId AND correction.GroupId=source.GroupId
          AND correction.ReplacementSourceId=source.Id AND correction.SourceKind=source.Kind
        WHERE source.TenantId=member.TenantId AND source.Id=member.EntityId AND source.GroupId=@GroupId
          AND correction.Id=member.CorrectionId AND control.JournalId=member.JournalId))))
    THROW 51004,'A cash-bearing correction member lacks exact original or actual successor proof.',1;
END;
